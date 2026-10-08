using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Domain.Exams;

namespace Exam.UnitTests.Exams;

public sealed class ExamSettingsRulesTests
{
    [Theory]
    [InlineData("100", true)]
    [InlineData("99", false)]
    public void SubjectMaximum_IncludesEveryQuestionType(string maximum, bool valid)
    {
        var subject = Settings().Subjects[0];
        subject = subject with { MaxScore = maximum, Groups =
        [
            subject.Groups[0],
            new("multiple", "多项组", "multiple", 2, 3, "25", false,
                null, new("3*{正确选项总数}", "{选择的正确选项数}", "0", "0")),
            subject.Groups[1] with { Order = 3, MaxScore = "15" }
        ] };

        var errors = ExamSettingsRules.Validate("考试", new([subject]));

        if (valid) Assert.Empty(errors);
        else Assert.Equal("科目“数学”的题组满分之和为 100，与科目满分 99 不一致。", Assert.Single(errors));
    }

    [Fact]
    public void SubjectMaximum_UsesExactDecimalAddition()
    {
        var subject = Settings().Subjects[0];
        var groups = new[]
        {
            subject.Groups[0] with { QuestionCount = 1, MaxScore = " 0.1 ", OneCorrect = new("0.1", "0", "0") },
            subject.Groups[1] with { MaxScore = "0.2" }
        };
        Assert.Empty(ExamSettingsRules.Validate("考试", new([subject with { MaxScore = "0.3", Groups = groups }])));
        Assert.Contains(ExamSettingsRules.Validate("考试", new([subject with { MaxScore = "0.300000000000000001", Groups = groups }])),
            error => error.Contains("题组满分之和为 0.3", StringComparison.Ordinal));
    }

    [Fact]
    public void SubjectMaximum_ChecksEachSubjectEvenWhenExamWideTotalsAgree()
    {
        var first = Settings().Subjects[0] with { MaxScore = "90" };
        var second = first with
        {
            SubjectId = "physics", Name = "物理", MaxScore = "110",
            Groups = first.Groups.Select(group => group with { GroupId = "physics-" + group.GroupId }).ToArray()
        };
        var errors = ExamSettingsRules.Validate("考试", new([first, second]));
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error => error.Contains("科目“数学”", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("科目“物理”", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidGroupMaximum_IsNotSilentlyCountedAsZero()
    {
        var subject = Settings().Subjects[0];
        var errors = ExamSettingsRules.Validate("考试", new([subject with
        {
            MaxScore = "60", Groups = [subject.Groups[0], subject.Groups[1] with { MaxScore = "abc" }]
        }]));
        Assert.Contains("满分必须是数字", Assert.Single(errors));
    }

    [Theory]
    [InlineData(20, "3", "60", true)]
    [InlineData(20, "3", "59", false)]
    [InlineData(3, "0.1", "0.3", true)]
    [InlineData(3, "0.1", "0.3000000000000000001", false)]
    [InlineData(3, "1/2", "1.5", true)]
    [InlineData(3, "2*{正确选项总数}+{选择的正确选项数}", "9", true)]
    public void SingleChoice_GroupMaximumMustEqualCountTimesCorrectScore(int count, string correct, string maximum, bool valid)
    {
        var subject = Settings().Subjects[0];
        var group = subject.Groups[0] with
        {
            QuestionCount = count, MaxScore = maximum, OneCorrect = new(correct, "0", "0")
        };
        var errors = ExamSettingsRules.Validate("考试", new([subject with { MaxScore = maximum, Groups = [group] }]));
        if (valid) Assert.Empty(errors);
        else Assert.Contains(errors, error => error.Contains("单项选择题满分不一致", StringComparison.Ordinal)
            && error.Contains(group.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void SingleChoice_ChecksEachGroupEvenIfTheirIncorrectMaximumsCancelOut()
    {
        var subject = Settings().Subjects[0];
        var group = subject.Groups[0] with { QuestionCount = 2, MaxScore = "5" };
        var second = group with { GroupId = "second", Name = "第二组", Order = 2, MaxScore = "7" };
        var errors = ExamSettingsRules.Validate("考试", new([subject with { MaxScore = "12", Groups = [group, second] }]));
        Assert.Equal(2, errors.Count(error => error.Contains("单项选择题满分不一致", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("multiple")]
    [InlineData("indeterminate")]
    public void OtherChoiceTypes_DoNotCheckCountTimesCorrectScore(string type)
    {
        var subject = Settings().Subjects[0];
        var group = subject.Groups[0] with
        {
            Type = type, QuestionCount = 3, MaxScore = "100",
            OneCorrect = type == "indeterminate" ? new("2", "0", "0") : null,
            MultipleCorrect = new("3*{正确选项总数}", "{选择的正确选项数}", "0", "0")
        };
        Assert.Empty(ExamSettingsRules.Validate("考试", new([subject with { Groups = [group] }])));
    }

    [Theory]
    [InlineData("1/({正确选项总数}-1)", "除零")]
    [InlineData("{正确选项总数}/3", "有限小数")]
    public void SingleChoice_InvalidEvaluatedCorrectScoreIsReported(string correct, string message)
    {
        var subject = Settings().Subjects[0];
        var group = subject.Groups[0] with { OneCorrect = new(correct, "0", "0") };
        Assert.Contains(ExamSettingsRules.Validate("考试", new([subject with { Groups = [group] }])),
            error => error.Contains(message, StringComparison.Ordinal));
    }

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
