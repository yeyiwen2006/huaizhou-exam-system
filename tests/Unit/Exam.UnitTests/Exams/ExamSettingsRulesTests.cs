using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Domain.Exams;

namespace Exam.UnitTests.Exams;

public sealed class ExamSettingsRulesTests
{
    [Fact]
    public void ValidSettings_PassAndInstructionsFollowRequirementFormat()
    {
        Assert.Empty(ExamSettingsRules.Validate("合成模拟笔试", Settings()));
        Assert.Equal(
        [
            "数学（满分100分）",
            "一、单项选择题：本题共20小题，每小题3分，共计60分。选对得3分，不选得0分，选错得-0.75分。",
            "二、不定项选择题：本题共10小题，每小题3分或4分，共计40分。若有一个正确答案，选对得3分，不选得0分，选错得0分；若有多个正确答案，全部选对得4分，选对但不全得2分，不选得0分，选错得0分。本组允许选择“不作答”。",
        ], ExamSettingsRules.BuildInstructions(Settings()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("名称?")]
    [InlineData("结尾句点.")]
    public void InvalidNames_AreRejected(string name) =>
        Assert.Contains(ExamSettingsRules.Validate(name, Settings()), error => error.Contains("考试名称", StringComparison.Ordinal));

    [Fact]
    public void NameKey_IgnoresCaseAndSurroundingSpaces() =>
        Assert.Equal(ExamSettingsRules.NameKey("  Mock Exam "), ExamSettingsRules.NameKey("mock exam"));

    [Fact]
    public void RulesMustMatchQuestionType_AndUseOnlyAllowedExpressions()
    {
        var group = Settings().Subjects[0].Groups[0];
        var mismatched = Settings() with { Subjects = [Settings().Subjects[0] with { Groups = [group with { Type = "multiple" }] }] };
        Assert.Contains(ExamSettingsRules.Validate("考试", mismatched), error => error.Contains("与题型不符", StringComparison.Ordinal));

        var badExpression = Settings() with { Subjects = [Settings().Subjects[0] with { Groups =
            [group with { OneCorrect = new ManifestOneCorrect("{未知变量}", "0", "1/0") }] }] };
        Assert.Contains(ExamSettingsRules.Validate("考试", badExpression), error => error.Contains("计分规则只能", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIdsAndOrders_AreRejected()
    {
        var group = Settings().Subjects[0].Groups[0];
        var duplicate = Settings() with { Subjects = [Settings().Subjects[0] with { Groups = [group, group] }] };
        var errors = ExamSettingsRules.Validate("考试", duplicate);
        Assert.Contains(errors, error => error.Contains("编号为空或与其他题组重复", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("顺序必须", StringComparison.Ordinal));
        Assert.Contains(ExamSettingsRules.Validate("考试", new ExamSettings([])), error => error.Contains("至少设置一个科目", StringComparison.Ordinal));
    }

    private static ExamSettings Settings() => new(
    [
        new ExamSubjectSettings("math", "数学", "100",
        [
            new ExamGroupSettings("math-1", "一、单项选择题", "single", 1, 20, "60", false,
                new ManifestOneCorrect("3", "0", "-0.75"), null),
            new ExamGroupSettings("math-2", "二、不定项选择题", "indeterminate", 2, 10, "40", true,
                new ManifestOneCorrect("3", "0", "0"), new ManifestMultipleCorrect("4", "2", "0", "0")),
        ]),
    ]);
}
