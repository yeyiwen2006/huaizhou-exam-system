using Microsoft.Win32;

namespace Exam.Admin.Services;

/// <summary>试卷导入页需要的选择器：材料包目录。</summary>
public interface IPaperImportFileDialogService
{
    /// <summary>选择导入包目录（含 manifest.json、questions、styles 与 server-only）。</summary>
    string? OpenPackageDirectory();
}

public sealed class PaperImportFileDialogService : IPaperImportFileDialogService
{
    public string? OpenPackageDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择导入包目录（含 manifest.json 与 server-only/answer-key.json）",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
