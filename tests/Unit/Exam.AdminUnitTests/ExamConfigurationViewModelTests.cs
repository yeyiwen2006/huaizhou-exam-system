using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Exam.AdminUnitTests;

public sealed class ExamConfigurationViewModelTests
{
    [Fact]
    public void DefaultDraft_StartsWithZeroCountsAndScores_AndNeedsAQuestionCount()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);

        var errors = viewModel.Validate();

        Assert.Contains(errors, error => error.Contains("题量", StringComparison.Ordinal));
        var subject = Assert.Single(viewModel.Subjects);
        var group = Assert.Single(subject.Groups);
        Assert.Equal("0", subject.MaxScore);
        Assert.Equal(0, group.QuestionCount);
        Assert.Equal("0", group.QuestionCountText);
        Assert.Equal("0", group.MaxScore);
        Assert.Equal("single", group.Type);
        Assert.Equal("一、单项选择题", group.Name);
        Assert.Equal("1", group.Correct);
        Assert.Equal("0", group.OneBlank);
        Assert.Equal("0", group.OneWrong);
        group.QuestionCount = 10;
        group.MaxScore = subject.MaxScore = "10";
        Assert.Empty(viewModel.Validate());

        viewModel.ResetForNew();
        Assert.EndsWith(" 12:00", viewModel.ScheduledEndText);
        Assert.Equal(0, viewModel.Subjects[0].Groups[0].QuestionCount);
        Assert.Equal("0", viewModel.Subjects[0].MaxScore);
        Assert.Equal("0", viewModel.Subjects[0].Groups[0].MaxScore);
    }

    [Fact]
    public void AddedGroups_UseChineseOrdinalsAndPreserveEditedNames()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        var subject = Assert.Single(viewModel.Subjects);
        subject.Groups[0].Name = "自定义题组名称";
        for (var number = 2; number <= 21; number++) viewModel.AddGroupCommand.Execute(null);

        Assert.Equal("自定义题组名称", subject.Groups[0].Name);
        Assert.Equal("二、单项选择题", subject.Groups[1].Name);
        Assert.Equal("十、单项选择题", subject.Groups[9].Name);
        Assert.Equal("十一、单项选择题", subject.Groups[10].Name);
        Assert.Equal("二十、单项选择题", subject.Groups[19].Name);
        Assert.Equal("二十一、单项选择题", subject.Groups[20].Name);
        viewModel.RefreshInstructionsCommand.Execute(null);
        Assert.Contains("二、单项选择题：", viewModel.InstructionsText);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("exam:name")]
    [InlineData("考试.")]
    [InlineData("  ")]
    public void Validate_RejectsWindowsInvalidExamName(string name)
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api) { Name = name };

        Assert.Contains(viewModel.Validate(), error => error.Contains("不能包含", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsUnknownScoringVariables()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        Assert.Single(Assert.Single(viewModel.Subjects).Groups).Correct = "{题目数} * 2";

        Assert.Contains(viewModel.Validate(), error => error.Contains("计分规则只能", StringComparison.Ordinal));
    }

    [Fact]
    public void RefreshInstructions_UsesBranchesForIndeterminateGroup()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        Assert.Single(Assert.Single(viewModel.Subjects).Groups).Type = "indeterminate";

        viewModel.RefreshInstructionsCommand.Execute(null);

        Assert.Contains("若有一个正确答案", viewModel.InstructionsText, StringComparison.Ordinal);
        Assert.Contains("若有多个正确答案", viewModel.InstructionsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsEmptyNameAndNonIncreasingSchedule()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api)
        {
            Name = " ",
            ScheduledStartText = "2026-09-23 10:00",
            ScheduledEndText = "2026-09-23 10:00"
        };

        var errors = viewModel.Validate();

        Assert.Contains(errors, error => error.Contains("考试名称", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("晚于", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicSubjectAndGroupEdits_KeepIdsAndOrdersUnique()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        viewModel.AddSubjectCommand.Execute(null);
        viewModel.AddSubjectCommand.Execute(null);
        Assert.Equal(3, viewModel.Subjects.Count);

        viewModel.SelectedSubject = viewModel.Subjects[1];
        viewModel.RemoveSubjectCommand.Execute(null);
        viewModel.AddSubjectCommand.Execute(null);
        Assert.Equal(3, viewModel.Subjects.Select(subject => subject.SubjectId).Distinct().Count());

        var subject = viewModel.SelectedSubject!;
        viewModel.AddGroupCommand.Execute(null);
        viewModel.AddGroupCommand.Execute(null);
        subject.SelectedGroup = subject.Groups[1];
        viewModel.RemoveGroupCommand.Execute(null);
        viewModel.AddGroupCommand.Execute(null);

        Assert.Equal(3, subject.Groups.Select(group => group.GroupId).Distinct().Count());
        Assert.Equal(3, subject.Groups.Select(group => group.Order).Distinct().Count());
        foreach (var draft in viewModel.Subjects.SelectMany(item => item.Groups)) draft.QuestionCount = 10;
        Assert.Empty(viewModel.Validate());
    }

    [Fact]
    public void DifferentQuestionTypesAndAbstainFlag_ProduceMatchingInstructions()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        var subject = Assert.Single(viewModel.Subjects);
        var single = Assert.Single(subject.Groups);
        single.Name = "单选组";
        single.AllowAbstain = true;
        viewModel.AddGroupCommand.Execute(null);
        var multiple = subject.Groups[1];
        multiple.Type = "multiple";
        multiple.Name = "多选组";
        viewModel.AddGroupCommand.Execute(null);
        var indeterminate = subject.Groups[2];
        indeterminate.Type = "indeterminate";
        indeterminate.Name = "不定项组";
        foreach (var group in subject.Groups) group.QuestionCount = 10;

        viewModel.RefreshInstructionsCommand.Execute(null);

        Assert.Empty(viewModel.Validate());
        Assert.True(single.AllowAbstain);
        Assert.Contains("单选组", viewModel.InstructionsText, StringComparison.Ordinal);
        Assert.Contains("多选组", viewModel.InstructionsText, StringComparison.Ordinal);
        Assert.Contains("不定项组", viewModel.InstructionsText, StringComparison.Ordinal);
        Assert.Contains("选对但不全", viewModel.InstructionsText, StringComparison.Ordinal);
        Assert.Contains("每小题1分", viewModel.InstructionsText, StringComparison.Ordinal);
        Assert.Contains("允许选择“不作答”", viewModel.InstructionsText, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EnforcesPublishedSchemaExpressionLength()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        Assert.Single(Assert.Single(viewModel.Subjects).Groups).Correct = new string('1', 201);

        Assert.Contains(viewModel.Validate(), error => error.Contains("计分规则只能", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveFailure_ShowsVersionConflictInsteadOfSuccess()
    {
        using var handler = new ConflictHandler();
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = CreateFixedScheduleDraft(api);
        await viewModel.LoadScheduleAsync();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Status) && viewModel.Status.StartsWith("保存失败", StringComparison.Ordinal))
            {
                failure.TrySetResult(viewModel.Status);
            }
        };

        viewModel.SaveScheduleCommand.Execute(null);
        var status = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("其他人修改", status, StringComparison.Ordinal);
        Assert.DoesNotContain("考试时间已保存", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveReply_DoesNotClaimEditsMadeWhileWaitingWereSaved()
    {
        using var handler = new DeferredSaveHandler();
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = CreateFixedScheduleDraft(api);
        await viewModel.LoadScheduleAsync();
        var completed = WaitForStatus(viewModel, "此前填写的考试时间已保存");

        viewModel.SaveScheduleCommand.Execute(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ScheduledEndText = "2026-10-01 13:00 +08:00";
        handler.Complete(Snapshot(new ScheduleWindow(
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)))));

        var status = await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("尚未保存", status, StringComparison.Ordinal);
        Assert.Equal("2026-10-01 13:00 +08:00", viewModel.ScheduledEndText);
        Assert.Equal(2, viewModel.LoadedScheduleVersion);
    }

    [Fact]
    public async Task SaveReply_MatchingScheduleShowsOnlySupportedFieldsAsSaved()
    {
        using var handler = new DeferredSaveHandler();
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = CreateFixedScheduleDraft(api);
        await viewModel.LoadScheduleAsync();
        var completed = WaitForStatus(viewModel, "考试时间已保存");

        viewModel.SaveScheduleCommand.Execute(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        handler.Complete(Snapshot(new ScheduleWindow(
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)))));

        var status = await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, viewModel.LoadedScheduleVersion);
    }

    [Fact]
    public async Task SaveReply_RejectsDifferentScheduleInsteadOfShowingSuccess()
    {
        using var handler = new DeferredSaveHandler();
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = CreateFixedScheduleDraft(api);
        await viewModel.LoadScheduleAsync();
        var completed = WaitForStatus(viewModel, "保存失败");

        viewModel.SaveScheduleCommand.Execute(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        handler.Complete(Snapshot(new ScheduleWindow(
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.FromHours(8)))));

        var status = await completed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("无法确认保存结果", status, StringComparison.Ordinal);
        Assert.DoesNotContain("考试时间已保存", status, StringComparison.Ordinal);
        Assert.Equal(1, viewModel.LoadedScheduleVersion);
    }

    private static ExamConfigurationViewModel CreateFixedScheduleDraft(AdminApiClient api)
    {
        var draft = new ExamConfigurationViewModel(api)
        {
            ExamId = "synthetic-exam",
            ScheduledStartText = "2026-10-01 10:00 +08:00",
            ScheduledEndText = "2026-10-01 12:00 +08:00",
            ScheduleChangeReason = "合成回执校验"
        };
        draft.Subjects[0].Groups[0].QuestionCount = 10;
        return draft;
    }

    private static ExamVersionSnapshot Snapshot(ScheduleWindow schedule) => new(
        "synthetic-exam", new VersionVector(2, 0, 0), schedule,
        ExamLifecycle.Scheduled, null, null, null, null,
        new Dictionary<string, ParticipantVersionStatus>());

    private static Task<string> WaitForStatus(ExamConfigurationViewModel viewModel, string prefix)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.Status) && viewModel.Status.StartsWith(prefix, StringComparison.Ordinal))
                completion.TrySetResult(viewModel.Status);
        };
        return completion.Task;
    }

    private static HttpResponseMessage LoadedSchedule() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>("synthetic-server", "synthetic-node", 1, "test",
            Snapshot(new(new(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8)),
                         new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)))) with { Versions = new(1, 0, 0) }, null),
            options: JsonDefaults.Create())
    };

    private sealed class DeferredSaveHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _reply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Complete(ExamVersionSnapshot snapshot) => _reply.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>(
                "synthetic-server", "synthetic-node", 1, "test", snapshot, null),
                options: JsonDefaults.Create())
        });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(LoadedSchedule());
            _started.TrySetResult();
            return _reply.Task;
        }
    }

    private sealed class ConflictHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(LoadedSchedule());
            var status = request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.Conflict;
            var code = request.Method == HttpMethod.Get ? "EXAM_NOT_FOUND" : "VERSION_CONFLICT";
            var response = new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new ApiEnvelope<object>(
                    "synthetic-server", "synthetic-node", 1, "test", null,
                    new ApiError(code, $"Synthetic {code}")), options: JsonDefaults.Create())
            };
            return Task.FromResult(response);
        }
    }
}
