using System.Globalization;
using ClosedXML.Excel;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.Infrastructure.Reporting;

/// <summary>Writes the five G report projections without recalculating scores or ranks.</summary>
public sealed class ReportWorkbookWriter
{
    public byte[] Write(ReportCalculationResult report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ReportResultShapeValidator.Validate(report);
        using var workbook = new XLWorkbook();
        workbook.Properties.Author = string.Empty;
        workbook.Properties.LastModifiedBy = string.Empty;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        WriteStudentTotals(workbook.AddWorksheet(SheetName("学生成绩", names)), report);
        foreach (var subject in report.Subjects)
        {
            WriteSubjectScores(workbook.AddWorksheet(SheetName(subject.Subject.DisplayName + "-成绩", names)),
                subject);
            WriteSubjectAnswers(workbook.AddWorksheet(SheetName(subject.Subject.DisplayName + "-答案", names)),
                subject);
            WriteQuestionScores(workbook.AddWorksheet(SheetName(subject.Subject.DisplayName + "-小题得分", names)),
                subject);
        }
        WriteClassAverages(workbook.AddWorksheet(SheetName("班级平均分", names)), report);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteStudentTotals(IXLWorksheet sheet, ReportCalculationResult report)
    {
        var headers = new List<string> { "总分排名", "准考证号", "姓名", "班级",
            "总分（满分" + ScoreText(report.TotalMaximumScore) + "）" };
        foreach (var subject in report.Subjects)
        {
            headers.Add(subject.Subject.DisplayName + "成绩（满分" + ScoreText(subject.Subject.MaximumScore) + "）");
            headers.Add(subject.Subject.DisplayName + "排名");
        }
        Header(sheet, headers);
        for (var index = 0; index < report.StudentTotals.Count; index++)
        {
            var source = report.StudentTotals[index];
            var row = index + 2;
            sheet.Cell(row, 1).Value = source.TotalRank;
            Identity(sheet, row, source.Participant);
            Score(sheet.Cell(row, 5), source.TotalScore);
            for (var subjectIndex = 0; subjectIndex < source.Subjects.Count; subjectIndex++)
            {
                var subject = source.Subjects[subjectIndex];
                Score(sheet.Cell(row, 6 + 2 * subjectIndex), subject.Score);
                sheet.Cell(row, 7 + 2 * subjectIndex).Value = subject.Rank;
            }
        }
        var footer = report.StudentTotals.Count + 2;
        MeanFooter(sheet, footer, headers.Count);
        MeanCell(sheet.Cell(footer, 5), report.TotalMean);
        for (var index = 0; index < report.Subjects.Count; index++)
            MeanCell(sheet.Cell(footer, 6 + 2 * index), report.Subjects[index].SubjectMean);
        Finish(sheet, headers.Count, footer);
    }

    private static void WriteSubjectScores(IXLWorksheet sheet, SubjectTables subject)
    {
        Header(sheet, ["排名", "准考证号", "姓名", "班级",
            "成绩（满分" + ScoreText(subject.Subject.MaximumScore) + "）"]);
        for (var index = 0; index < subject.Scores.Count; index++)
        {
            var source = subject.Scores[index];
            var row = index + 2;
            sheet.Cell(row, 1).Value = source.Rank;
            Identity(sheet, row, source.Participant);
            Score(sheet.Cell(row, 5), source.Score);
        }
        var footer = subject.Scores.Count + 2;
        MeanFooter(sheet, footer, 5);
        MeanCell(sheet.Cell(footer, 5), subject.SubjectMean);
        Finish(sheet, 5, footer);
    }

    private static void WriteSubjectAnswers(IXLWorksheet sheet, SubjectTables subject)
    {
        var headers = new List<string> { "排名", "准考证号", "姓名", "班级",
            "成绩（满分" + ScoreText(subject.Subject.MaximumScore) + "）" };
        headers.AddRange(subject.Subject.Questions.Select(question => "第" + question.QuestionNo + "题答案"));
        Header(sheet, headers);
        for (var index = 0; index < subject.Answers.Count; index++)
        {
            var source = subject.Answers[index];
            var row = index + 2;
            sheet.Cell(row, 1).Value = source.Rank;
            Identity(sheet, row, source.Participant);
            Score(sheet.Cell(row, 5), source.Score);
            for (var questionIndex = 0; questionIndex < source.Answers.Count; questionIndex++)
            {
                var cell = sheet.Cell(row, questionIndex + 6);
                Text(cell, ReportAnswerText.Format(source.Answers[questionIndex].Text));
                cell.Style.Font.FontColor = source.Answers[questionIndex].IsCompletelyCorrect
                    ? XLColor.Black : XLColor.Red;
            }
        }
        Finish(sheet, headers.Count, subject.Answers.Count + 1);
    }

    private static void WriteQuestionScores(IXLWorksheet sheet, SubjectTables subject)
    {
        var headers = new List<string> { "排名", "准考证号", "姓名", "班级",
            "成绩（满分" + ScoreText(subject.Subject.MaximumScore) + "）" };
        headers.AddRange(subject.Subject.Questions.Select(question => "第" + question.QuestionNo +
            "题得分（满分" + ScoreText(question.MaximumScore) + "）"));
        Header(sheet, headers);
        for (var index = 0; index < subject.QuestionScores.Count; index++)
        {
            var source = subject.QuestionScores[index];
            var row = index + 2;
            sheet.Cell(row, 1).Value = source.Rank;
            Identity(sheet, row, source.Participant);
            Score(sheet.Cell(row, 5), source.Score);
            for (var questionIndex = 0; questionIndex < source.Questions.Count; questionIndex++)
            {
                var cell = sheet.Cell(row, questionIndex + 6);
                Score(cell, source.Questions[questionIndex].Score);
                cell.Style.Font.FontColor = source.Questions[questionIndex].IsFullScore
                    ? XLColor.Black : XLColor.Red;
            }
        }
        var footer = subject.QuestionScores.Count + 2;
        MeanFooter(sheet, footer, headers.Count);
        MeanCell(sheet.Cell(footer, 5), subject.SubjectMean);
        for (var questionIndex = 0; questionIndex < subject.QuestionMeans.Count; questionIndex++)
            MeanCell(sheet.Cell(footer, questionIndex + 6), subject.QuestionMeans[questionIndex].Mean);
        Finish(sheet, headers.Count, footer);
    }

    private static void WriteClassAverages(IXLWorksheet sheet, ReportCalculationResult report)
    {
        var headers = new List<string> { "班级",
            "总平均分（满分" + ScoreText(report.TotalMaximumScore) + "）", "总平均分排名" };
        foreach (var subject in report.Subjects)
        {
            headers.Add(subject.Subject.DisplayName + "平均分（满分" +
                ScoreText(subject.Subject.MaximumScore) + "）");
            headers.Add(subject.Subject.DisplayName + "排名");
        }
        Header(sheet, headers);
        for (var index = 0; index < report.ClassAverages.Count; index++)
        {
            var source = report.ClassAverages[index];
            var row = index + 2;
            Text(sheet.Cell(row, 1), source.ClassNumber);
            MeanCell(sheet.Cell(row, 2), source.TotalMean);
            if (source.TotalRank is { } totalRank) sheet.Cell(row, 3).Value = totalRank;
            for (var subjectIndex = 0; subjectIndex < source.Subjects.Count; subjectIndex++)
            {
                MeanCell(sheet.Cell(row, 4 + 2 * subjectIndex), source.Subjects[subjectIndex].Mean);
                if (source.Subjects[subjectIndex].Rank is { } rank)
                    sheet.Cell(row, 5 + 2 * subjectIndex).Value = rank;
            }
        }
        Finish(sheet, headers.Count, report.ClassAverages.Count + 1);
    }

    private static void Identity(IXLWorksheet sheet, int row, ReportParticipant participant)
    {
        Text(sheet.Cell(row, 2), participant.ExamNumber);
        Text(sheet.Cell(row, 3), participant.DisplayName);
        Text(sheet.Cell(row, 4), participant.ClassNumber);
    }

    private static void Header(IXLWorksheet sheet, IReadOnlyList<string> headers)
    {
        for (var column = 0; column < headers.Count; column++) Text(sheet.Cell(1, column + 1), headers[column]);
        sheet.Range(1, 1, 1, headers.Count).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
    }

    private static void MeanFooter(IXLWorksheet sheet, int row, int lastColumn)
    {
        sheet.Range(row, 1, row, 4).Merge();
        Text(sheet.Cell(row, 1), "平均分");
        sheet.Range(row, 1, row, lastColumn).Style.Font.Bold = true;
    }

    private static void MeanCell(IXLCell cell, ReportMean? mean)
    {
        if (mean is null)
        {
            Text(cell, "无有效样本");
            return;
        }
        if (TryExcelNumber(mean.Display, out var number))
        {
            cell.Value = number;
            cell.Style.NumberFormat.Format = "0.00";
        }
        else
        {
            Text(cell, mean.Display);
        }
    }

    private static void Score(IXLCell cell, ExactRational score)
    {
        var text = ScoreText(score);
        if (!score.TryFormatFiniteDecimal(out _) || !TryExcelNumber(text, out var number))
        {
            Text(cell, text);
            return;
        }
        cell.Value = number;
        cell.Style.NumberFormat.Format = text.Contains('.') ? "0." +
            new string('#', text.Length - text.IndexOf('.') - 1) : "0";
    }

    private static string ScoreText(ExactRational score) => score.TryFormatFiniteDecimal(out var text)
        ? text : score.ToString();

    private static bool TryExcelNumber(string text, out double number)
    {
        number = 0;
        // Decimal.TryParse itself rounds sufficiently long fractions. Check the original
        // significant digits first, otherwise that rounding can falsely prove an exact round trip.
        var significantDigits = text.TrimStart('-').Replace(".", string.Empty, StringComparison.Ordinal)
            .TrimStart('0').TrimEnd('0').Length;
        if (significantDigits > 15 || text.TrimStart('-').Split('.')[0].Length > 15 ||
            (text.Contains('.') && text.Length - text.IndexOf('.') - 1 > 15) ||
            !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var original) ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
            !double.IsFinite(number) ||
            !decimal.TryParse(number.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var reconstructed) || original != reconstructed)
            return false;
        return true;
    }

