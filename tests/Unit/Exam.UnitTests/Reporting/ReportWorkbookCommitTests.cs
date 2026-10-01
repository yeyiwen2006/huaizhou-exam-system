using Exam.Reporting;

namespace Exam.UnitTests.Reporting;

public sealed class ReportWorkbookCommitTests
{
    [Fact]
    public async Task DownloadCommit_RetriesTransientSharingAndRevalidatesBeforeReplacement()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing modes are the regression boundary.
        await WithDestination(async (directory, destination, original) =>
        {
            using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var calls = 0;
            var workbook = Workbook();
            await ReportWorkbookDownload.SaveAsync(workbook, destination, () =>
            {
                // The second validation can only run after an actual failed replacement.
                if (++calls == 2) held.Dispose();
                return Task.CompletedTask;
            });
            Assert.True(calls >= 2);
            Assert.Equal(workbook.Bytes, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    [Fact]
    public async Task DownloadCommit_RevocationDuringRetryPreservesOriginalAndRemovesTemporary()
    {
        if (!OperatingSystem.IsWindows()) return;
        await WithDestination(async (directory, destination, original) =>
        {
            using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var calls = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => ReportWorkbookDownload.SaveAsync(
                Workbook(), destination, () => ++calls == 1 ? Task.CompletedTask
                    : throw new InvalidDataException("Synthetic report snapshot revoked.")));
            Assert.Equal(2, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    [Fact]
    public async Task DownloadCommit_CancellationDuringRetryPreservesOriginalAndRemovesTemporary()
    {
        if (!OperatingSystem.IsWindows()) return;
        await WithDestination(async (directory, destination, original) =>
        {
            using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportWorkbookDownload.SaveAsync(
                Workbook(), destination, () =>
                {
                    if (++calls == 2) cancellation.Cancel();
                    return Task.CompletedTask;
                }, cancellation.Token));
            Assert.Equal(2, calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    [Fact]
    public async Task DownloadCommit_PermanentSharingFailureIsBoundedAndNeverChangesOriginal()
    {
        if (!OperatingSystem.IsWindows()) return;
        await WithDestination(async (directory, destination, original) =>
        {
            using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var calls = 0;
            var failure = await Record.ExceptionAsync(() => ReportWorkbookDownload.SaveAsync(
                Workbook(), destination, () => { calls++; return Task.CompletedTask; }));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.InRange(calls, 2, 21);
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    private static DownloadedWorkbook Workbook() => new([1, 2, 3, 4, 5],
        "synthetic-server", "synthetic-node", 1, "synthetic-snapshot");

    private static async Task WithDestination(Func<string, string, byte[], Task> action)
    {
        var directory = Directory.CreateTempSubdirectory("g-report-commit-synthetic-").FullName;
        try
        {
            var destination = Path.Combine(directory, "synthetic.xlsx");
            byte[] original = [9, 8, 7, 6];
            await File.WriteAllBytesAsync(destination, original);
            await action(directory, destination, original);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
