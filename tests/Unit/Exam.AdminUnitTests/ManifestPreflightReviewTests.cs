using System.IO;
using System.Text;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed partial class ManifestPreflightViewModelTests
{
    [Fact]
    public async Task Review_LateReadErrorCannotOverwriteNewConnectionStatus()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("manifest.json");
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        var pending = viewModel.SelectManifestAsync();
        await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        api.Configure("https://localhost:5001/", "synthetic-token", "review-operator");
        files.Fail(new IOException("synthetic-private-path"));
        await pending;
        Assert.False(viewModel.IsPreflighting);
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);
        Assert.Contains("连接已变更", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-path", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_CancellingSelectionClearsPreviousValidatedResultWithoutReading()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("manifest.json") { SelectedPath = null };
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("minimal")));
        await viewModel.SelectManifestAsync();
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Null(viewModel.StructurePreview);
        Assert.Contains("已取消选择", viewModel.Status, StringComparison.Ordinal);
        Assert.False(files.WasRead);
    }

    [Fact]
    public async Task Review_MissingExamPreventsFileRead()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("manifest.json");
        var viewModel = new ManifestPreflightViewModel(api, files);
        await viewModel.SelectManifestAsync();
        Assert.False(files.WasRead);
        Assert.False(viewModel.IsPreflighting);
        Assert.Contains("请先填写当前考试 ID", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_NewerSelectionWinsEvenWhenOlderReadFailsLast()
    {
        using var api = new AdminApiClient();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new QueuedManifestFiles(new Queue<Task<string>>([first.Task, second.Task]));
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        var older = viewModel.SelectManifestAsync();
        var newer = viewModel.SelectManifestAsync();
        second.SetResult(File.ReadAllText(SampleManifest("minimal")));
        await newer;
        Assert.True(viewModel.StaticPreflightPassed);
        var status = viewModel.Status;
        first.SetException(new IOException("synthetic-old-failure"));
        await older;
        Assert.True(viewModel.StaticPreflightPassed);
        Assert.False(viewModel.IsPreflighting);
        Assert.Empty(viewModel.Issues);
        Assert.Equal(status, viewModel.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_RealReaderAcceptsExactUtf8ByteLimitAndOptionalBom(bool bom)
    {
        var directory = Directory.CreateTempSubdirectory("exam-c05-review-");
        try
        {
            const string json = "{\"examId\":\"合成审查\"}";
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bom) bytes = [.. Encoding.UTF8.Preamble, .. bytes];
            var path = Path.Combine(directory.FullName, "manifest.json");
            await File.WriteAllBytesAsync(path, bytes);
            var reader = new FileDialogService();
            Assert.Equal(json, await reader.ReadManifestAsync(path, bytes.Length));
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadManifestAsync(path, bytes.Length - 1));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class QueuedManifestFiles(Queue<Task<string>> reads) : IManifestFileDialogService
    {
        public string? OpenManifestFile() => "manifest.json";
        public Task<string> ReadManifestAsync(string path, long maxBytes, CancellationToken cancellationToken = default) =>
            reads.Dequeue().WaitAsync(cancellationToken);
    }
}
