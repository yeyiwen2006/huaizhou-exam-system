using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

/// <summary>PR #42 review regressions; all identities, credentials and workbook rows are synthetic.</summary>
public sealed class ReviewRegressionTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static readonly ScheduleWindow Schedule = new(
        new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("https://user:password@localhost/")]
    [InlineData("https://localhost/?redirect=other")]
    [InlineData("https://localhost/#fragment")]
    public void Connection_RejectsAmbiguousCredentialBearingBaseAddress(string address)
    {
        using var api = new AdminApiClient();
        Assert.Throws<ArgumentException>(() => api.Configure(address, "synthetic-token", "operator-1"));
        Assert.False(api.IsConfigured);
    }

    [Fact]
    public async Task UpdateSchedule_UsesObservedVersionWithoutRefreshingIt()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Put, request.Method);
            using var body = ReadBody(request);
            Assert.Equal(7, body.RootElement.GetProperty("expectedVersion").GetInt64());
            return Error(HttpStatusCode.Conflict, "VERSION_CONFLICT");
        });
        using var api = Configured(handler);
        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.SaveScheduleAsync("exam-1", Schedule, 7));
        Assert.Equal("VERSION_CONFLICT", error.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NewSchedule_IsCreateOnlyAndNeverOverwritesExistingExam()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/versioning/exams", request.RequestUri!.AbsolutePath);
            return Error(HttpStatusCode.Conflict, "EXAM_ALREADY_EXISTS");
        });
        using var api = Configured(handler);
        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.SaveScheduleAsync("exam-1", Schedule));
        Assert.Equal("EXAM_ALREADY_EXISTS", error.Code);
    }

    [Fact]
    public async Task ScheduleViewModel_ConflictPreservesDraftAndOriginalVersion()
    {
        var gets = 0;
        var expectedVersions = new List<long>();
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) { gets++; return Success(Snapshot(7)); }
            using var body = ReadBody(request);
            expectedVersions.Add(body.RootElement.GetProperty("expectedVersion").GetInt64());
            return Error(HttpStatusCode.Conflict, "VERSION_CONFLICT");
        });
        using var api = Configured(handler);
        var viewModel = new ExamConfigurationViewModel(api) { ExamId = "exam-1" };
        await viewModel.LoadScheduleAsync();
        viewModel.ScheduledEndText = "2026-10-01 12:00:00 +00:00";
        await Assert.ThrowsAsync<AdminApiException>(() => viewModel.SaveScheduleAsync());
        await Assert.ThrowsAsync<AdminApiException>(() => viewModel.SaveScheduleAsync());
        Assert.Equal(new long[] { 7, 7 }, expectedVersions);
        Assert.Equal(1, gets);
        Assert.Equal("2026-10-01 12:00:00 +00:00", viewModel.ScheduledEndText);
        Assert.Contains("版本 7", viewModel.ScheduleVersionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduleViewModel_AdvancesVersionOnlyAfterSuccessfulSaveAndClearsOnReconnect()
    {
        var expected = new List<long>();
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Success(Snapshot(7));
            using var body = ReadBody(request);
            expected.Add(body.RootElement.GetProperty("expectedVersion").GetInt64());
            return Success(Snapshot(7 + expected.Count));
        });
        using var api = Configured(handler);
        var viewModel = new ExamConfigurationViewModel(api) { ExamId = "exam-1" };
        await viewModel.LoadScheduleAsync();
        await viewModel.SaveScheduleAsync();
        await viewModel.SaveScheduleAsync();
        Assert.Equal(new long[] { 7, 8 }, expected);
        api.Configure("https://localhost:5002/", "synthetic-token", "operator-1");
        Assert.Contains("尚未加载", viewModel.ScheduleVersionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduleLoad_DoesNotOverwriteEditsMadeWhileRequestWasInFlight()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(_ => release.Task);
        using var api = Configured(handler);
        var viewModel = new ExamConfigurationViewModel(api) { ExamId = "exam-1" };
        var pending = viewModel.LoadScheduleAsync();
        viewModel.ScheduledEndText = "2026-10-01 12:00:00 +00:00";
        release.SetResult(Success(Snapshot(7)));
        await pending;
        Assert.Equal("2026-10-01 12:00:00 +00:00", viewModel.ScheduledEndText);
        Assert.Contains("尚未加载", viewModel.ScheduleVersionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostMutationResponse_RetryKeepsRequestIdUntilDefinitiveSuccess()
    {
        var ids = new List<string>();
        using var handler = new Handler(request =>
        {
            using var body = ReadBody(request);
            ids.Add(body.RootElement.GetProperty("requestId").GetString()!);
            if (ids.Count == 1) throw new HttpRequestException("Synthetic response lost after server commit");
            return Success(new ActivationResult("device-1", "20260923001", new DateOnly(2026, 9, 23),
                ActivationStatus.Active, null, 1, 1));
        });
        using var api = Configured(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => api.ApproveActivationAsync("request-1", null));
        await api.ApproveActivationAsync("request-1", null);
        await api.ApproveActivationAsync("request-1", null);
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[1], ids[2]);
    }

    [Fact]
    public async Task ProxyError_ReportsHttpStatusWithoutLeakingBody()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>synthetic-private-proxy-detail</html>")
        });
        using var api = Configured(handler);
        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.GetExamAsync("exam-1"));
        Assert.Equal("HTTP_502", error.Code);
        Assert.DoesNotContain("synthetic-private", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulHttpWithInvalidJson_IsNotBusinessSuccess()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json")
        });
        using var api = Configured(handler);
        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.GetExamAsync("exam-1"));
        Assert.Equal("INVALID_RESPONSE", error.Code);
    }

    [Theory]
    [InlineData("other-server", "node-1", 2)]
    [InlineData("server-1", "node-1", 1)]
    [InlineData("server-1", "other-node", 2)]
    public async Task Response_RejectsForeignServerOrStaleEpoch(string server, string node, long epoch)
    {
        var first = true;
        using var handler = new Handler(_ =>
        {
            var result = first ? Success(Snapshot(1), "server-1", "node-1", 2)
                : Success(Snapshot(1), server, node, epoch);
            first = false;
            return result;
        });
        using var api = Configured(handler);
        await api.GetExamAsync("exam-1");
        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.GetExamAsync("exam-1"));
        Assert.Equal("STALE_SERVER_IDENTITY", error.Code);
    }

    [Fact]
    public async Task Reconnection_DiscardsOldSuccessfulResponse()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(_ => release.Task);
        using var api = Configured(handler);
        var pending = api.GetExamAsync("exam-1");
        api.Configure("https://localhost:5002/", "synthetic-token", "operator-1");
        release.SetResult(Success(Snapshot(1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("CON.txt.backup")]
    [InlineData("AUX.exam.json")]
    [InlineData("LPT1.paper.tex")]
    public void ExamName_RejectsReservedDeviceNameWithMultipleExtensions(string name)
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api) { Name = name };
        Assert.Contains(viewModel.Validate(), error => error.Contains("Windows 文件名", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupIds_AreUniqueAcrossSubjects_NotOnlyWithinOneSubject()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        viewModel.AddSubjectCommand.Execute(null);
        viewModel.Subjects[1].Groups[0].GroupId = viewModel.Subjects[0].Groups[0].GroupId;
        Assert.Contains(viewModel.Validate(), error => error.Contains("标识为空或重复", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectedGroupChange_RefreshesDeleteCommandAvailability()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        var notifications = 0;
        viewModel.RemoveGroupCommand.CanExecuteChanged += (_, _) => notifications++;
        viewModel.SelectedSubject!.SelectedGroup = null;
        Assert.False(viewModel.RemoveGroupCommand.CanExecute(null));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Monitoring_RejectsOldResponseAfterExamSwitchAwayAndBack()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(_ => release.Task);
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        var pending = viewModel.LoadAsync();
        viewModel.ExamId = "exam-2";
        viewModel.ExamId = "exam-1";
        release.SetResult(Success(new[] { Participation("exam-1") }));
        await pending;
        Assert.Empty(viewModel.Rows);
        Assert.Contains("重新", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitoring_LastRequestedRefreshWinsEvenWhenResponsesArriveOutOfOrder()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(_ => ++calls == 1 ? release.Task
            : Task.FromResult(Success(new[] { Participation("exam-1") with { CandidateId = "new-candidate" } })));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        var oldRequest = viewModel.LoadAsync();
        await viewModel.LoadAsync();
        release.SetResult(Success(new[] { Participation("exam-1") }));
        await oldRequest;
        Assert.Equal("new-candidate", Assert.Single(viewModel.Rows).CandidateId);
    }

    [Fact]
    public async Task CollectionEvents_RejectOtherExamWithoutAdvancingCursorOrNotifying()
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            calls.Add(request.RequestUri!.Query);
            return Success(new[] { new CollectionCompletedNotification("event-1", "exam-2", 999, 300, DateTimeOffset.UtcNow) });
        });
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        var notifications = 0;
        viewModel.CollectionCompleted += _ => notifications++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.RefreshCollectionEventsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.RefreshCollectionEventsAsync());
        Assert.All(calls, query => Assert.Contains("afterRosterVersion=0", query, StringComparison.Ordinal));
        Assert.Empty(viewModel.CollectionEvents);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task CollectionEvents_NotifyOnceForNewVersionAndDoNotRepeatOnRefresh()
    {
        var notification = new CollectionCompletedNotification("event-1", "exam-1", 1, 300, DateTimeOffset.UtcNow);
        using var handler = new Handler(_ => Success(new[] { notification }));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        var notifications = new List<CollectionCompletedNotification>();
        viewModel.CollectionCompleted += notifications.Add;
        await viewModel.RefreshCollectionEventsAsync();
        await viewModel.RefreshCollectionEventsAsync();
        Assert.Equal(notification, Assert.Single(notifications));
        Assert.Single(viewModel.CollectionEvents);
    }

    [Fact]
    public async Task DeviceDecisionSuccess_WithRefreshFailure_IsNotReportedAsFailedDecision()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Post
            ? Success(new DeviceDecisionResult("part-1", "device-2", "device-1", 2, 4,
                "operator-1", "synthetic reason", DateTimeOffset.UtcNow))
            : throw new HttpRequestException("Synthetic refresh unavailable"));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api)
        {
            ExamId = "exam-1", ParticipationId = "part-1", SelectedDeviceId = "device-2",
            ExpectedVersionText = "3", DecisionReason = "synthetic reason"
        };
        await viewModel.DecideDeviceAsync();
        Assert.Contains("裁决已保存", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("刷新失败", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("不要重复裁决", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("4", viewModel.ExpectedVersionText);
    }

    [Fact]
    public void Reconnection_ClearsRemoteRowsAndDestructiveActionInputs()
    {
        using var api = new AdminApiClient();
        var viewModel = new MonitoringViewModel(api)
        {
            ExamId = "exam-1", ParticipationId = "part-1", SelectedDeviceId = "device-2",
            EventId = "event-1", ExpectedVersionText = "7"
        };
        viewModel.Rows.Add(new MonitoringRow("part-1", "candidate-1", "候考", "device-1", 1, 1, 7,
            "未提交", "接口未提供", "接口未提供"));
        api.Configure("https://localhost:5002/", "synthetic-token", "operator-1");
        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.ParticipationId);
        Assert.Empty(viewModel.SelectedDeviceId);
        Assert.Empty(viewModel.EventId);
        Assert.Empty(viewModel.ExpectedVersionText);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Roster_RejectsNumericIdentifierEvenWithZeroPaddingDisplayFormat(int column)
    {
        WithWorkbook((path, sheet) =>
        {
            sheet.Cell(2, column).Value = 123;
            sheet.Cell(2, column).Style.NumberFormat.Format = "000000";
        }, path =>
        {
            var preview = new RosterWorkbookService().ReadPreview(path, []);
            Assert.True(preview.HasErrors);
            Assert.Contains("以文本存储", Assert.Single(preview.Rows).Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Roster_RejectsFormulaHeadersWithoutEvaluatingThem()
    {
        WithWorkbook((_, sheet) => sheet.Cell(1, 1).FormulaA1 = "\"姓名\"",
            path => Assert.Throws<RosterImportException>(() => new RosterWorkbookService().ReadPreview(path, [])));
    }

    [Fact]
    public void Roster_DoesNotSilentlyIgnoreSecondWorksheet()
    {
        WithWorkbook((_, sheet) => sheet.Workbook.AddWorksheet("另一个名单"),
            path => Assert.Throws<RosterImportException>(() => new RosterWorkbookService().ReadPreview(path, [])));
    }

    [Fact]
    public void Roster_AnyEntryCollectionChangeInvalidatesEarlierPreview()
    {
        WithWorkbook((_, _) => { }, path =>
        {
            var viewModel = new RosterViewModel(new RosterWorkbookService(), new Dialogs(path));
            viewModel.ImportPreviewCommand.Execute(null);
            Assert.True(viewModel.ConfirmImportCommand.CanExecute(null));
            viewModel.Entries.Add(new RosterEntryDraft("另一名合成考生", "002", "00001"));
            Assert.False(viewModel.ConfirmImportCommand.CanExecute(null));
            viewModel.ConfirmImportCommand.Execute(null);
            Assert.Single(viewModel.Entries);
        });
    }

    private static void WithWorkbook(Action<string, IXLWorksheet> configure, Action<string> verify)
    {
        var path = Path.Combine(Path.GetTempPath(), $"synthetic-admin-review-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("名册");
                sheet.Cell(1, 1).Value = "姓名";
                sheet.Cell(1, 2).Value = "班级";
                sheet.Cell(1, 3).Value = "准考证号";
                sheet.Cell(2, 1).Value = "合成考生";
                sheet.Cell(2, 2).Value = "001";
                sheet.Cell(2, 3).Value = "00001";
                configure(path, sheet);
                workbook.SaveAs(path);
            }
            verify(path);
        }
        finally { File.Delete(path); }
    }

    private static ParticipationResult Participation(string examId) => new("part-1", examId, "candidate-1",
        ParticipationBusinessState.Waiting, "device-1", 1, 1, 1, null);

    private static ExamVersionSnapshot Snapshot(long version) => new("exam-1", new VersionVector(version, 0, 0),
        Schedule, ExamLifecycle.Scheduled, null, null, null, null, new Dictionary<string, ParticipantVersionStatus>());

    private static JsonDocument ReadBody(HttpRequestMessage request) =>
        JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

    private static AdminApiClient Configured(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
    {
        Content = JsonContent.Create(new ApiEnvelope<object>("server-1", "node-1", 1, "review-test", null,
            new ApiError(code, $"Synthetic {code}")), options: Json)
    };

    private static HttpResponseMessage Success<T>(T result, string server = "server-1", string node = "node-1", long epoch = 1) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>(server, node, epoch, "review-test", result, null), options: Json)
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class Dialogs(string path) : IFileDialogService
    {
        public string? OpenExcelFile() => path;
        public string? SaveExcelFile(string suggestedName) => null;
    }
}
