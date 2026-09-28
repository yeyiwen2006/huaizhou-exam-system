using Exam.Contracts.Resources;
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
}
