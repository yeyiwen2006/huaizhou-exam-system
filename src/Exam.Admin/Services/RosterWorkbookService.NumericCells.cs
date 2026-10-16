using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Exam.Admin.Services;

public sealed partial class RosterWorkbookService
{
    // Read the original decimal lexeme: a double alone cannot distinguish a fraction
    // such as 1.0000000000000001 from the integer to which it was rounded on loading.
    private static IReadOnlyDictionary<string, string> ReadStoredNumericValues(Stream stream)
    {
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new RosterImportException("工作簿结构无效。");
        var sheet = workbook.Workbook.Sheets?.Elements<Sheet>().SingleOrDefault()
            ?? throw new RosterImportException("名册必须只有一个工作表。");
        var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in part.Worksheet.Descendants<Cell>())
        {
            var address = cell.CellReference?.Value;
            if (address is null || address.Length < 2 || address[0] is not ('B' or 'C') ||
                !int.TryParse(address.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 2) continue;
            if ((cell.DataType is null || cell.DataType.Value == CellValues.Number) && cell.CellValue is { } value)
                result.Add(address, value.Text);
        }
        return result;
    }

    private static string ReadIdentityCell(IXLCell cell, string column,
        IReadOnlyDictionary<string, string> numericValues, List<string> issues)
    {
        string Fail(string reason)
        {
            issues.Add($"第 {cell.Address.RowNumber} 行“{column}”：{reason}");
            return string.Empty;
        }
        if (cell.DataType == XLDataType.Text)
        {
            // quotePrefix is style metadata, not a character to add or strip here.
            var text = ReadText(cell);
            return text.Contains('\'')
                ? Fail("实际内容含单引号，请核对原始名单并删除该字符；文本前缀标记不应写入内容。")
                : text;
        }
        if (cell.IsEmpty()) return string.Empty;
        if (cell.DataType != XLDataType.Number)
            return Fail("只接受数字文本或非负整数数字单元格，不接受日期、布尔值和错误值。");
        var address = cell.Address.ToStringRelative();
        if (!numericValues.TryGetValue(address, out var raw))
            return Fail("无法核对单元格原始数值，请按原始名单重新填写为文本。");
        return TryReadExactInteger(raw, out var digits, out var reason) ? digits : Fail(reason);
    }

    private static bool TryReadExactInteger(string raw, out string value, out string reason)
    {
        value = string.Empty;
        reason = "数值格式或精度无法可靠处理，请核对原始名单并以文本重新录入，不补造前导零或号码。";
        if (raw.Length > 128) return false;
        var match = StoredNumber().Match(raw);
        if (!match.Success) return false;
        var coefficient = match.Groups["coefficient"].Value;
        var exponent = 0;
        if (match.Groups["exponent"].Success && !int.TryParse(match.Groups["exponent"].Value,
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        if (exponent is < -10000 or > 10000) return false;
        var point = coefficient.IndexOf('.');
        var fractionalPlaces = point < 0 ? 0 : coefficient.Length - point - 1;
        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) { value = "0"; return true; }
        if (match.Groups["sign"].Value == "-")
        { reason = "数值不能为负数，请修正该单元格。"; return false; }
        var shift = exponent - fractionalPlaces;
        if (shift < 0)
        {
            var remove = -shift;
            if (remove >= digits.Length || digits.AsSpan(digits.Length - remove).ContainsAnyExcept('0'))
            { reason = "数值包含小数，必须是非负整数；不会通过四舍五入导入。"; return false; }
            digits = digits[..(digits.Length - remove)];
        }
        else
        {
            if (digits.Length + shift > 15)
            { reason = "数字单元格超过 15 位，可能已丢失号码精度；请核对原始名单并重新录入文本，不能据显示值猜测。"; return false; }
            digits += new string('0', shift);
        }
        if (digits.Length > 15)
        { reason = "数字单元格超过 15 位，可能已丢失号码精度；请核对原始名单并重新录入文本，不能据显示值猜测。"; return false; }
        value = digits;
        reason = string.Empty;
        return true;
    }

    [GeneratedRegex(@"\A(?<sign>[+-]?)(?<coefficient>(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))(?:[Ee](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoredNumber();
}
