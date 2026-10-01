using Exam.Admin.Reporting;
using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.AdminUnitTests.Reporting;

/// <summary>成绩表按姓名或准考证号查询。所有考生与成绩均为合成数据。</summary>
public sealed class ReportStudentSearchTests
{
    [Fact]
    public void Search_KeepsOnlyMatchingRowsWithSameColumnsRanksColoursAndFullAverage()
    {
        using var session = Loaded();
        var full = session.SelectedTable!;
        Assert.Equal(ReportTableKind.StudentTotals, full.Kind);

        session.SearchText = "合成考生02";
        session.SearchCommand.Execute(null);

        var shown = session.DisplayedTable!;
        var row = Assert.Single(shown.Rows);
        Assert.Equal("合成考生02", row.Cells[2].Text);
        Assert.Same(full.Columns, shown.Columns);
        // 排名与完整表格中的同一行完全一致，不因查询重新排名。
        Assert.Equal(full.Rows.Single(item => item.Cells[2].Text == "合成考生02"), row);
        // 底部平均分仍是全部合格样本的结果。
        Assert.Equal(full.MeanFooter, shown.MeanFooter);
        Assert.True(session.IsSearching);
        Assert.Contains("找到 1 名学生", session.SearchResultText, StringComparison.Ordinal);

        session.ClearSearchCommand.Execute(null);
        Assert.Same(full, session.DisplayedTable);
        Assert.False(session.IsSearching);
        Assert.Equal(string.Empty, session.SearchText);
    }

    [Theory]
    [InlineData("000003", "合成考生03")]
    [InlineData("0003", "合成考生03")]
    [InlineData("考生04", "合成考生04")]
    public void Search_MatchesAdmissionNumberOrNameByPart(string query, string expectedName)
    {
        using var session = Loaded();
        session.SearchText = $"  {query}  ";
        session.SearchCommand.Execute(null);
        Assert.Equal(expectedName, Assert.Single(session.DisplayedTable!.Rows).Cells[2].Text);
    }

    [Fact]
    public void Search_AppliesToEveryStudentTableAndKeepsAnswerColouring()
    {
        using var session = Loaded();
        session.SearchText = "合成考生01";
        session.SearchCommand.Execute(null);
        foreach (var table in session.Tables.Where(table => table.Kind != ReportTableKind.ClassAverages))
        {
            session.SelectedTable = table;
            var full = table.Rows.Single(row => row.Cells[2].Text == "合成考生01");
            Assert.Equal(full, Assert.Single(session.DisplayedTable!.Rows));
            Assert.Equal(table.MeanFooter, session.DisplayedTable.MeanFooter);
        }
        var answers = session.Tables.First(table => table.Kind == ReportTableKind.SubjectAnswers);
        session.SelectedTable = answers;
        // 合成考生01 第 1 题答错，答案表仍按完整表格标红。
        Assert.True(session.DisplayedTable!.Rows[0].Cells[5].IsRed);
    }

    [Fact]
    public void Search_NoMatchShowsEmptyTableWithAverageAndNotice()
    {
        using var session = Loaded();
        session.SearchText = "不存在的考生";
        session.SearchCommand.Execute(null);
        Assert.Empty(session.DisplayedTable!.Rows);
        Assert.NotNull(session.DisplayedTable.MeanFooter);
        Assert.Contains("未找到", session.SearchResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_IsDisabledForClassAverageTable()
    {
        using var session = Loaded();
        session.SearchText = "合成考生01";
        session.SearchCommand.Execute(null);
        var classes = session.Tables.Single(table => table.Kind == ReportTableKind.ClassAverages);
        session.SelectedTable = classes;
        Assert.False(session.CanSearch);
        Assert.Same(classes, session.DisplayedTable);
        Assert.False(session.SearchCommand.CanExecute(null));
    }

    [Fact]
    public void Search_IsClearedWhenReportIsInvalidatedOrReloaded()
    {
        using var session = Loaded();
        session.SearchText = "合成考生01";
        session.SearchCommand.Execute(null);
        session.Invalidate();
        Assert.False(session.IsSearching);
        Assert.Equal(string.Empty, session.SearchText);
        Assert.Null(session.DisplayedTable);

        var ticket = session.BeginLoad("synthetic-exam");
        Assert.True(session.TryAcceptPublished(ticket, Result()));
        Assert.Equal(4, session.DisplayedTable!.Rows.Count);
    }

    [Fact]
    public void Search_EmptyTextCannotBeApplied()
    {
        using var session = Loaded();
        session.SearchText = "   ";
        Assert.False(session.SearchCommand.CanExecute(null));
        session.SearchCommand.Execute(null);
        Assert.False(session.IsSearching);
    }

    private static ReportTableSessionViewModel Loaded()
    {
        var session = new ReportTableSessionViewModel();
        Assert.True(session.TryAcceptPublished(session.BeginLoad("synthetic-exam"), Result()));
        return session;
    }

    private static ReportCalculationResult Result()
    {
        var subjects = new[]
        {
            new ReportSubject("s1", "合成科目", 1, R(10),
                [new ReportQuestion("q1", 1, R(5)), new ReportQuestion("q2", 2, R(5))]),
        };
        var participants = Enumerable.Range(1, 4).Select(number => new ReportParticipant(
            $"synthetic-candidate-{number}", number.ToString("000000"), $"合成考生{number:00}", "01")).ToArray();
        var submissions = participants.Select((participant, index) =>
        {
            // 考生01 第 1 题答错；其余考生全部答对，便于检查着色。
            var firstCorrect = index != 0;
            var questions = new[]
            {
                new ReportQuestionGrade("q1", firstCorrect ? "A" : "B",
                    firstCorrect ? AnswerOutcome.Correct : AnswerOutcome.Wrong, R(firstCorrect ? 5 : 0)),
                new ReportQuestionGrade("q2", "A", AnswerOutcome.Correct, R(5)),
            };
            var total = questions.Aggregate(R(0), (sum, item) => sum + item.Score);
            return new ReportSubmission($"synthetic-submission-{index}", "synthetic-exam", participant.CandidateId, 1,
                [new ReportSubjectGrade("s1", total, questions)], total);
        }).ToArray();
        return ReportCalculator.Calculate(new ReportCalculationInput("synthetic-exam", 1, subjects, participants, submissions));
    }

    private static ExactRational R(int value) => ExactRational.FromInteger(value);
}
