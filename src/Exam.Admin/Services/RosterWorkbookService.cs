using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Exam.Admin.Services;

public sealed partial class RosterWorkbookService
{
    public const string NameHeader = "姓名";
    public const string ClassHeader = "班级";
    public const string AdmissionHeader = "准考证号";

    public void CreateTemplate(string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("名册");
        sheet.Cell(1, 1).Value = NameHeader;
        sheet.Cell(1, 2).Value = ClassHeader;
        sheet.Cell(1, 3).Value = AdmissionHeader;
        sheet.Range(1, 1, 1, 3).Style.Font.Bold = true;
        sheet.Columns(2, 3).Style.NumberFormat.Format = "@";
        sheet.Columns(1, 3).Width = 20;
        sheet.SheetView.FreezeRows(1);
        workbook.SaveAs(path);
    }

    public RosterPreview ReadPreview(string path, IReadOnlyCollection<RosterEntryDraft> existing)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.FirstOrDefault()
                    ?? throw new RosterImportException("工作簿中没有工作表。");

        ValidateHeaders(sheet);
        var rows = new List<RosterPreviewRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var admissionIndex = existing.ToDictionary(row => row.AdmissionNo, StringComparer.Ordinal);

        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            var name = ReadText(sheet.Cell(rowNumber, 1));
            var classText = ReadText(sheet.Cell(rowNumber, 2));
            var admissionNo = ReadText(sheet.Cell(rowNumber, 3));
            if (name.Length == 0 && classText.Length == 0 && admissionNo.Length == 0)
            {
                continue;
            }

            var issues = ValidateFields(name, classText, admissionNo).ToList();
            var state = issues.Count == 0 ? RosterPreviewState.New : RosterPreviewState.Error;
            if (issues.Count == 0 && admissionIndex.TryGetValue(admissionNo, out var prior))
            {
                if (StringComparer.Ordinal.Equals(prior.Name, name) &&
                    StringComparer.Ordinal.Equals(prior.ClassText, classText))
                {
                    state = RosterPreviewState.Duplicate;
                    issues.Add("该记录已存在，将跳过。 ");
                }
                else
                {
                    state = RosterPreviewState.Conflict;
                    issues.Add("准考证号与已有记录冲突。 ");
                }
            }

            var earlier = rows.FirstOrDefault(row =>
                StringComparer.Ordinal.Equals(row.AdmissionNo, admissionNo) && admissionNo.Length > 0);
            if (issues.Count == 0 && earlier is not null)
            {
                if (StringComparer.Ordinal.Equals(earlier.Name, name) &&
                    StringComparer.Ordinal.Equals(earlier.ClassText, classText))
                {
                    state = RosterPreviewState.Duplicate;
                    issues.Add($"与第 {earlier.RowNumber} 行完全重复，将跳过。 ");
                }
                else
                {
                    state = RosterPreviewState.Conflict;
                    issues.Add($"与第 {earlier.RowNumber} 行使用相同准考证号但信息不同。 ");
                }
            }

            rows.Add(new RosterPreviewRow(
                rowNumber,
                name,
                classText,
                admissionNo,
                state,
                string.Concat(issues).Trim()));
        }

        return new RosterPreview(rows);
    }

    public static IReadOnlyList<string> ValidateEntry(string name, string classText, string admissionNo) =>
        ValidateFields(name.Trim(), classText.Trim(), admissionNo.Trim()).ToArray();

    private static void ValidateHeaders(IXLWorksheet sheet)
    {
        var actual = Enumerable.Range(1, 3)
            .Select(column => ReadText(sheet.Cell(1, column)))
            .ToArray();
        var expected = new[] { NameHeader, ClassHeader, AdmissionHeader };
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new RosterImportException(
                $"表头必须依次为“{NameHeader}、{ClassHeader}、{AdmissionHeader}”，当前为“{string.Join('、', actual)}”。");
        }

        if (sheet.Row(1).CellsUsed().Any(cell => cell.Address.ColumnNumber > 3 && ReadText(cell).Length > 0))
        {
            throw new RosterImportException("名册模板只允许姓名、班级、准考证号三列。");
        }
    }

    private static IEnumerable<string> ValidateFields(string name, string classText, string admissionNo)
    {
        if (name.Length == 0)
        {
            yield return "姓名不能为空。 ";
        }

        if (classText.Length == 0)
        {
            yield return "班级不能为空。 ";
        }
        else if (!DigitsOnly().IsMatch(classText))
        {
            yield return "班级只能包含数字，并按文本保存。 ";
        }

        if (admissionNo.Length == 0)
        {
            yield return "准考证号不能为空。 ";
        }
        else if (!DigitsOnly().IsMatch(admissionNo))
        {
            yield return "准考证号只能包含数字，并按文本保存。 ";
        }
    }

    private static string ReadText(IXLCell cell) => cell.GetFormattedString().Trim();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnly();
}

public sealed record RosterEntryDraft(string Name, string ClassText, string AdmissionNo);

public enum RosterPreviewState
{
    New,
    Duplicate,
    Conflict,
    Error
}

public sealed record RosterPreviewRow(
    int RowNumber,
    string Name,
    string ClassText,
    string AdmissionNo,
    RosterPreviewState State,
    string Message)
{
    public string StateText => State switch
    {
        RosterPreviewState.New => "可导入",
        RosterPreviewState.Duplicate => "完全重复",
        RosterPreviewState.Conflict => "冲突",
        _ => "错误"
    };
}

public sealed record RosterPreview(IReadOnlyList<RosterPreviewRow> Rows)
{
    public bool HasErrors => Rows.Any(row => row.State is RosterPreviewState.Error or RosterPreviewState.Conflict);
    public int NewCount => Rows.Count(row => row.State == RosterPreviewState.New);
    public int DuplicateCount => Rows.Count(row => row.State == RosterPreviewState.Duplicate);
}

public sealed class RosterImportException(string message) : Exception(message);
