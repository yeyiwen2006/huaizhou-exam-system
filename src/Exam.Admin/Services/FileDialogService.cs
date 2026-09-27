using System.IO;
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
    Task<string> ReadManifestAsync(string path, long maxBytes, CancellationToken cancellationToken = default);
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

    public async Task<string> ReadManifestAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxBytes is < 1 or > int.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var file = new FileInfo(path);
        if (file.Length > maxBytes)
        {
            throw new InvalidDataException("清单超过大小上限。");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[checked((int)maxBytes + 1)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0)
            {
                break;
            }
            count += read;
        }

        if (count > maxBytes)
        {
            throw new InvalidDataException("清单超过大小上限。");
        }

        var content = bytes.AsSpan(0, count);
        if (content.StartsWith(System.Text.Encoding.UTF8.Preamble))
        {
            content = content[System.Text.Encoding.UTF8.Preamble.Length..];
        }

        return new System.Text.UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(content);
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
