using Exam.Domain.Reporting;

namespace Exam.Admin.Reporting;

// Presentation-only values. The server result owns ranks, row order, and means.
public enum ReportTableKind { StudentTotals, SubjectScores, SubjectAnswers, QuestionScores, ClassAverages }

public sealed record ReportTableColumn(string Header, double Width = 120);
public sealed record ReportTableCell(string Text, bool IsRed = false);
public sealed record ReportTableRow(IReadOnlyList<ReportTableCell> Cells);
public sealed record ReportTable(
    ReportTableKind Kind,
    string Title,
    string? SubjectId,
    IReadOnlyList<ReportTableColumn> Columns,
    IReadOnlyList<ReportTableRow> Rows,
    IReadOnlyList<string>? MeanFooter);

public static class ReportTableProjection
{
    public static IReadOnlyList<ReportTable> Build(ReportCalculationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var tables = new List<ReportTable> { StudentTotals(result) };
        foreach (var subject in result.Subjects)
        {
            tables.Add(SubjectScores(subject));
            tables.Add(SubjectAnswers(subject));
            tables.Add(QuestionScores(subject));
        }
        tables.Add(ClassAverages(result));
        return Array.AsReadOnly(tables.ToArray());
    }

    private static ReportTable StudentTotals(ReportCalculationResult result)
    {
        var columns = BaseColumns("总分排名");
        foreach (var subject in result.Subjects)
        {
            columns.Add(new($"{subject.Subject.DisplayName}成绩（满分 {Score(subject.Subject.MaximumScore)}）"));
            columns.Add(new($"{subject.Subject.DisplayName}排名"));
        }
        columns.Add(new($"总分（满分 {Score(result.TotalMaximumScore)}）"));

        var rows = result.StudentTotals.Select(row =>
        {
            var cells = BaseCells(row.TotalRank, row.Participant);
            foreach (var subject in row.Subjects)
            {
                cells.Add(new(Score(subject.Score)));
                cells.Add(new(subject.Rank.ToString()));
            }
            cells.Add(new(Score(row.TotalScore)));
            return new ReportTableRow(Array.AsReadOnly(cells.ToArray()));
        }).ToArray();

        var footer = BaseFooter();
        foreach (var subject in result.Subjects)
        {
            footer.Add(Mean(subject.SubjectMean));
            footer.Add(string.Empty);
        }
        footer.Add(Mean(result.TotalMean));
        return Make(ReportTableKind.StudentTotals, "学生成绩表", null, columns, rows, footer);
    }

    private static ReportTable SubjectScores(SubjectTables subject)
    {
        var columns = BaseColumns("排名");
        columns.Add(new($"成绩（满分 {Score(subject.Subject.MaximumScore)}）"));
        var rows = subject.Scores.Select(row =>
        {
            var cells = BaseCells(row.Rank, row.Participant);
            cells.Add(new(Score(row.Score)));
            return new ReportTableRow(Array.AsReadOnly(cells.ToArray()));
        }).ToArray();
        var footer = BaseFooter();
        footer.Add(Mean(subject.SubjectMean));
        return Make(ReportTableKind.SubjectScores, $"{subject.Subject.DisplayName}学生成绩表",
            subject.Subject.SubjectId, columns, rows, footer);
    }

    private static ReportTable SubjectAnswers(SubjectTables subject)
    {
        var columns = BaseColumns("排名");
        foreach (var question in subject.Subject.Questions)
            columns.Add(new($"第 {question.QuestionNo} 题", 150));
        columns.Add(new($"成绩（满分 {Score(subject.Subject.MaximumScore)}）"));
        var rows = subject.Answers.Select(row =>
        {
            var cells = BaseCells(row.Rank, row.Participant);
            cells.AddRange(row.Answers.Select(answer => new ReportTableCell(answer.Text, !answer.IsCompletelyCorrect)));
            cells.Add(new(Score(row.Score)));
            return new ReportTableRow(Array.AsReadOnly(cells.ToArray()));
        }).ToArray();
        return Make(ReportTableKind.SubjectAnswers, $"{subject.Subject.DisplayName}学生答案表",
            subject.Subject.SubjectId, columns, rows, null);
    }

    private static ReportTable QuestionScores(SubjectTables subject)
    {
        var columns = BaseColumns("排名");
        foreach (var question in subject.Subject.Questions)
            columns.Add(new($"第 {question.QuestionNo} 题（满分 {Score(question.MaximumScore)}）"));
        columns.Add(new($"成绩（满分 {Score(subject.Subject.MaximumScore)}）"));
        var rows = subject.QuestionScores.Select(row =>
        {
            var cells = BaseCells(row.Rank, row.Participant);
            cells.AddRange(row.Questions.Select(question => new ReportTableCell(Score(question.Score), !question.IsFullScore)));
            cells.Add(new(Score(row.Score)));
            return new ReportTableRow(Array.AsReadOnly(cells.ToArray()));
        }).ToArray();
        var footer = BaseFooter();
        footer.AddRange(subject.QuestionMeans.Select(question => Mean(question.Mean)));
        footer.Add(Mean(subject.SubjectMean));
        return Make(ReportTableKind.QuestionScores, $"{subject.Subject.DisplayName}学生小题得分表",
            subject.Subject.SubjectId, columns, rows, footer);
    }

    private static ReportTable ClassAverages(ReportCalculationResult result)
    {
        var columns = new List<ReportTableColumn> { new("班级") };
        foreach (var subject in result.Subjects)
        {
            columns.Add(new($"{subject.Subject.DisplayName}平均分（满分 {Score(subject.Subject.MaximumScore)}）"));
            columns.Add(new($"{subject.Subject.DisplayName}排名"));
        }
        columns.Add(new($"总平均分（满分 {Score(result.TotalMaximumScore)}）"));
        columns.Add(new("总平均分排名"));
        var rows = result.ClassAverages.Select(row =>
        {
            var cells = new List<ReportTableCell> { new(row.ClassNumber) };
            foreach (var subject in row.Subjects)
            {
                cells.Add(new(Mean(subject.Mean)));
                cells.Add(new(subject.Rank?.ToString() ?? string.Empty));
            }
            cells.Add(new(Mean(row.TotalMean)));
            cells.Add(new(row.TotalRank?.ToString() ?? string.Empty));
            return new ReportTableRow(Array.AsReadOnly(cells.ToArray()));
        }).ToArray();
        return Make(ReportTableKind.ClassAverages, "班级平均分表", null, columns, rows, null);
    }

    private static List<ReportTableColumn> BaseColumns(string rankHeader) =>
        [new(rankHeader, 90), new("准考证号", 130), new("姓名", 150), new("班级", 90)];

    private static List<ReportTableCell> BaseCells(int rank, ReportParticipant participant) =>
        [new(rank.ToString()), new(participant.ExamNumber), new(participant.DisplayName), new(participant.ClassNumber)];

    private static List<string> BaseFooter() => [string.Empty, string.Empty, string.Empty, string.Empty];

    private static string Score(Exam.Domain.Scoring.ExactRational score) => score.ToString();
    private static string Mean(ReportMean? mean) => mean?.Display ?? "无有效样本";

    private static ReportTable Make(ReportTableKind kind, string title, string? subjectId,
        List<ReportTableColumn> columns, ReportTableRow[] rows, List<string>? footer) =>
        new(kind, title, subjectId, Array.AsReadOnly(columns.ToArray()), Array.AsReadOnly(rows),
            footer is null ? null : Array.AsReadOnly(footer.ToArray()));
}
