using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Exam.AdminUnitTests;

public sealed class ExamConfigurationViewModelTests
{
    [Fact]
    public void DefaultDraft_MatchesD01ShapeAndPassesLocalValidation()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);

        var errors = viewModel.Validate();

        Assert.Empty(errors);
        var group = Assert.Single(Assert.Single(viewModel.Subjects).Groups);
        Assert.Equal("single", group.Type);
        Assert.Equal("1", group.Correct);
        Assert.Equal("0", group.OneBlank);
        Assert.Equal("0", group.OneWrong);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("exam:name")]
    [InlineData("考试.")]
    [InlineData("考试 ")]
    public void Validate_RejectsWindowsInvalidExamName(string name)
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api) { Name = name };

        Assert.Contains(viewModel.Validate(), error => error.Contains("Windows 文件名", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsUnknownScoringVariables()
    {
        using var api = new AdminApiClient();
        var viewModel = new ExamConfigurationViewModel(api);
        Assert.Single(Assert.Single(viewModel.Subjects).Groups).Correct = "{题目数} * 2";

        Assert.Contains(viewModel.Validate(), error => error.Contains("计分表达式", StringComparison.Ordinal));
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

        Assert.Contains(viewModel.Validate(), error => error.Contains("计分表达式", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveFailure_ShowsVersionConflictInsteadOfSuccess()
    {
        using var handler = new ConflictHandler();
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new ExamConfigurationViewModel(api);
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

        Assert.Contains("VERSION_CONFLICT", status, StringComparison.Ordinal);
        Assert.DoesNotContain("计划已保存", status, StringComparison.Ordinal);
    }

    private sealed class ConflictHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
