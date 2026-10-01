using System.IO;
using System.IO.Compression;
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

public interface IImportSourceFileDialogService
{
    string? OpenImportDirectory();
    string? OpenImportZip();
    Task<string> ReadManifestFromDirectoryAsync(string directory, long maxBytes, CancellationToken cancellationToken = default);
    Task<string> ReadManifestFromZipAsync(string zipPath, long maxBytes, CancellationToken cancellationToken = default);
}

public sealed class FileDialogService : IFileDialogService, IManifestFileDialogService, IImportSourceFileDialogService
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

    public string? OpenImportDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择本地试题目录" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? OpenImportZip()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择本地试题 ZIP 包",
            Filter = "ZIP 包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public Task<string> ReadManifestFromDirectoryAsync(
        string directory, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("试题目录不存在。");
        return ReadManifestAsync(Path.Combine(directory, "manifest.json"), maxBytes, cancellationToken);
    }

    public async Task<string> ReadManifestFromZipAsync(
        string zipPath, long maxBytes, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var manifests = archive.Entries.Where(entry =>
            string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || !string.Equals(manifests[0].FullName, "manifest.json", StringComparison.Ordinal))
            throw new InvalidDataException("ZIP 根目录必须且只能有一个 manifest.json。未执行静态预检。");
        if (manifests[0].Length > maxBytes) throw new InvalidDataException("清单超过大小上限。");
        await using var stream = manifests[0].Open();
        return await ReadStrictUtf8Async(stream, maxBytes, cancellationToken);
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
        return await ReadStrictUtf8Async(stream, maxBytes, cancellationToken);
    }

    private static async Task<string> ReadStrictUtf8Async(
        Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes is < 1 or > int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
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
