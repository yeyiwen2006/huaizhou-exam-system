using Exam.Admin.Reporting;
using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.AdminUnitTests.Reporting;

/// <summary>All identifiers and scores are synthetic examples, not real candidate data.</summary>
public sealed partial class ReportTableProjectionTests
{
    [Theory]
    [InlineData("A.", "A")]
    [InlineData("A.C.D.", "ACD")]
    [InlineData("A．B．", "AB")]
    [InlineData("AB", "AB")]
    [InlineData("不作答", "不作答")]
    [InlineData("未作答", "未作答")]
    [InlineData("1.5", "1.5")]
    public void ExistingAnswers_DisplayLettersWithoutPrintedDots(string answer, string expected)
    {
        var result = CreateResult();
        var subject = result.Subjects[0];
        var row = subject.Answers[0];
        result = result with { Subjects = [subject with { Answers = [row with
        {
            Answers = [row.Answers[0] with { Text = answer }, row.Answers[1]]
        }] }] };

        var table = ReportTableProjection.Build(result)[2];

        Assert.Equal(expected, table.Rows[0].Cells[5].Text);
        Assert.True(table.Rows[0].Cells[5].IsRed);
    }

    [Fact]
    public void Projection_UsesServerOrderRanksMeansAndTwoIndependentColorFlags()
    {
        var tables = ReportTableProjection.Build(CreateResult());

        Assert.Equal(5, tables.Count);
        var totals = tables[0];
        Assert.Equal(ReportTableKind.StudentTotals, totals.Kind);
        Assert.Equal(["总分排名", "准考证号", "姓名", "班级"],
            totals.Columns.Take(4).Select(column => column.Header));
        Assert.Contains("总分（满分 10", totals.Columns[4].Header);
        Assert.Equal("7", totals.Rows[0].Cells[0].Text);
        Assert.Equal("3", totals.Rows[0].Cells[5].Text);
        Assert.Equal("7.00", totals.MeanFooter![4]);
        Assert.Equal("5.00", totals.MeanFooter[5]);
        Assert.Equal(string.Empty, totals.MeanFooter[^1]);

        var scores = tables[1];
        Assert.Equal(ReportTableKind.SubjectScores, scores.Kind);
        Assert.Equal("5.00", scores.MeanFooter![4]);
        Assert.Equal("3", scores.Rows[0].Cells[4].Text);

        var answers = tables[2];
        Assert.Equal(ReportTableKind.SubjectAnswers, answers.Kind);
        Assert.Null(answers.MeanFooter);
        Assert.StartsWith("成绩", answers.Columns[4].Header);
        Assert.Equal("第 1 题", answers.Columns[5].Header);
        Assert.True(answers.Rows[0].Cells[5].IsRed);
        Assert.False(answers.Rows[0].Cells[6].IsRed);

        var questionScores = tables[3];
        Assert.Equal(ReportTableKind.QuestionScores, questionScores.Kind);
        Assert.False(questionScores.Rows[0].Cells[5].IsRed);
        Assert.True(questionScores.Rows[0].Cells[6].IsRed);
        Assert.Equal("5.00", questionScores.MeanFooter![4]);
        Assert.Equal("4.00", questionScores.MeanFooter[5]);
        Assert.Equal("1.00", questionScores.MeanFooter[6]);

        var classes = tables[4];
        Assert.Equal(ReportTableKind.ClassAverages, classes.Kind);
        Assert.Null(classes.MeanFooter);
        Assert.Equal("02", classes.Rows[0].Cells[0].Text);
        Assert.Equal("无有效样本", classes.Rows[0].Cells[1].Text);
        Assert.Equal("", classes.Rows[0].Cells[2].Text);
    }

    [Fact]
    public void Projection_AddsThreeTablesPerSubjectInPublishedSubjectOrder()
    {
        var first = CreateResult();
        var second = first.Subjects[0] with
        {
            Subject = first.Subjects[0].Subject with { SubjectId = "s2", DisplayName = "Synthetic B", Order = 2 }
        };
        var result = first with { Subjects = [first.Subjects[0], second] };

        var tables = ReportTableProjection.Build(result);

        Assert.Equal(8, tables.Count);
        Assert.Equal([ReportTableKind.SubjectScores, ReportTableKind.SubjectAnswers,
            ReportTableKind.QuestionScores], tables.Skip(4).Take(3).Select(table => table.Kind));
        Assert.Equal("s2", tables[4].SubjectId);
        Assert.Equal("Synthetic B", tables[4].Title.Split('学')[0]);
    }

    [Fact]
    public void Projection_PreservesLongAnswerTextWithoutTruncatingTheValue()
    {
        var result = CreateResult();
        var answer = new string('X', 500);
        var source = result.Subjects[0];
        var answerRow = source.Answers[0] with
        {
            Answers = [source.Answers[0].Answers[0] with { Text = answer }, source.Answers[0].Answers[1]]
        };
        result = result with { Subjects = [source with { Answers = [answerRow] }] };

        var table = ReportTableProjection.Build(result)[2];

        Assert.Equal(answer, table.Rows[0].Cells[5].Text);
    }

    private static ReportCalculationResult CreateResult()
    {
        var participant = new ReportParticipant("synthetic-1", "0001", "Synthetic candidate", "02");
        var subject = new ReportSubject("s1", "Synthetic A", 1, R(10),
            [new("q1", 1, R(4)), new("q2", 2, R(6))]);
        var mean4 = new ReportMean(R(4), 1, "4.00");
        var mean1 = new ReportMean(R(1), 1, "1.00");
        var mean5 = new ReportMean(R(5), 1, "5.00");
        var mean7 = new ReportMean(R(7), 1, "7.00");
        var subjectTables = new SubjectTables(subject,
            [new(participant, R(3), 2)],
            [new(participant, 2, [new("q1", "A", false), new("q2", "B", true)], R(3))],
            [new(participant, 2, [new("q1", R(4), true), new("q2", R(1), false)], R(3))],
            mean5, [new("q1", mean4), new("q2", mean1)]);
        return new(
            [new(participant, R(3), 7, [new("s1", R(3), 2)])],
            [subjectTables],
            [new("02", 0, [new("s1", null, null)], null, null)],
            R(10), mean7, 1);
    }

    private static ExactRational R(int value) => ExactRational.FromInteger(value);
}
