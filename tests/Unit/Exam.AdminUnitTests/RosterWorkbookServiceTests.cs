using System.IO;
using ClosedXML.Excel;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ImportPreview_RejectsWholeBatchWhenAnyPositionHasError(int badIndex)
    {
        var rows = new[]
        {
            ("考生甲", "001", "00001"),
            ("考生乙", "002", "00002"),
            ("考生丙", "003", "00003")
        };
        rows[badIndex].Item1 = string.Empty;
        var path = CreateWorkbook(rows);
        var viewModel = new RosterViewModel(_service, new StubDialogs(path));

        viewModel.ImportPreviewCommand.Execute(null);
        viewModel.ConfirmImportCommand.Execute(null);

        Assert.Equal(3, viewModel.PreviewRows.Count);
        Assert.Equal(badIndex + 2, viewModel.PreviewRows[badIndex].RowNumber);
        Assert.False(viewModel.ConfirmImportCommand.CanExecute(null));
        Assert.Empty(viewModel.Entries);
    }

    [Fact]
    public void RepeatedImport_DoesNotCreateDuplicateRows()
    {
        var path = CreateWorkbook(("合成考生", "00125", "0000125"));
        var viewModel = new RosterViewModel(_service, new StubDialogs(path));

        viewModel.ImportPreviewCommand.Execute(null);
        viewModel.ConfirmImportCommand.Execute(null);
        viewModel.ImportPreviewCommand.Execute(null);

        var row = Assert.Single(viewModel.Entries);
        Assert.Equal("00125", row.ClassText);
        Assert.Equal("0000125", row.AdmissionNo);
        Assert.Equal(RosterPreviewState.Duplicate, Assert.Single(viewModel.PreviewRows).State);
        Assert.False(viewModel.ConfirmImportCommand.CanExecute(null));
    }

    [Fact]
    public void ManualEditAfterPreview_InvalidatesStaleImportApproval()
    {
        var path = CreateWorkbook(("合成考生甲", "001", "00001"));
        var viewModel = new RosterViewModel(_service, new StubDialogs(path));
        viewModel.ImportPreviewCommand.Execute(null);
        Assert.True(viewModel.ConfirmImportCommand.CanExecute(null));

        viewModel.Name = "合成考生乙";
        viewModel.ClassText = "002";
        viewModel.AdmissionNo = "00002";
        viewModel.AddManualCommand.Execute(null);

        Assert.False(viewModel.ConfirmImportCommand.CanExecute(null));
        Assert.Empty(viewModel.PreviewRows);
        Assert.Single(viewModel.Entries);
    }

    [Fact]
    public void Preview_HandlesThreeHundredSyntheticCandidates()
    {
        var rows = Enumerable.Range(1, 300)
            .Select(number => ($"合成考生{number}", $"{number % 10:D3}", $"{number:D5}"))
            .ToArray();
        var path = CreateWorkbook(rows);

        var preview = _service.ReadPreview(path, []);

        Assert.False(preview.HasErrors);
        Assert.Equal(300, preview.NewCount);
        Assert.Equal("00001", preview.Rows[0].AdmissionNo);
        Assert.Equal("00300", preview.Rows[^1].AdmissionNo);
    }

    [Fact]
    public void Preview_RejectsFormulaAndUnexpectedFourthColumn()
    {
        var path = CreateWorkbook(("合成考生甲", "001", "00001"), ("合成考生乙", "002", "00002"));
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(1);
            sheet.Cell(2, 1).FormulaA1 = "1+1";
            sheet.Cell(3, 4).Value = "unexpected";
            workbook.Save();
        }

        var preview = _service.ReadPreview(path, []);

        Assert.True(preview.HasErrors);
        Assert.Contains("公式", preview.Rows[0].Message, StringComparison.Ordinal);
        Assert.Contains("三列", preview.Rows[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_RejectsWrongHeaderBeforeAnyImport()
    {
        var path = CreateWorkbook(("合成考生", "001", "00001"));
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet(1).Cell(1, 3).Value = "学号";
            workbook.Save();
        }

        Assert.Throws<RosterImportException>(() => _service.ReadPreview(path, []));
    }

    [Fact]
    public void Preview_RejectsOversizedFileBeforeWorkbookParsing()
    {
        var path = Path.Combine(_directory, "oversized.xlsx");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(10 * 1024 * 1024 + 1);
        }

        var error = Assert.Throws<RosterImportException>(() => _service.ReadPreview(path, []));

        Assert.Contains("10 MB", error.Message, StringComparison.Ordinal);
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

    private sealed class StubDialogs(string path) : IFileDialogService
    {
        public string? OpenExcelFile() => path;
        public string? SaveExcelFile(string suggestedName) => null;
    }
}
