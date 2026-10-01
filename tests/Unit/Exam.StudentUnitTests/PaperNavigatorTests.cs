using Exam.Contracts.Resources;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>B03 左侧导航：科目按清单顺序、题组按 Order、题号方框按数值升序；状态映射。</summary>
public sealed class PaperNavigatorTests
{
    private static StudentPaperManifest CreateManifest() => new(
        1, "exam", "合成考试", 1,
        [new("math", "数学"), new("chem", "化学")],
        [
            new("g-m2", "math", "二、多项选择题", 2),
            new("g-m1", "math", "一、单项选择题", 1),
            new("g-c1", "chem", "一、不定项选择题", 1),
        ],
        [
            // 故意乱序：题号 10 在 2 前面，导航必须按数值升序。
            new(new("q10", 10, "math", "g-m2", "multiple", ["A", "B"]), "questions/合成考试_10.pdf", null),
            new(new("q1", 1, "math", "g-m1", "single", ["A", "B"]), "questions/合成考试_1.pdf", null),
            new(new("q5", 5, "chem", "g-c1", "indeterminate", ["A", "B", "C"], AllowAbstain: true), "questions/合成考试_5.pdf", null),
            new(new("q2", 2, "math", "g-m1", "single", ["A", "B"]), "questions/合成考试_2.pdf", null),
        ],
        []);

    [Fact]
    public void SectionsFollowSubjectOrderGroupOrderAndNumericQuestionOrder()
    {
        var navigator = new PaperNavigator(CreateManifest());
        var sections = navigator.BuildSections(null, null, null);

        // 科目按清单顺序；题组按 Order（一、二）；题号数值升序（1、2 / 10）。
        Assert.Equal(new[] { "数学", "数学", "化学" }, sections.Select(s => s.SubjectName));
        Assert.Equal(new[] { true, false, true }, sections.Select(s => s.ShowSubjectHeading));
        Assert.Equal(new[] { "一、单项选择题", "二、多项选择题", "一、不定项选择题" },
            sections.Select(s => s.GroupTitle));
        Assert.Equal(new[] { 1, 2 }, sections[0].Boxes.Select(b => b.QuestionNo));
        Assert.Equal(new[] { 10 }, sections[1].Boxes.Select(b => b.QuestionNo));
        Assert.Equal(new[] { 5 }, sections[2].Boxes.Select(b => b.QuestionNo));

        // 全卷题目按题号全局升序；首题为最小题号。
        Assert.Equal(new[] { 1, 2, 5, 10 }, navigator.Questions.Select(q => q.QuestionNo));
        Assert.Equal("q1", navigator.FirstQuestion().QuestionId);
    }

    [Fact]
    public void AnsweredMarkedAndCurrentStatesAreReflectedOnBoxes()
    {
        var navigator = new PaperNavigator(CreateManifest());
        var answers = new Dictionary<string, AnswerValue>(StringComparer.Ordinal)
        {
            ["q1"] = AnswerValue.Selected("A"),
            ["q5"] = AnswerValue.Abstain(),
            ["q2"] = AnswerValue.Empty(),
        };
        var marks = new HashSet<string>(StringComparer.Ordinal) { "q2" };

        var sections = navigator.BuildSections(answers, marks, "q1");

        var first = sections[0].Boxes;
        Assert.True(first[0].IsAnswered);    // 普通选项 → 绿
        Assert.True(first[0].IsCurrent);
        Assert.False(first[0].IsMarked);
        Assert.False(first[1].IsAnswered);   // Empty → 未作答颜色
        Assert.True(first[1].IsMarked);      // 私人标记 → 红点
        // “不作答”与普通选项同样视为已作答（q5）。
        Assert.True(sections[2].Boxes[0].IsAnswered);
    }

    [Fact]
    public void FindResolvesQuestionsById()
    {
        var navigator = new PaperNavigator(CreateManifest());
        Assert.Equal(10, navigator.Find("q10")?.QuestionNo);
        Assert.Null(navigator.Find("missing"));
        Assert.Null(navigator.Find(""));
    }

