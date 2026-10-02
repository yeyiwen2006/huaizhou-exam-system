using System.IO;
using Microsoft.Win32;

namespace Exam.Admin.Services;

/// <summary>试卷导入页需要的选择器：材料包目录与节点公钥证书。</summary>
public interface IPaperImportFileDialogService
{
    /// <summary>选择导入包目录（含 manifest.json、questions、styles 与 server-only）。</summary>
    string? OpenPackageDirectory();

    /// <summary>选择学生包的节点公钥证书文件；节点标识取文件名（不含扩展名）。</summary>
    IReadOnlyList<string> OpenNodeCertificates();
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

    public IReadOnlyList<string> OpenNodeCertificates()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择学生包的加密接收节点公钥证书（文件名即节点标识）",
            Filter = "公钥证书 (*.cer;*.crt;*.der)|*.cer;*.crt;*.der|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
