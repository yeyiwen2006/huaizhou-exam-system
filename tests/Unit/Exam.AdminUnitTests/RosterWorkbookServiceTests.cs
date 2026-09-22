using System.IO;
using ClosedXML.Excel;
using Exam.Admin.Services;

namespace Exam.AdminUnitTests;

public sealed class RosterWorkbookServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"exam-admin-tests-{Guid.NewGuid():N}");
    private readonly RosterWorkbookService _service = new();

    public RosterWorkbookServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ReadPreview_PreservesLeadingZerosAsText()
    {
        var path = CreateWorkbook(("张三", "001", "00001234"));

        var preview = _service.ReadPreview(path, []);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("001", row.ClassText);
        Assert.Equal("00001234", row.AdmissionNo);
        Assert.Equal(RosterPreviewState.New, row.State);
    }

    [Fact]
    public void ReadPreview_DetectsConflictAndPreventsWholePreviewApproval()
    {
        var path = CreateWorkbook(("李四", "2", "1001"), ("王五", "3", "1001"));

        var preview = _service.ReadPreview(path, []);

        Assert.True(preview.HasErrors);
        Assert.Equal(RosterPreviewState.Conflict, preview.Rows[1].State);
    }

    [Fact]
    public void ReadPreview_MarksIdenticalExistingRecordAsDuplicate()
    {
        var path = CreateWorkbook(("张三", "01", "001001"));
        var existing = new[] { new RosterEntryDraft("张三", "01", "001001") };

        var preview = _service.ReadPreview(path, existing);

        Assert.False(preview.HasErrors);
        Assert.Equal(0, preview.NewCount);
        Assert.Equal(1, preview.DuplicateCount);
    }

    [Fact]
    public void ReadPreview_RejectsNonNumericClassOrAdmissionNumber()
    {
        var path = CreateWorkbook(("张三", "高一1班", "A001"));

        var row = Assert.Single(_service.ReadPreview(path, []).Rows);

        Assert.Equal(RosterPreviewState.Error, row.State);
        Assert.Contains("班级只能包含数字", row.Message, StringComparison.Ordinal);
        Assert.Contains("准考证号只能包含数字", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTemplate_WritesExactThreeHeaders()
    {
        var path = Path.Combine(_directory, "template.xlsx");

        _service.CreateTemplate(path);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("姓名", sheet.Cell(1, 1).GetString());
        Assert.Equal("班级", sheet.Cell(1, 2).GetString());
        Assert.Equal("准考证号", sheet.Cell(1, 3).GetString());
    }

    private string CreateWorkbook(params (string Name, string ClassText, string AdmissionNo)[] rows)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("名册");
        sheet.Cell(1, 1).Value = "姓名";
        sheet.Cell(1, 2).Value = "班级";
        sheet.Cell(1, 3).Value = "准考证号";
        for (var index = 0; index < rows.Length; index++)
        {
            sheet.Cell(index + 2, 1).Value = rows[index].Name;
            sheet.Cell(index + 2, 2).Value = rows[index].ClassText;
            sheet.Cell(index + 2, 3).Value = rows[index].AdmissionNo;
            sheet.Cell(index + 2, 2).Style.NumberFormat.Format = "@";
            sheet.Cell(index + 2, 3).Style.NumberFormat.Format = "@";
        }
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
