using Exam.Admin.Services;
using Exam.Admin.ViewModels;

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
}
