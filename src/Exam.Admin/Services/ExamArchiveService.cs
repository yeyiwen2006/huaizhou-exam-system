using System.IO;
using Exam.Contracts.Archives;

namespace Exam.Admin.Services;

public enum AddExamChoice { New, Import }
public enum ArchiveImportChoice { Copy, Overwrite, Skip }
public sealed record ArchiveImportDecision(ArchiveImportChoice Choice, ExamArchiveConflict? Target = null);

public interface IExamArchiveDirectoryDialog
{
    string? ChooseExportDirectory();
    string? ChooseImportDirectory();
}

public interface IExamArchiveSource : IDisposable
{
    ExamArchivePreview Preview { get; }
    Task<ExamArchiveImportResult> ImportAsync(ArchiveImportDecision decision, string requestId, CancellationToken token);
}

public interface IExamArchiveService
{
    Task<string> ExportAsync(string examId, string examName, string directory, CancellationToken token);
    Task<IExamArchiveSource> OpenImportAsync(string directory, CancellationToken token);
}

/// <summary>Portable exam archives are stored on the operator's computer; server paths never enter the directory workflow.</summary>
public sealed class ExamArchiveService(AdminApiClient api) : IExamArchiveService
{
    public const string ArchiveFileName = ExamArchiveFormat.FileName;

    public async Task<string> ExportAsync(string examId, string examName, string directory, CancellationToken token)
    {
        var parent = Path.GetFullPath(directory);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("所选本地目录不存在。");
        var unique = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent, ".exam-export-" + unique);
        var name = string.Concat(examName.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c))).Trim(' ', '.');
        if (name.Length == 0) name = "考试";
        if (name.Length > 60) name = name[..60];
        var completed = Path.Combine(parent, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}_{unique[..8]}");
        Directory.CreateDirectory(staging);
        var file = Path.Combine(staging, ArchiveFileName);
        try
        {
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await api.DownloadExamArchiveAsync(examId, output, token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, completed);
            return completed;
        }
        finally
        {
            // Only the one file and directory allocated by this export may be removed.
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(staging)) Directory.Delete(staging);
        }
    }

    public async Task<IExamArchiveSource> OpenImportAsync(string directory, CancellationToken token)
    {
        var path = Path.Combine(Path.GetFullPath(directory), ArchiveFileName);
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            // Keep this same read-only handle across preview, operator choice and import.
            var generation = api.Generation;
            var preview = await api.PreviewExamArchiveAsync(source, token);
            return new Source(api, source, preview, generation);
        }
        catch { await source.DisposeAsync(); throw; }
    }

    private sealed class Source(AdminApiClient api, FileStream stream, ExamArchivePreview preview, long generation) : IExamArchiveSource
    {
        public ExamArchivePreview Preview => preview;
        public Task<ExamArchiveImportResult> ImportAsync(ArchiveImportDecision decision, string requestId, CancellationToken token)
        {
            if (generation != api.Generation) throw new OperationCanceledException("考务会话已切换，请重新选择目录。");
            stream.Position = 0;
            return api.ImportExamArchiveAsync(stream, decision, requestId, token);
        }
        public void Dispose() => stream.Dispose();
    }
}
