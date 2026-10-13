using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Exam.Admin;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Archives;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;

namespace Exam.AdminUnitTests;

public sealed class ExamArchiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "exam-archive-client-" + Guid.NewGuid().ToString("N"));
    private static readonly ExamArchivePreview Preview = new(1, "source-exam", "合成考试", DateTimeOffset.UtcNow,
        [new("existing-exam", "已有的合成考试", 7)]);
    private static readonly ExamSummary Summary = new("imported-exam", "合成考试（副本）", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddHours(1), ExamLifecycle.Scheduled, false, false, 0, 0, 1);

    public ExamArchiveTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExportCreatesANewLocalDirectoryAndKeepsPreviouslyExportedFiles()
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic portable archive");
        using var api = Configured(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/exams/source-exam/archive", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Archive(bytes));
        }));
        var service = new ExamArchiveService(api);
        var first = await service.ExportAsync("source-exam", "合成考试", _directory, default);
        var second = await service.ExportAsync("source-exam", "合成考试", _directory, default);
        Assert.NotEqual(first, second);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(first, ExamArchiveFormat.FileName)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(second, ExamArchiveFormat.FileName)));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_directory), path => Path.GetFileName(path).StartsWith(".exam-export-"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidArchiveDownloadRemovesOnlyItsUnfinishedLocalFiles(bool missingIdentity)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "keep.txt"), "existing");
        using var api = Configured(new Handler(_ =>
        {
            var response = Archive([1, 2, 3]);
            if (missingIdentity) response.Headers.Remove("X-Exam-Server-Id");
            else response.Content.Headers.ContentLength = 99;
            return Task.FromResult(response);
        }));
        var service = new ExamArchiveService(api);
        await Assert.ThrowsAnyAsync<Exception>(() => service.ExportAsync("source-exam", "合成考试", _directory, default));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_directory, "keep.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_directory));
    }

    [Fact]
    public async Task PreviewAndOverwriteUseTheSameReadLockedArchiveAndExplicitTargetVersion()
    {
        var path = Path.Combine(_directory, ExamArchiveFormat.FileName);
        await File.WriteAllTextAsync(path, "synthetic archive bytes");
        var seen = new List<string>();
        using var api = Configured(new Handler(async request =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content);
            var fields = new Dictionary<string, string>();
            foreach (var part in parts)
                fields.Add(part.Headers.ContentDisposition!.Name!.Trim('"'), await part.ReadAsStringAsync());
            seen.Add(fields["file"]);
            if (request.RequestUri!.AbsolutePath.EndsWith("/preview")) return Success(Preview);
            Assert.Equal("overwrite", fields["mode"]);
            Assert.Equal("existing-exam", fields["expectedTargetExamId"]);
            Assert.Equal("7", fields["expectedTargetVersion"]);
            Assert.Equal("stable-import-request", fields["requestId"]);
            return Success(new ExamArchiveImportResult("existing-exam", "已有的合成考试", true, false));
        }));
        var service = new ExamArchiveService(api);
        using (var source = await service.OpenImportAsync(_directory, default))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed after preview"));
            var result = await source.ImportAsync(new(ArchiveImportChoice.Overwrite, source.Preview.Conflicts[0]), "stable-import-request", default);
            Assert.True(result.Imported);
        }
        Assert.Equal(new[] { "synthetic archive bytes", "synthetic archive bytes" }, seen);
        await File.WriteAllTextAsync(path, "unlocked after use");
    }

    [Theory]
    [InlineData(ArchiveImportChoice.Copy)]
    [InlineData(ArchiveImportChoice.Overwrite)]
    [InlineData(ArchiveImportChoice.Skip)]
    public async Task ExistingExamChoiceControlsOnlyTheExplicitImport(ArchiveImportChoice choice)
    {
        using var api = Configured(new Handler(_ => Task.FromResult(Success(new[] { Summary }))));
        var source = new Source(Preview); var service = new Service(source);
        using var home = new ExamHomeViewModel(api, service, new Directories(_directory));
        home.ChooseArchiveConflictAsync = preview => Task.FromResult<ArchiveImportDecision?>(
            new(choice, choice == ArchiveImportChoice.Overwrite ? preview.Conflicts[0] : null));
        await home.ImportArchiveAsync();
        Assert.True(source.Disposed);
        Assert.Equal(choice == ArchiveImportChoice.Skip ? 0 : 1, source.Imports);
        if (choice == ArchiveImportChoice.Skip) Assert.Contains("已跳过", home.ArchiveStatus);
        else
        {
            Assert.Equal(choice, source.Decision!.Choice);
            Assert.False(string.IsNullOrWhiteSpace(source.RequestId));
            Assert.Single(home.Exams); Assert.Contains("已导入", home.ArchiveStatus);
            if (choice == ArchiveImportChoice.Overwrite) Assert.Equal(Preview.Conflicts[0], source.Decision.Target);
        }
    }

    [Fact]
    public async Task ImportWithoutConflictCreatesCopyAndSessionSwitchCannotSubmitOldPreview()
    {
        using var api = Configured(new Handler(_ => Task.FromResult(Success(new[] { Summary }))));
        var noConflict = new Source(Preview with { Conflicts = [] });
        var service = new Service(noConflict);
        using var home = new ExamHomeViewModel(api, service, new Directories(_directory));
        await home.ImportArchiveAsync();
        Assert.Equal(ArchiveImportChoice.Copy, noConflict.Decision!.Choice);
        var delayed = new Source(Preview);
        service.OpenResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var importing = home.ImportArchiveAsync();
        api.Configure("https://other.example.test/", "other-synthetic-token", "other-operator");
        service.OpenResponse.SetResult(delayed); await importing;
        Assert.Equal(0, delayed.Imports); Assert.True(delayed.Disposed);
        Assert.Equal(string.Empty, home.ArchiveStatus); Assert.False(home.IsArchiveBusy);
    }

    [Fact]
    public async Task AddExamRoutesNewChoiceToTheExistingCreationFlow()
    {
        using var api = Configured(new Handler(_ => throw new InvalidOperationException("No request expected.")));
        var service = new Service(new Source(Preview));
        using var home = new ExamHomeViewModel(api, service, new Directories(_directory));
        var requested = 0; home.NewExamRequested += () => requested++;
        home.ChooseAddExamAsync = () => Task.FromResult<AddExamChoice?>(AddExamChoice.New);
        await home.AddExamAsync();
        Assert.Equal(1, requested); Assert.Equal(0, service.Opens);
    }

    [Fact]
    [Trait("Category", "Wpf")]
    public Task OverwritingTheOpenedExamInvalidatesOldMaterialsAndReportLoads() => ExamCatalogViewModelTests.OnStaAsync(async () =>
    {
        var existing = Summary with { ExamId = "existing-exam", Name = "已有的合成考试" };
        using var api = Configured(new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/exams" => Success(new[] { existing }),
            "/api/v1/exams/existing-exam" => Success(new ExamDetail(existing, new([]), 1, [])),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = JsonContent.Create(new ApiEnvelope<object>("archive-server", "archive-node", 1, "synthetic", null, new(ErrorCodes.NotFound, "合成测试记录不存在。"))) }
        })));
        var source = new Source(Preview);
        using var main = new MainViewModel(api, archiveService: new Service(source), archiveDirectories: new Directories(_directory));
        main.OpenExam(existing); await main.Exam.OpenAsync(existing.ExamId);
        main.SelectedPage = MainViewModel.HomePage;
        main.Import.SourcePath = @"C:\synthetic-old-materials";
        var oldReport = main.Reports.Session.BeginLoad(existing.ExamId);
        main.Home.ChooseArchiveConflictAsync = preview => Task.FromResult<ArchiveImportDecision?>(new(ArchiveImportChoice.Overwrite, preview.Conflicts[0]));
        await main.Home.ImportArchiveAsync();
        Assert.Equal(existing.ExamId, main.CurrentExam!.ExamId);
        Assert.Equal(existing.ExamId, main.Import.ExamId);
        Assert.Equal(string.Empty, main.Import.SourcePath);
        Assert.True(oldReport.CancellationToken.IsCancellationRequested);
        Assert.False(main.Reports.Session.TryFail(oldReport));
        Assert.Equal(existing.ExamId, main.Exam.ExamId);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task ArchiveButtonHandlesItsClickWithoutOpeningTheExamCard() => ExamCatalogViewModelTests.OnStaAsync(() =>
    {
        using var api = Configured(new Handler(_ => Task.FromResult(Success(Array.Empty<ExamSummary>()))));
        using var main = new MainViewModel(api);
        var window = new MainWindow(main);
        try
        {
            var opened = 0; var exported = 0;
            var card = new ExamCardViewModel(Summary, () => opened++, export: _ => { exported++; return Task.CompletedTask; });
            var template = (DataTemplate)window.Resources["ExamCardTemplate"];
            var presenter = new ContentPresenter { Content = card, ContentTemplate = template, Resources = window.Resources };
            presenter.ApplyTemplate(); presenter.Measure(new Size(1100, 500)); presenter.Arrange(new Rect(0, 0, 1100, 500));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            var root = Descendants(presenter).OfType<Button>().First();
            var button = Descendants(root).OfType<Button>().Single(b => Equals(b.Content, "导出本次考试的全部数据"));
            var bubbled = false; root.Click += (_, _) => bubbled = true;
            var click = new RoutedEventArgs(Button.ClickEvent, button); button.RaiseEvent(click);
            Assert.True(click.Handled); Assert.False(bubbled);
            Assert.Same(card, button.DataContext);
            Assert.Same(card.ExportArchiveCommand, button.Command);
            button.Command!.Execute(null);
            Assert.Equal(1, exported); Assert.Equal(0, opened);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static AdminApiClient Configured(HttpMessageHandler handler)
    { var api = new AdminApiClient(handler); api.Configure("https://exam.example.test/", "synthetic-token", "synthetic-operator"); return api; }
    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("archive-server", "archive-node", 1, "synthetic", value, null), options: JsonDefaults.Create()) };
    private static HttpResponseMessage Archive(byte[] bytes)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        result.Headers.Add("X-Exam-Server-Id", "archive-server"); result.Headers.Add("X-Exam-Node-Id", "archive-node"); result.Headers.Add("X-Exam-Server-Epoch", "1");
        return result;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    private sealed class Directories(string directory) : IExamArchiveDirectoryDialog
    { public string? ChooseExportDirectory() => directory; public string? ChooseImportDirectory() => directory; }
    private sealed class Service(Source source) : IExamArchiveService
    {
        public int Opens { get; private set; }
        public TaskCompletionSource<IExamArchiveSource>? OpenResponse { get; set; }
        public Task<string> ExportAsync(string examId, string name, string directory, CancellationToken token) => Task.FromResult(directory);
        public Task<IExamArchiveSource> OpenImportAsync(string directory, CancellationToken token)
        { Opens++; return OpenResponse?.Task ?? Task.FromResult<IExamArchiveSource>(source); }
    }
    private sealed class Source(ExamArchivePreview preview) : IExamArchiveSource
    {
        public ExamArchivePreview Preview => preview;
        public int Imports { get; private set; }
        public ArchiveImportDecision? Decision { get; private set; }
        public string? RequestId { get; private set; }
        public bool Disposed { get; private set; }
        public Task<ExamArchiveImportResult> ImportAsync(ArchiveImportDecision decision, string requestId, CancellationToken token)
        {
            Imports++; Decision = decision; RequestId = requestId;
            return Task.FromResult(new ExamArchiveImportResult(decision.Choice == ArchiveImportChoice.Overwrite
                ? decision.Target!.ExamId : Summary.ExamId, Summary.Name, true, false));
        }
        public void Dispose() => Disposed = true;
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
