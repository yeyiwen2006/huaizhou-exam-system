using Exam.Domain.Reporting;

namespace Exam.Infrastructure.Reporting;

/// <summary>
/// Saves one complete report workbook through a temporary file in the destination
/// directory. Cancellation is observed before replacement; a completed replacement
/// is the successful commit. The caller owns report authorization and validity.
/// </summary>
public sealed class ReportWorkbookFileWriter
{
    public async Task WriteAsync(ReportCalculationResult report, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(Path.GetFileName(destination)))
            throw new ArgumentException("导出目标必须是文件路径。", nameof(destinationPath));

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await Task.Run(() => new ReportWorkbookWriter().Write(report), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var temporary = Path.Combine(directory, ".g-report-" + Guid.NewGuid().ToString("N") + ".partial");
        var created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }
}