    private static void Text(IXLCell cell, string value)
    {
        cell.Style.NumberFormat.Format = "@";
        // ClosedXML consumes one initial apostrophe as Excel's quote prefix, even for text cells.
        // Escape that prefix so an actual leading apostrophe survives save/readback unchanged.
        cell.Value = value.StartsWith('\'') ? "'" + value : value;
    }

    private static void Finish(IXLWorksheet sheet, int lastColumn, int lastRow)
    {
        sheet.Columns(1, lastColumn).Width = 18;
        sheet.Column(3).Width = 22;
        // 表头、数据与平均分行的文字一律水平、垂直居中（requirements.md 成绩统计一节）。
        var used = sheet.Range(1, 1, Math.Max(1, lastRow), lastColumn).Style.Alignment;
        used.Horizontal = XLAlignmentHorizontalValues.Center;
        used.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static string SheetName(string requested, HashSet<string> used)
    {
        var cleaned = new string(requested.Select(character =>
            character is ':' or '\\' or '/' or '?' or '*' or '[' or ']' or '\'' ? '_' : character).ToArray())
            .Trim().TrimStart('[').TrimEnd(']');
        if (cleaned.Length == 0) cleaned = "报表";
        var index = 1;
        while (true)
        {
            var suffix = index == 1 ? string.Empty : "~" + index.ToString(CultureInfo.InvariantCulture);
            var length = Math.Min(cleaned.Length, 31 - suffix.Length);
            if (length > 0 && char.IsHighSurrogate(cleaned[length - 1])) length--;
            var prefix = cleaned[..length];
            var candidate = prefix + suffix;
            if (used.Add(candidate)) return candidate;
            index++;
        }
    }
}
