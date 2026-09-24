using Microsoft.Win32;

namespace Exam.Admin.Services;

public interface IFileDialogService
{
    string? OpenExcelFile();
    string? SaveExcelFile(string suggestedName);
}

public interface IManifestFileDialogService
{
    string? OpenManifestFile();
}

public sealed class FileDialogService : IFileDialogService, IManifestFileDialogService
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

    public string? OpenManifestFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "逐题清单 (manifest.json)|manifest.json|JSON 文件 (*.json)|*.json",
            FileName = "manifest.json",
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
