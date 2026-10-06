using System.Collections.Concurrent;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Commands;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Operators;
using Exam.Contracts.Participation;
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

        // 没有该编号时必须失败，并且不产生新的转组请求（前面的成功用例已经发出过一次，所以要看增量）。
        var movesBefore = host.Calls.Count(call => call == "POST /api/v1/rooms/move-devices");
        var missing = await CliProcess.RunAsync("devices", "show", "20270115999", "--server", host.Address);
        Assert.NotEqual(0, missing.Exit);
        Assert.Equal(movesBefore, host.Calls.Count(call => call == "POST /api/v1/rooms/move-devices"));
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

    [Fact]
    public async Task Activation_PendingApproveAndRevokeResolveNumbersInsteadOfInternalIds()
    {
        var pending = new ActivationRequestResult("request-1", "device-1", ActivationStatus.Pending, "20261006001", 1, Start);
        var bodies = new ConcurrentQueue<string>();
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/activation/pending", () => self.Envelope(new[] { pending }));
            app.MapGet("/api/v1/rooms", () => self.Envelope(new[] { new RoomGroup("room-a", "合成试场A", 1, 0) }));
            app.MapPost("/api/v1/activation/approve", async (HttpContext context) =>
            {
                bodies.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
                return self.Envelope(new ActivationResult("device-1", "20261006001", new DateOnly(2026, 10, 6),
                    ActivationStatus.Active, "room-a", 1, 1));
            });
            app.MapGet("/api/v1/devices", () => self.Envelope(new DeviceDirectoryPage(
            [
                new DeviceDirectoryEntry("device-1", "20261006001", "room-a", "合成试场A", ActivationStatus.Active,
                    DeviceConnectionState.Online, null, null, null, null, null, "登录", null, Start, 1),
            ], null, Start)));
            app.MapPost("/api/v1/activation/revoke", async (HttpContext context) =>
            {
                bodies.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
                return self.Envelope(new ActivationResult("device-1", "20261006001", new DateOnly(2026, 10, 6),
                    ActivationStatus.Revoked, "room-a", 1, 2));
            });
        });

        var list = await CliProcess.RunAsync("activation", "pending", "--server", host.Address);
        Assert.Equal(0, list.Exit);
        Assert.Equal("20261006001", Assert.Single(JsonSerializer.Deserialize<ActivationRequestResult[]>(list.Out, Json)!).ClientNumber);

        // 批准：按学生端编号解析申请标识，并把试场名称解析成试场标识。
        var approve = await CliProcess.RunAsync("activation", "approve", "20261006001", "合成试场A", "--server", host.Address);
        Assert.Equal(0, approve.Exit);
        Assert.Equal("Active", JsonSerializer.Deserialize<ActivationResult>(approve.Out, Json)!.Status.ToString());
        var approveJson = ReadBody(Assert.Single(bodies));
        Assert.Equal("request-1", approveJson.GetProperty("activationRequestId").GetString());
        Assert.Equal("room-a", approveJson.GetProperty("roomGroupId").GetString());

        // 撤回：按学生端编号解析设备标识，原因从输入给出。
        var revokeInput = CliProcess.WriteInput("""{"reason":"合成撤回原因"}""");
        try
        {
            var revoke = await CliProcess.RunAsync("activation", "revoke", "20261006001", "--input", revokeInput, "--server", host.Address);
            Assert.Equal(0, revoke.Exit);
            Assert.Equal("Revoked", JsonSerializer.Deserialize<ActivationResult>(revoke.Out, Json)!.Status.ToString());
            var revokeBody = bodies.Last();
            // 正文是 JSON：按字段核对（中文会被转义，不能直接搜子串）。
            Assert.Contains("\"deviceId\":\"device-1\"", revokeBody, StringComparison.Ordinal);
            Assert.Equal("合成撤回原因", ReadBody(revokeBody).GetProperty("reason").GetString());
        }
        finally { File.Delete(revokeInput); }
    }

    [Fact]
    public async Task Decisions_KeepResolvesTheConflictDeviceAndSignsWithTheSignedInOperator()
    {
        var conflict = new ConflictDevice("device-b", "20261006009", "第二试场", DeviceConnectionState.Online, Start, false, 2, "交卷确认");
        var participant = Pres14_Participant(conflicts: [conflict]);
        var bodies = new ConcurrentQueue<string>();
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/operators/me", () => self.Envelope(
                new OperatorAccount("synthetic-operator", "合成考务", OperatorStatus.Active, true, Start, null)));
            app.MapGet("/api/v1/exams/exam-1/monitoring", () => self.Envelope(
                MonitoringSnapshot(participant)));
            app.MapPost("/api/v1/participation/device-decisions", async (HttpContext context) =>
            {
                bodies.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
                return self.Envelope(new DeviceDecisionResult("part-1", "device-b", "device-a", 1, 2,
                    "synthetic-operator", "合成裁决原因", Start));
            });
        });

        var input = CliProcess.WriteInput("""{"device":"20261006009","reason":"合成裁决原因"}""");
        try
        {
            var keep = await CliProcess.RunAsync("decisions", "keep", "合成考试", "20261006001",
                "--input", input, "--server", host.Address);

            Assert.Equal(0, keep.Exit);
            var body = ReadBody(Assert.Single(bodies));
            // 保留的是公开编号解析出的内部设备标识；版本取参试版本；操作者是登录身份而不是请求里自填。
            Assert.Equal("device-b", body.GetProperty("selectedDeviceId").GetString());
            Assert.Equal(3, body.GetProperty("expectedVersion").GetInt64());
            Assert.Equal("synthetic-operator", body.GetProperty("operatorId").GetString());
            Assert.Equal("合成裁决原因", body.GetProperty("reason").GetString());
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task Commands_ResendAndReuploadPickTheResendableCommandForTheCandidate()
    {
        var paths = new ConcurrentQueue<string>();
        var bodies = new ConcurrentQueue<string>();
        var end = new MonitoringCommand("end-1", CommandEventType.End, 3, "待确认", Start, null, true);
        var participant = Pres14_Participant(commands: [end]);
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/exams/exam-1/monitoring", () => self.Envelope(MonitoringSnapshot(participant)));
            app.MapPost("/api/v1/commands/{eventId}/resend", async (string eventId, HttpContext context) =>
            {
                paths.Enqueue(eventId);
                bodies.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
                return self.Envelope(new CommandEventView("end-1", "exam-1", "part-1", 1, CommandEventType.End, 3,
                    "device-a", 1, null, Start, false, 2));
            });
        });

        var resend = await CliProcess.RunAsync("commands", "resend", "合成考试", "20261006001", "end", "--server", host.Address);
        Assert.Equal(0, resend.Exit);
        Assert.Equal("end-1", Assert.Single(paths));
        var resendBody = ReadBody(bodies.Last());
        Assert.Equal("part-1", resendBody.GetProperty("participationId").GetString());
        Assert.Equal(1, resendBody.GetProperty("authorizationVersion").GetInt64());
        Assert.Equal(1, resendBody.GetProperty("entryVersion").GetInt64());

        // 未回收的考生可以请求补传；已回收的必须被拒绝并说明原因，且不再发请求。
        var reupload = await CliProcess.RunAsync("commands", "reupload", "合成考试", "20261006001", "--server", host.Address);
        Assert.Equal(0, reupload.Exit);
        Assert.Equal(2, paths.Count);

        await using var collectedHost = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/exams/exam-1/monitoring", () => self.Envelope(
                MonitoringSnapshot(Pres14_Participant(receiptId: "receipt-1"))));
        });
        var collected = await CliProcess.RunAsync("commands", "reupload", "合成考试", "20261006001", "--server", collectedHost.Address);
        // 已回收的考生必须被拒绝：退出码 2；具体文案由统一错误层给出（不能断言命令自己的文案）。
        Assert.Equal(2, collected.Exit);
        Assert.Empty(collected.Out);
    }

    [Fact]
    public async Task MonitorEventsSubscribesToConflictCollectionAndFailureNotifications()
    {
        await using var host = await CliHost.StartAsync((self, app) =>
        {
            app.MapGet("/api/v1/exams", () => self.Envelope(new[] { Summary() }));
            app.MapGet("/api/v1/submissions/collection/events", () => self.Envelope(Array.Empty<CollectionCompletedNotification>()));
            app.MapGet("/api/v1/exams/exam-1/monitoring", () => self.Envelope(MonitoringSnapshot(
                Pres14_Participant(conflicts: [new ConflictDevice("device-b", "20261006009", "第二试场",
                    DeviceConnectionState.Online, Start, false, 2, "交卷确认")]),
                Pres14_Participant(participationId: "part-2", candidateId: "cand-2", clientNumber: "20261006002",
                    downloadState: "失败"))));
        });

        var watch = await CliProcess.RunAsync("monitor", "events", "合成考试", "--server", host.Address);

        Assert.Equal(0, watch.Exit);
        var events = watch.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<CliMonitorEvent>(line, Json)!)
            .ToArray();
        Assert.Contains(events, item => item.Kind == "conflict");
        Assert.Contains(events, item => item.Kind == "failure");
        Assert.Contains("设备冲突待裁决", watch.Error, StringComparison.Ordinal);
        Assert.Contains("试题包下载失败", watch.Error, StringComparison.Ordinal);
    }

    private static JsonElement ReadBody(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>CLI 输出的监测事件形状（测试侧镜像；CLI 以一行一个 JSON 写到 stdout）。</summary>
    private sealed record CliMonitorEvent(string ExamId, string Kind, string ParticipantId, string ClientNumber,
        string Candidate, string Detail, DateTimeOffset ObservedAt);

    private static MonitoringSnapshot MonitoringSnapshot(params MonitoringParticipant[] participants) =>
        new("exam-1", Start, new VersionVector(1, 1, 1), ExamLifecycle.Running, 1, participants.Length, 0, participants, []);

    private static MonitoringParticipant Pres14_Participant(string participationId = "part-1", string candidateId = "cand-1",
        string clientNumber = "20261006001", string downloadState = "已下载", string? receiptId = null,
        IReadOnlyList<ConflictDevice>? conflicts = null, IReadOnlyList<MonitoringCommand>? commands = null) => new(
        participationId, candidateId, "合成考生", "001", "00001", "device-a", clientNumber, "room-1", "第一试场",
        ParticipationBusinessState.Answering, DeviceConnectionState.Online, "正在答题", "q-1", Start,
        1, 1, 3, 1, 1, 1, downloadState, new Dictionary<string, AnswerValue>(StringComparer.Ordinal),
        receiptId is null ? "未回收" : "已回收", receiptId, receiptId is null ? null : Start.AddMinutes(-1),
        0, 0, null, commands ?? [], conflicts ?? []);

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
