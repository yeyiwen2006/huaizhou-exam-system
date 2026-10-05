using System.Collections.Concurrent;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Exam.IntegrationTests.Cli;

/// <summary>
/// C 家族业务 CLI 的真端到端证据：真 CLI 进程 + 环回宿主 + 合成数据，覆盖考试目录、名册、
/// 试场与设备、监测四族命令的正向路径（用法错误仍由 <see cref="CliBoundaryTests"/> 覆盖）。
/// 只用合成数据，不连真实数据库或真实试卷。
/// </summary>
public sealed class AdministrationCliTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static readonly DateTimeOffset Start = new(2027, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Exams_CatalogCommandsUseTheVersionedApi()
    {
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/exams/exam-1", () => self.Envelope(Detail()));
            app.MapPost("/api/v1/exams", () => self.Envelope(Detail()));
            app.MapPut("/api/v1/exams/exam-1/settings", () => self.Envelope(Detail() with { Summary = Summary() with { SettingsVersion = 4 } }));
            app.MapPut("/api/v1/exams/exam-1/visibility", () => self.Envelope(Summary(visible: true)));
        });

        var list = await CliProcess.RunAsync("exams", "list", "--server", host.Address);
        Assert.Equal(0, list.Exit);
        Assert.Equal("合成考试", Assert.Single(JsonSerializer.Deserialize<ExamSummary[]>(list.Out, Json)!).Name);

        var show = await CliProcess.RunAsync("exams", "show", "合成考试", "--server", host.Address);
        Assert.Equal(0, show.Exit);
        var detail = JsonSerializer.Deserialize<ExamDetail>(show.Out, Json)!;
        Assert.Equal("合成数学", Assert.Single(detail.Settings.Subjects).Name);
        Assert.NotEmpty(detail.InstructionLines);

        var createInput = CliProcess.WriteInput(JsonSerializer.Serialize(
            new CreateExamRequest("", "合成新考试", new ScheduleWindow(Start, Start.AddHours(2)), Settings()), Json));
        try
        {
            var create = await CliProcess.RunAsync("exams", "create", "--input", createInput, "--server", host.Address);
            Assert.Equal(0, create.Exit);
            Assert.Equal("exam-1", JsonSerializer.Deserialize<ExamDetail>(create.Out, Json)!.Summary.ExamId);
        }
        finally { File.Delete(createInput); }

        var updateInput = CliProcess.WriteInput(JsonSerializer.Serialize(
            new UpdateExamSettingsRequest("", 3, "合成改名考试", Settings()), Json));
        try
        {
            var update = await CliProcess.RunAsync("exams", "update", "合成考试", "--input", updateInput, "--server", host.Address);
            Assert.Equal(0, update.Exit);
            Assert.Equal(4, JsonSerializer.Deserialize<ExamDetail>(update.Out, Json)!.Summary.SettingsVersion);
        }
        finally { File.Delete(updateInput); }

        var visibility = await CliProcess.RunAsync("exams", "visibility", "合成考试", "on", "--server", host.Address);
        Assert.Equal(0, visibility.Exit);
        Assert.True(JsonSerializer.Deserialize<ExamSummary>(visibility.Out, Json)!.VisibleToStudents);
        Assert.Contains(host.Calls, call => call == "POST /api/v1/exams");
        Assert.Contains(host.Calls, call => call == "PUT /api/v1/exams/exam-1/settings");
        Assert.Contains(host.Calls, call => call == "PUT /api/v1/exams/exam-1/visibility");
    }

    [Fact]
    public async Task Roster_CommandsUseTheAuthoritativeRosterApi()
    {
        var snapshot = new CandidateRosterSnapshot("exam-1", 4,
            [new CandidateRosterEntry("cand-1", "合成考生", "001", "00001")]);
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/exams/exam-1/roster", () => self.Envelope(snapshot));
            app.MapPost("/api/v1/exams/exam-1/roster/import", () => self.Envelope(
                new RosterImportResult(true, 1, 0, snapshot with { Version = 5 }, [])));
            app.MapPut("/api/v1/exams/exam-1/roster/cand-1", () => self.Envelope(snapshot with { Version = 5 }));
            app.MapDelete("/api/v1/exams/exam-1/roster/cand-1", () => self.Envelope(snapshot with { Version = 5 }));
        });

        var list = await CliProcess.RunAsync("roster", "list", "合成考试", "--server", host.Address);
        Assert.Equal(0, list.Exit);
        Assert.Equal("00001", Assert.Single(JsonSerializer.Deserialize<CandidateRosterSnapshot>(list.Out, Json)!.Entries).AdmissionNo);

        var importInput = CliProcess.WriteInput(
            """{"rows":[{"rowNumber":1,"name":"合成考生二","classText":"002","admissionNo":"00002"}]}""");
        try
        {
            var import = await CliProcess.RunAsync("roster", "import", "合成考试", "--input", importInput, "--server", host.Address);
            Assert.Equal(0, import.Exit);
            var applied = JsonSerializer.Deserialize<RosterImportResult>(import.Out, Json)!;
            Assert.True(applied.Applied);
            Assert.Equal(5, applied.Snapshot.Version);
        }
        finally { File.Delete(importInput); }

        var updateInput = CliProcess.WriteInput(
            """{"entry":{"rowNumber":1,"name":"合成考生改","classText":"001","admissionNo":"00003"}}""");
        try
        {
            var update = await CliProcess.RunAsync("roster", "update", "合成考试", "00001", "--input", updateInput, "--server", host.Address);
            Assert.Equal(0, update.Exit);
            Assert.Equal(5, JsonSerializer.Deserialize<CandidateRosterSnapshot>(update.Out, Json)!.Version);
        }
        finally { File.Delete(updateInput); }

        var delete = await CliProcess.RunAsync("roster", "delete", "合成考试", "00001", "--server", host.Address);
        Assert.Equal(0, delete.Exit);
        Assert.Contains(host.Calls, call => call == "PUT /api/v1/exams/exam-1/roster/cand-1");
        Assert.Contains(host.Calls, call => call == "DELETE /api/v1/exams/exam-1/roster/cand-1");
    }

    [Fact]
    public async Task Roster_RejectedImportIsNotReportedAsSuccess()
    {
        var snapshot = new CandidateRosterSnapshot("exam-1", 4,
            [new CandidateRosterEntry("cand-1", "合成考生", "001", "00001")]);
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/exams/exam-1/roster", () => self.Envelope(snapshot));
            app.MapPost("/api/v1/exams/exam-1/roster/import", () => self.Envelope(
                new RosterImportResult(false, 0, 0, snapshot, [new RosterRowIssue(1, "admissionNo", "准考证号与已有记录冲突")])));
        });

        var input = CliProcess.WriteInput(
            """{"rows":[{"rowNumber":1,"name":"合成考生二","classText":"002","admissionNo":"00001"}]}""");
        try
        {
            var import = await CliProcess.RunAsync("roster", "import", "合成考试", "--input", input, "--server", host.Address);

            // 未写入就必须是非零退出码，且逐行问题要能被脚本读到。
            Assert.Equal(2, import.Exit);
            var result = JsonSerializer.Deserialize<RosterImportResult>(import.Out, Json)!;
            Assert.False(result.Applied);
            Assert.Equal("admissionNo", Assert.Single(result.Issues).Field);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task RoomsAndDevices_CommandsUseTheAdministrationApi()
    {
        var rooms = new[] { new RoomGroup("room-a", "合成试场A", 1, 1), new RoomGroup("room-b", "合成试场B", 1, 0) };
        var devices = new DeviceDirectoryPage(
        [
            new DeviceDirectoryEntry("device-1", "20270115001", "room-a", "合成试场A", ActivationStatus.Active,
                DeviceConnectionState.Online, "合成考生甲", "001", "00001", "exam-1", "合成考试", "正在答题", null,
                Start.AddMinutes(-1), 1),
        ], null, Start);
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/rooms", () => self.Envelope(rooms));
            app.MapPost("/api/v1/rooms", () => self.Envelope(new RoomGroup("room-c", "合成试场C", 1, 0)));
            app.MapPut("/api/v1/rooms/room-a", () => self.Envelope(new RoomGroup("room-a", "合成试场A改", 2, 1)));
            app.MapDelete("/api/v1/rooms/room-b", () => self.Envelope(new DeletedRoomGroup("room-b", true)));
            app.MapPost("/api/v1/rooms/move-devices", () => self.Envelope(new MoveDevicesResult(1, ["20270115001"])));
            app.MapGet("/api/v1/devices", () => self.Envelope(devices));
            app.MapGet("/api/v1/devices/20270115001/room-history", () => self.Envelope(new[]
            {
                new DeviceRoomHistory("20270115001", null, "未分组", "room-a", "合成试场A", "synthetic-operator",
                    "合成转组原因", Start),
            }));
        });

        var list = await CliProcess.RunAsync("rooms", "list", "--server", host.Address);
        Assert.Equal(0, list.Exit);
        Assert.Equal(2, JsonSerializer.Deserialize<RoomGroup[]>(list.Out, Json)!.Length);

        var create = await CliProcess.RunAsync("rooms", "create", "合成试场C", "--server", host.Address);
        Assert.Equal(0, create.Exit);
        Assert.Equal("合成试场C", JsonSerializer.Deserialize<RoomGroup>(create.Out, Json)!.Name);

        var rename = await CliProcess.RunAsync("rooms", "rename", "合成试场A", "合成试场A改", "--server", host.Address);
        Assert.Equal(0, rename.Exit);
        Assert.Equal("合成试场A改", JsonSerializer.Deserialize<RoomGroup>(rename.Out, Json)!.Name);

        var delete = await CliProcess.RunAsync("rooms", "delete", "合成试场B", "--server", host.Address);
        Assert.Equal(0, delete.Exit);
        Assert.True(JsonSerializer.Deserialize<DeletedRoomGroup>(delete.Out, Json)!.Deleted);

        var moveInput = CliProcess.WriteInput(
            """{"clientNumbers":["20270115001"],"reason":"合成转组原因"}""");
        try
        {
            var move = await CliProcess.RunAsync("rooms", "move", "合成试场A", "--input", moveInput, "--server", host.Address);
            Assert.Equal(0, move.Exit);
            Assert.Equal(1, JsonSerializer.Deserialize<MoveDevicesResult>(move.Out, Json)!.Moved);
        }
        finally { File.Delete(moveInput); }

        var devicesList = await CliProcess.RunAsync("devices", "list", "--server", host.Address);
        Assert.Equal(0, devicesList.Exit);
        Assert.Equal("001", Assert.Single(JsonSerializer.Deserialize<DeviceDirectoryEntry[]>(devicesList.Out, Json)!).ClassText);

        var show = await CliProcess.RunAsync("devices", "show", "20270115001", "--server", host.Address);
        Assert.Equal(0, show.Exit);
        Assert.Equal("合成考生甲", JsonSerializer.Deserialize<DeviceDirectoryEntry>(show.Out, Json)!.CandidateName);

        var history = await CliProcess.RunAsync("devices", "history", "20270115001", "--server", host.Address);
        Assert.Equal(0, history.Exit);
        Assert.Equal("合成试场A", Assert.Single(JsonSerializer.Deserialize<DeviceRoomHistory[]>(history.Out, Json)!).ToGroupName);

        // 没有该编号时必须失败且不发转组请求。
        var missing = await CliProcess.RunAsync("devices", "show", "20270115999", "--server", host.Address);
        Assert.NotEqual(0, missing.Exit);
        Assert.Contains(host.Calls, call => call == "POST /api/v1/rooms/move-devices");
    }

    [Fact]
    public async Task Monitor_ShowAndMissingReturnAuthoritativeState()
    {
        var participant = new MonitoringParticipant("part-1", "cand-1", "合成考生甲", "001", "00001",
            "device-1", "20270115001", "room-a", "合成试场A", ParticipationBusinessState.Answering,
            DeviceConnectionState.Online, "正在答题", "1", Start.AddMinutes(-1), 1, 1, 3, 2, 1, 1, "已下载",
            new Dictionary<string, AnswerValue>(StringComparer.Ordinal) { ["1"] = AnswerValue.Selected("A") },
            "未回收", null, null, 0, 0, null, [], []);
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/exams/exam-1/monitoring", () => self.Envelope(
                new MonitoringSnapshot("exam-1", Start, new VersionVector(1, 1, 1), ExamLifecycle.Running, 4, 1, 0,
                    [participant], [participant])));
            app.MapGet("/api/v1/exams/exam-1/uncollected", () => self.Envelope(new[] { participant }));
        });

        var show = await CliProcess.RunAsync("monitor", "show", "合成考试", "--server", host.Address);
        Assert.Equal(0, show.Exit);
        var snapshot = JsonSerializer.Deserialize<MonitoringSnapshot>(show.Out, Json)!;
        Assert.Equal("合成考生甲", Assert.Single(snapshot.Participants).Name);
        Assert.Equal(1, snapshot.ActualCount);
        // 该考生还没有持久化回执，因此同时出现在未回收名单里。
        Assert.Equal("part-1", Assert.Single(snapshot.Uncollected).ParticipationId);

        var missing = await CliProcess.RunAsync("monitor", "missing", "合成考试", "--server", host.Address);
        Assert.Equal(0, missing.Exit);
        Assert.Equal("part-1", Assert.Single(JsonSerializer.Deserialize<MonitoringParticipant[]>(missing.Out, Json)!).ParticipationId);
        Assert.Contains(host.Calls, call => call == "GET /api/v1/exams/exam-1/monitoring");
        Assert.Contains(host.Calls, call => call == "GET /api/v1/exams/exam-1/uncollected");
    }

    private static ExamSettings Settings() => new(
    [
        new ExamSubjectSettings("m", "合成数学", "6",
        [
            new ExamGroupSettings("m-1", "一、单项选择题", "single", 1, 2, "6", false,
                new ManifestOneCorrect("3", "0", "0"), null),
        ]),
    ]);

    private static ExamSummary Summary(bool visible = false) =>
        new("exam-1", "合成考试", Start, Start.AddHours(2), ExamLifecycle.Scheduled, visible, false, 0, 0, 3);

    private static ExamDetail Detail() => new(Summary(), Settings(), 1, ExamSettingsRules.BuildInstructions(Settings()));

    /// <summary>环回宿主：记录真实请求路径，便于断言 CLI 打对了接口。</summary>
    private sealed class CliHost(WebApplication app, ConcurrentQueue<string> calls) : IAsyncDisposable
    {
        public ConcurrentQueue<string> Calls { get; } = calls;
        public string Address => app.Urls.Single();

        public IResult Envelope<T>(T value) => Results.Json(
            new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "synthetic-request", value, null), Json);

        public static async Task<CliHost> StartAsync(Action<CliHost, WebApplication> configure)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var calls = new ConcurrentQueue<string>();
            app.Use(async (context, next) =>
            {
                calls.Enqueue($"{context.Request.Method} {context.Request.Path}");
                await next();
            });
            var host = new CliHost(app, calls);
            configure(host, app);
            await app.StartAsync();
            return host;
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
