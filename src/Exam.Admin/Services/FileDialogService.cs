using Microsoft.Win32;

namespace Exam.Admin.Services;

public interface IFileDialogService
{
    string? OpenExcelFile();
    string? SaveExcelFile(string suggestedName);
}

public sealed class FileDialogService : IFileDialogService
{
    private const string ExcelFilter = "Excel 工作簿 (*.xlsx)|*.xlsx";

    public string? OpenExcelFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = ExcelFilter,
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveExcelFile(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = ExcelFilter,
            FileName = suggestedName,
            AddExtension = true,
            DefaultExt = ".xlsx",
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