    [Theory]
    [InlineData("single", true, AnswerKind.Selected, "q2")]
    [InlineData("single", true, AnswerKind.Empty, null)]
    [InlineData("single", true, AnswerKind.Abstain, "q2")]
    [InlineData("single", false, AnswerKind.Abstain, null)]
    [InlineData("multiple", true, AnswerKind.Selected, null)]
    [InlineData("multiple", true, AnswerKind.Abstain, "q2")]
    [InlineData("indeterminate", true, AnswerKind.Selected, null)]
    [InlineData("indeterminate", true, AnswerKind.Abstain, "q2")]
    public void AutomaticAdvanceDependsOnQuestionTypeAndTheSavedSelection(string type, bool allowAbstain,
        AnswerKind kind, string? expected)
    {
        var template = CreateManifest();
        var manifest = template with
        {
            Questions = template.Questions.Select(q => q.Structure.QuestionId == "q1"
                ? q with { Structure = q.Structure with { QuestionType = type, AllowAbstain = allowAbstain } } : q).ToArray()
        };
        var answer = kind switch
        {
            AnswerKind.Selected => AnswerValue.Selected("A"),
            AnswerKind.Abstain => AnswerValue.Abstain(),
            _ => AnswerValue.Empty()
        };
        Assert.Equal(expected, new PaperNavigator(manifest).NextQuestionAfterSelection("q1", answer)?.QuestionId);
    }

    [Fact]
    public void AutomaticAdvanceUsesGlobalNumericOrderAndStopsAtTheLastQuestion()
    {
        var template = CreateManifest();
        var navigator = new PaperNavigator(template with
        {
            Questions = template.Questions.Select(q => q.Structure.QuestionId == "q10"
                ? q with { Structure = q.Structure with { QuestionType = "single" } } : q).ToArray()
        });
        Assert.Equal("q5", navigator.NextQuestionAfterSelection("q2", AnswerValue.Selected("A"))?.QuestionId);
        Assert.Equal("q10", navigator.NextQuestionAfterSelection("q5", AnswerValue.Abstain())?.QuestionId);
        Assert.Null(navigator.NextQuestionAfterSelection("q10", AnswerValue.Selected("A")));
        Assert.Null(navigator.NextQuestionAfterSelection("missing", AnswerValue.Selected("A")));
    }

    [Fact]
    public void NavigationDisplaysConfiguredScoresAndResetsGroupNumbersPerSubject()
    {
        var original = CreateManifest();
        var manifest = original with
        {
            SchemaVersion = StudentPaperPackage.NavigationSchemaVersion,
            Subjects = [new("math", "数学", "100"), new("chem", "化学", "40")],
            Groups = [
                new("g-m2", "math", "多项选择", 9, "4分"),
                new("g-m1", "math", "三、单项选择", 2, "3分"),
                new("g-c1", "chem", "1.不定项选择", 5, "{正确选项总数}分")]
        };
        var sections = new PaperNavigator(manifest).BuildSections(null, null, null);
        Assert.Equal(new[] { "数学（100分）", "数学（100分）", "化学（40分）" }, sections.Select(s => s.SubjectName));
        Assert.Equal(new[] { "一、单项选择题（每小题3分）", "二、多项选择题（每小题4分）",
            "一、不定项选择题（每小题{正确选项总数}分）" }, sections.Select(s => s.GroupTitle));
        Assert.Equal(new[] { true, false, true }, sections.Select(s => s.ShowSubjectHeading));
    }

    [Theory]
    [InlineData(10, "十")]
    [InlineData(11, "十一")]
    [InlineData(20, "二十")]
    [InlineData(101, "一百零一")]
    [InlineData(110, "一百一十")]
    public void GroupNumbersAreChineseOrdinalsAndCustomNamesArePreserved(int count, string expected)
    {
        var template = CreateManifest();
        var manifest = template with
        {
            Groups = Enumerable.Range(1, count).Select(i => new StudentPaperGroup($"g{i}", "math", "实验综合题", i, "3分/4分")).ToArray(),
            Questions = Enumerable.Range(1, count).Select(i => template.Questions[0] with
            {
                Structure = template.Questions[0].Structure with { QuestionId = $"q{i}", QuestionNo = i, GroupId = $"g{i}" }
            }).ToArray()
        };
        Assert.Equal($"{expected}、实验综合题（每小题3分/4分）",
            new PaperNavigator(manifest).BuildSections(null, null, null)[^1].GroupTitle);
    }
}
