using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

/// <summary>
/// C04 监测页用例：字段、状态、回收与裁决全部来自服务器权威快照（合成数据），
/// 界面不自行拼接来源，也不把非法快照显示成半份状态。
/// </summary>
public sealed class MonitoringViewModelTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public async Task Load_ShowsAuthoritativeFieldsWithBusinessAndConnectionStatesSeparated()
    {
        var snapshot = MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(
                businessState: ParticipationBusinessState.Answering,
                connectionState: DeviceConnectionState.Offline,
                currentQuestionId: "2",
                answers: new Dictionary<string, AnswerValue> { ["2"] = AnswerValue.Selected("A", "C") },
                installedPaperVersion: 1, targetPaperVersion: 2, downloadState: "失败",
                focusLossCount: 2, focusLossMilliseconds: 4500,
                revisionStatus: new ParticipantVersionStatus("part-1", ParticipationBusinessState.Answering, 2, 2,
                    PaperUpdateState.ReviewRequired, null),
                commands: [MonitoringFixture.Command(CommandEventType.Start, canResend: false, eventId: "start-1",
                    confirmation: "已执行", acknowledgedAt: MonitoringFixture.At)],
                conflicts: [MonitoringFixture.Conflict()]),
            MonitoringFixture.Participant(
                participationId: "part-2", candidateId: "candidate-2", name: "合成考生二", admissionNo: "00002",
                clientNumber: "20261005002", roomGroupId: null, roomGroupName: "未分组",
                businessState: ParticipationBusinessState.Waiting, connectionState: DeviceConnectionState.Unknown,
                currentQuestionId: null, answers: new Dictionary<string, AnswerValue>()));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1", ExamName = "合成考试" };

        await viewModel.LoadAsync();

        var answering = viewModel.Rows[0];
        Assert.Equal("作答中", answering.BusinessState);
        // 离线只改变连接状态，不改变业务状态。
        Assert.Equal("离线", answering.ConnectionState);
        Assert.Equal("第一试场", answering.RoomGroup);
        Assert.Equal("正在答题", answering.Page);
        Assert.Equal("第 2 题：A/C", answering.QuestionAnswer);
        Assert.Equal("第 1 版 → 第 2 版（失败）", answering.PaperState);
        Assert.Equal("2 次 / 共 4 秒", answering.FocusSummary);
        Assert.Equal("未复核", answering.PaperReviewState);
        Assert.Equal("未回收", answering.CollectionState);
        Assert.Equal("开始答题：已执行（09:30:00）", answering.CommandSummary);
        Assert.Equal("2026-10-05 09:30:00", answering.LastCommunicationText);

        var waiting = viewModel.Rows[1];
        Assert.Equal("候考", waiting.BusinessState);
        // 没有有效通信记录时明确标识未登录，而不是显示为在线。
        Assert.Equal("未登录", waiting.ConnectionState);
        Assert.Equal("未分组", waiting.RoomGroup);
        Assert.Equal("—", waiting.QuestionAnswer);
        Assert.Equal("暂无指令", waiting.CommandSummary);
        Assert.Equal("无通信记录", waiting.LastCommunicationText);

        Assert.Equal("实际参试 2 人；已回收 0 份；未回收 2 份。", viewModel.CountsText);
        Assert.Contains("进行中", viewModel.AsOfText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_UncollectedListSharesRowsWithTheParticipationList()
    {
        var snapshot = MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(),
            MonitoringFixture.Participant(participationId: "part-2", candidateId: "candidate-2",
                receiptId: "receipt-2"));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();

        // 未回收名单与参试列表是同一批行对象：在任一表格选中都作用于同一名考生。
        Assert.Same(viewModel.Rows[0], Assert.Single(viewModel.UncollectedRows));
        Assert.Equal("已回收", viewModel.Rows[1].CollectionState);
        Assert.Equal("2026-10-05 09:30:00", viewModel.Rows[1].ReceivedText);
        Assert.Equal("实际参试 2 人；已回收 1 份；未回收 1 份。", viewModel.CountsText);
    }

    [Fact]
    public async Task Load_SubmittedWithoutReceiptIsNotCountedAsCollected()
    {
        // 需求：交卷处理中另行提示，尚未持久保存不能显示已回收——业务状态已交卷不等于已回收。
        var snapshot = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            businessState: ParticipationBusinessState.Submitted, collectionState: "未回收"));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();

        Assert.Equal("已交卷", Assert.Single(viewModel.Rows).BusinessState);
        Assert.Equal("未回收", viewModel.Rows[0].CollectionState);
        Assert.Equal("实际参试 1 人；已回收 0 份；未回收 1 份。", viewModel.CountsText);
        Assert.Same(viewModel.Rows[0], Assert.Single(viewModel.UncollectedRows));
    }

    [Fact]
    public async Task RequestReupload_CountsUpdateOnTheNextAuthoritativeRead()
    {
        // 需求：补传后计数更新——补传之后服务器拿到持久化回执，下一次权威读取应把该考生计为已回收。
        var uncollected = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            commands: [MonitoringFixture.Command(CommandEventType.End, canResend: true, eventId: "end-1")]));
        var collected = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(receiptId: "receipt-1"));
        var collectedOnServer = false;
        using var handler = new SyntheticHandler(request => request.Method == HttpMethod.Post
            ? Success(new CommandEventView("end-1", "exam-1", "part-1", 1, CommandEventType.End, 1,
                "device-part-1", 2, null, MonitoringFixture.At, false, 2))
            : Success(collectedOnServer ? collected : uncollected));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        await UntilAsync(() => viewModel.Rows.Count == 1);
        Assert.Equal("实际参试 1 人；已回收 0 份；未回收 1 份。", viewModel.CountsText);

        // 补传：服务器在之后拿到回执，考务端下一次权威读取即更新计数。
        collectedOnServer = true;
        viewModel.SelectedRow = viewModel.Rows[0];
        await viewModel.RequestReuploadAsync();
        await UntilAsync(() => viewModel.CountsText.Contains("已回收 1 份", StringComparison.Ordinal));

        Assert.Equal("已回收", viewModel.Rows[0].CollectionState);
        Assert.Empty(viewModel.UncollectedRows);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 600 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "等待条件超时。");
    }

    [Fact]
    public async Task Load_EmptyExamDoesNotManufactureRows()
    {
        using var handler = new SyntheticHandler(_ =>
            Success(MonitoringFixture.Snapshot("exam-1", Array.Empty<MonitoringParticipant>())));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.UncollectedRows);
        Assert.Equal("实际参试 0 人；已回收 0 份；未回收 0 份。", viewModel.CountsText);
        Assert.Contains("还没有实际参试考生", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_TransportFailureClearsRows()
    {
        var failing = false;
        using var handler = new SyntheticHandler(_ => failing
            ? throw new HttpRequestException("synthetic unavailable")
            : Success(MonitoringFixture.Default()));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();
        Assert.Single(viewModel.Rows);
        failing = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.LoadAsync());
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task Load_RejectedSnapshotKeepsTheLastTrustedRows()
    {
        // 人数与列表不一致：边界读取必须整体拒绝，不能替换成半份状态。
        using var handler = new SyntheticHandler(_ =>
            Success(MonitoringFixture.Default() with { ActualCount = 2 }));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        viewModel.Rows.Add(new MonitoringRow("existing", "candidate-existing", "候考", "device-existing", 1, 1, 1,
            "未回收", "暂无记录", "—"));

        var error = await Assert.ThrowsAsync<AdminApiException>(() => viewModel.LoadAsync());

        Assert.Equal("INVALID_RESPONSE", error.Code);
        Assert.Equal("existing", Assert.Single(viewModel.Rows).ParticipationId);
        Assert.Contains("已保留上一次结果", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotBoundary_RejectsInvalidAuthoritativePayloads()
    {
        var valid = MonitoringFixture.Default();
        var cases = new (string Name, MonitoringSnapshot Value)[]
        {
            ("别的考试", valid with { ExamId = "exam-2" }),
            ("人数与列表不一致", valid with { ActualCount = 2 }),
            ("已回收却没有持久化回执", MonitoringFixture.Snapshot("exam-1",
                MonitoringFixture.Participant(collectionState: "已回收"))),
            ("未回收却带持久化回执", MonitoringFixture.Snapshot("exam-1",
                MonitoringFixture.Participant(receiptId: "receipt-1", collectionState: "未回收"))),
            ("未定义业务状态", MonitoringFixture.Snapshot("exam-1",
                MonitoringFixture.Participant(businessState: (ParticipationBusinessState)99))),
            ("同一名考生两条参试记录", MonitoringFixture.Snapshot("exam-1",
                MonitoringFixture.Participant(), MonitoringFixture.Participant(participationId: "part-2"))),
            ("版本越界", MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(entryVersion: 0))),
            ("未回收不属于参试列表", MonitoringFixture.Snapshot("exam-1",
                new[] { MonitoringFixture.Participant() },
                new[] { MonitoringFixture.Participant(participationId: "part-9", candidateId: "candidate-9") })),
        };

        foreach (var (name, value) in cases)
        {
            using var handler = new SyntheticHandler(_ => Success(value));
            using var api = new AdminApiClient(handler);
            api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
            var error = await Assert.ThrowsAsync<AdminApiException>(() => api.GetMonitoringSnapshotAsync("exam-1"));
            Assert.Equal("INVALID_RESPONSE", error.Code);
            Assert.True(error.Message.Length > 0, name);
        }
    }

    [Fact]
    public async Task Filter_CombinesSiteFieldsAndClears()
    {
        var participants = Enumerable.Range(1, 300).Select(index => MonitoringFixture.Participant(
            participationId: $"part-{index:D3}", candidateId: $"candidate-{index:D3}",
            name: $"合成考生{index:D3}", classText: index % 2 == 0 ? "002" : "001",
            admissionNo: index % 2 == 0 ? $"00002{index:D3}" : $"00001{index:D3}",
            clientNumber: $"2026100{index:D3}",
            roomGroupId: index % 3 == 0 ? null : "room-1",
            roomGroupName: index % 3 == 0 ? "未分组" : "第一试场",
            receiptId: index % 2 == 0 ? $"receipt-{index:D3}" : null)).ToArray();
        using var handler = new SyntheticHandler(_ => Success(MonitoringFixture.Snapshot("exam-1", participants)));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();
        Assert.Equal(300, viewModel.FilteredRows.Count());
        Assert.Equal(new[] { "未分组", "第一试场" }, viewModel.RoomGroupOptions);

        viewModel.ClassFilter = "002";
        Assert.Equal(150, viewModel.FilteredRows.Count());
        viewModel.ClientNumberFilter = "2026100050";
        Assert.Equal("合成考生050", Assert.Single(viewModel.FilteredRows).Name);
        viewModel.ClearFiltersCommand.Execute(null);
        Assert.Equal(300, viewModel.FilteredRows.Count());

        viewModel.NameFilter = "合成考生1";
        Assert.Equal(100, viewModel.FilteredRows.Count());
        viewModel.ClearFiltersCommand.Execute(null);
        viewModel.AdmissionNoFilter = "00001003";
        Assert.Equal("合成考生003", Assert.Single(viewModel.FilteredRows).Name);
        viewModel.ClearFiltersCommand.Execute(null);

        viewModel.RoomGroupFilter = "未分组";
        Assert.Equal(100, viewModel.FilteredRows.Count());
        viewModel.ClassFilter = "001";
        viewModel.OnlyUncollected = true;
        // 未分组(3 的倍数) ∩ 001 班(奇数) ∩ 未回收(奇数) = 3 的奇数倍
        Assert.Equal(50, viewModel.FilteredRows.Count());
        viewModel.ClearFiltersCommand.Execute(null);
        Assert.Equal(300, viewModel.FilteredRows.Count());
        Assert.False(viewModel.OnlyUncollected);
    }

    [Fact]
    public async Task SelectingRowFillsDecisionInputsCommandListAndConflictComparison()
    {
        var snapshot = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            commands:
            [
                MonitoringFixture.Command(CommandEventType.Start, canResend: false, eventId: "start-1",
                    confirmation: "已执行", acknowledgedAt: MonitoringFixture.At),
                MonitoringFixture.Command(CommandEventType.End, canResend: true, eventId: "end-1"),
            ],
            conflicts:
            [
                MonitoringFixture.Conflict("device-part-1", authorized: true),
                MonitoringFixture.Conflict("20261005009-room", authorized: false,
                    connectionState: DeviceConnectionState.Offline, answerVersion: 9, page: "交卷确认"),
            ]));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();
        viewModel.SelectedRow = viewModel.Rows[0];

        Assert.Equal("part-1", viewModel.ParticipationId);
        Assert.Equal("device-part-1", viewModel.SelectedDeviceId);
        Assert.Equal("3", viewModel.ExpectedVersionText);
        // 指令按名称选择：默认选中最近一条仍可重发的指令，不再手填事件标识。
        Assert.Equal(2, viewModel.SelectedCommands.Count);
        Assert.Equal("end-1", viewModel.EventId);
        Assert.Equal("结束答题（待确认）｜可重发", viewModel.SelectedCommand!.DisplayText);
        Assert.Equal("开始答题（已执行，09:30:00）｜不可重发",
            viewModel.SelectedCommands.Single(item => item.Command.EventId == "start-1").DisplayText);

        // 裁决前先核对两台设备的编号、试场、授权、连接与答题进度。
        Assert.Equal(2, viewModel.SelectedConflictDevices.Count);
        viewModel.SelectedConflictDevice = viewModel.SelectedConflictDevices
            .Single(item => item.DeviceId == "20261005009-room");
        Assert.Equal("20261005009-room", viewModel.SelectedDeviceId);
        Assert.Equal("等待裁决", viewModel.SelectedConflictDevice!.AuthorizationText);
        Assert.Equal("离线", viewModel.SelectedConflictDevice.ConnectionState);
        Assert.Equal("第 9 版答案｜交卷确认", viewModel.SelectedConflictDevice.ProgressText);
    }

    [Fact]
    public async Task RequestReuploadResendsTheResendableEndCommandOnly()
    {
        var posts = new List<string>();
        using var handler = new SyntheticHandler(request =>
        {
            if (request.Method != HttpMethod.Post)
                return Success(MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
                    commands: [MonitoringFixture.Command(CommandEventType.End, canResend: true, eventId: "end-1")])));
            posts.Add(request.RequestUri!.AbsolutePath);
            return Success(new CommandEventView("end-1", "exam-1", "part-1", 1, CommandEventType.End, 1,
                "device-part-1", 2, null, MonitoringFixture.At, false, 2));
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await viewModel.LoadAsync();
        viewModel.SelectedRow = Assert.Single(viewModel.Rows);
        await viewModel.RequestReuploadAsync();

        Assert.Equal("/api/v1/commands/end-1/resend", Assert.Single(posts));
        Assert.Contains("已重发指令", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestReupload_DoesNotResendAcknowledgedEndCommand()
    {
        var posts = 0;
        using var handler = new SyntheticHandler(request =>
        {
            if (request.Method != HttpMethod.Post) return Success(MonitoringFixture.Default());
            posts++;
            return Success(new CommandEventView("end-1", "exam-1", "part-1", 1, CommandEventType.End, 1,
                "device-part-1", 2, null, MonitoringFixture.At, false, 2));
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        viewModel.Rows.Add(MonitoringRow.From(MonitoringFixture.Participant(commands:
            [MonitoringFixture.Command(CommandEventType.End, canResend: false, eventId: "end-1", confirmation: "已执行",
                acknowledgedAt: MonitoringFixture.At)])));
        viewModel.SelectedRow = viewModel.Rows[0];

        await viewModel.RequestReuploadAsync();

        Assert.Equal(0, posts);
        Assert.Contains("不能重复请求补传", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_OnlyUncollectedIncludesRowsStillBeingCollected()
    {
        // “回收中”同样没有持久化回执：它属于未回收名单，也必须能被“只看未回收”筛出来。
        var snapshot = MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(),
            MonitoringFixture.Participant(participationId: "part-2", candidateId: "candidate-2", collectionState: "回收中"),
            MonitoringFixture.Participant(participationId: "part-3", candidateId: "candidate-3", receiptId: "receipt-3"));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await viewModel.LoadAsync();

        viewModel.OnlyUncollected = true;

        Assert.Equal(new[] { "未回收", "回收中" }, viewModel.FilteredRows.Select(row => row.CollectionState));
        // 与回收统计、未回收名单口径一致。
        Assert.Equal("实际参试 3 人；已回收 1 份；未回收 2 份。", viewModel.CountsText);
        Assert.Equal(2, viewModel.UncollectedRows.Count);
    }

    [Fact]
    public async Task Filter_WhitespaceOnlyBehavesLikeNoFilter()
    {
        var snapshot = MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(),
            MonitoringFixture.Participant(participationId: "part-2", candidateId: "candidate-2",
                name: "合成考生二", roomGroupId: "room-2", roomGroupName: "第二试场"));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await viewModel.LoadAsync();

        // 只输了空白按“未筛选”处理：文本筛选与试场筛选口径必须一致，不能一个全放行、一个全挡住。
        viewModel.NameFilter = " ";
        viewModel.RoomGroupFilter = " ";

        Assert.Equal(2, viewModel.FilteredRows.Count());
    }

    [Fact]
    public void ChangingExam_ClearsRowsCollectionEventsAndFilters()
    {
        using var api = new AdminApiClient();
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1", ExamName = "合成考试" };
        viewModel.Rows.Add(new MonitoringRow("part-1", "candidate-1", "候考", "device-1",
            1, 1, 1, "未回收", "暂无记录", "—"));
        viewModel.UncollectedRows.Add(viewModel.Rows[0]);
        viewModel.CollectionEvents.Add(new CollectionCompletedNotification(
            "event-1", "exam-1", 1, 1, DateTimeOffset.UtcNow));
        viewModel.NameFilter = "合成考生";
        viewModel.RoomGroupFilter = "第一试场";
        viewModel.ClassFilter = "001";
        viewModel.OnlyUncollected = true;

        viewModel.ExamId = "exam-2";

        // 上一场考试的筛选条件对新考试没有意义（试场可能根本不存在），必须清空。
        Assert.Empty(viewModel.NameFilter);
        Assert.Empty(viewModel.RoomGroupFilter);
        Assert.Empty(viewModel.ClassFilter);
        Assert.Empty(viewModel.ClientNumberFilter);
        Assert.Empty(viewModel.AdmissionNoFilter);
        Assert.Empty(viewModel.FilterText);
        Assert.False(viewModel.OnlyUncollected);
        // 绑定在选项被替换时可能写入 null，不能让它变成“空引用”。
        viewModel.NameFilter = null!;
        Assert.Empty(viewModel.NameFilter);
        Assert.Equal(viewModel.Rows.Count, viewModel.FilteredRows.Count());
        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.UncollectedRows);
        Assert.Empty(viewModel.CollectionEvents);
        Assert.Empty(viewModel.SelectedCommands);
        Assert.Empty(viewModel.SelectedConflictDevices);
        Assert.Equal("实际参试 0 人；已回收 0 份；未回收 0 份。", viewModel.CountsText);
        Assert.Contains("已切换", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionEvents_AdvanceCursorAndDeduplicatePerExam()
    {
        var allEvents = Enumerable.Range(1, 101)
            .Select(index => new CollectionCompletedNotification(
                $"event-{index}", "exam-1", index, 300, DateTimeOffset.UtcNow))
            .ToArray();
        var calls = new List<long>();
        using var handler = new SyntheticHandler(request =>
        {
            var query = request.RequestUri!.Query;
            var cursor = long.Parse(query.Split("afterRosterVersion=")[1]);
            calls.Add(cursor);
            return Success(allEvents.Where(item => item.RosterVersion > cursor).Take(100).ToArray());
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.RefreshCollectionEventsAsync();
        await viewModel.RefreshCollectionEventsAsync();

        Assert.Equal(new long[] { 0, 100, 101 }, calls);
        Assert.Equal(101, viewModel.CollectionEvents.Count);
        Assert.Contains("重新统计", viewModel.CollectionStatus, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>(
            "synthetic-server", "synthetic-node", 1, "test", value, null), options: Json)
    };

    private sealed class SyntheticHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
