using Exam.Infrastructure.Packaging;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Exams;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Contracts.Administration;
using Exam.Domain.Scoring;

namespace Exam.AdminUnitTests;

public sealed class ExamImportWorkflowTests
{
    [Fact]
    public void SyntheticPackage_BindsOnlyMatchingNamedExam_WithoutEditingOriginal()
    {
        var sample = FindSyntheticMinimal();
        var manifest = File.ReadAllText(Path.Combine(sample, "manifest.json"));
        var answers = File.ReadAllText(Path.Combine(sample, "server-only", "answer-key.json"));
        var rebound = ExamImportService.BindPackageToSelectedExam(manifest, answers,
            "exam-server-generated", "合成最小考试");
        Assert.Equal("exam-server-generated", ImportedGradingRules.Create(rebound.Manifest, rebound.Answers).ExamId);
        var parsed = JsonSerializer.Deserialize<ExamManifest>(rebound.Manifest, ImportJson.Create())!;
        Assert.Equal("1", ExamImportService.QuestionFullScore(Assert.Single(parsed.Groups), 1));
        Assert.Contains("synthetic-minimal", manifest, StringComparison.Ordinal);
        Assert.Contains("synthetic-minimal", File.ReadAllText(Path.Combine(sample, "manifest.json")), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ExamImportService.BindPackageToSelectedExam(manifest, answers,
            "exam-other", "另一场合成考试"));
    }

    [Fact]
    public void AlreadyPublishedOrRunningExam_IsRejectedBeforeAnyLocalImport()
    {
        var expected = new ExamVersionSnapshot("synthetic-exam", new VersionVector(1, 0, 0),
            new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            ExamLifecycle.Scheduled, null, null, null, null,
            new Dictionary<string, ParticipantVersionStatus>());
        ExamImportService.RequireFirstPublication(expected);
        Assert.Throws<InvalidOperationException>(() => ExamImportService.RequireFirstPublication(expected with
        { Versions = new VersionVector(1, 1, 1) }));
        Assert.Throws<InvalidOperationException>(() => ExamImportService.RequireFirstPublication(expected with
        { LifecycleState = ExamLifecycle.Running }));
    }

    [Fact]
    public async Task DirectoryAndZip_CreatePrivateSnapshotsWithoutChangingSource()
    {
        using var fixture = new SourceFixture();
        await using var directory = await CreateAndCheckAsync(fixture.Directory);
        await using var zip = await CreateAndCheckAsync(fixture.ZipPath);
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(directory.Workspace.PackageRoot, "manifest.json")));
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(zip.Workspace.PackageRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "manifest.json")));
        Assert.True(File.Exists(fixture.ZipPath));

        static async Task<AsyncWorkspace> CreateAndCheckAsync(string source) =>
            new(await ExamImportWorkspace.CreateAsync(source));
    }

    [Fact]
    public async Task InvalidDirectoryAndZip_RejectAndDoNotCreateUsableSnapshot()
    {
        using var fixture = new SourceFixture();
        File.Delete(Path.Combine(fixture.Directory, "server-only", "answer-key.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ExamImportWorkspace.CreateAsync(fixture.Directory));
        var escaped = Path.Combine(fixture.Root, "escaped.zip");
        using (var archive = ZipFile.Open(escaped, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("synthetic");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ExamImportWorkspace.CreateAsync(escaped));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Fact]
    public async Task SuccessfulPreviewThenFailedCheck_ClearsOldQuestionsAndPublishPermission()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory);
        using var vm = new ExamImportViewModel(api, service: service);
        vm.SetCurrentExam(Summary("synthetic-exam-1"));
        vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        Assert.True(vm.CanPublish);
        Assert.Single(vm.Questions);

        service.FailNextCheck = true;
        await vm.CheckAsync();
        Assert.False(vm.CanPublish);
        Assert.Empty(vm.Questions);
        Assert.Contains("检查失败", vm.Status);
        Assert.Contains("合成错误包", vm.Status);
        Assert.Equal(0, service.PublishCalls);
    }

    [Fact]
    public async Task CancelAndExamSwitch_NeverCallPublishOrKeepOldPreview()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory);
        using var vm = new ExamImportViewModel(api, service: service);
        vm.SetCurrentExam(Summary("synthetic-exam-1"));
        vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        vm.ConfirmPublicationAsync = _ => Task.FromResult(false);
        await vm.PublishAsync();
        Assert.Equal(0, service.PublishCalls);
        Assert.Contains("取消", vm.Status);
        vm.SetCurrentExam(Summary("synthetic-exam-2"));
        Assert.False(vm.CanPublish);
        Assert.Empty(vm.Questions);
    }

    [Fact]
    public async Task ConfirmedPublish_RequiresMatchingPaperAndGradingVersions()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory);
        using var vm = new ExamImportViewModel(api, service: service)
        { ConfirmPublicationAsync = _ => Task.FromResult(true) };
        vm.SetCurrentExam(Summary("synthetic-exam-1"));
        vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        service.FailPublish = true;
        await vm.PublishAsync();
        Assert.DoesNotContain("正式发布成功", vm.Status);
        Assert.True(vm.CanPublish);
        service.FailPublish = false;
        await vm.PublishAsync();
        Assert.Contains("正式发布成功", vm.Status);
        Assert.False(vm.CanPublish);
        Assert.Equal(2, service.PublishCalls);
    }

    [Fact]
    public async Task Upload_UsesOfficialMultipartAndDoesNotTreatEmptySuccessAsPublication()
    {
        using var fixture = new SourceFixture();
        var encrypted = Path.Combine(fixture.Root, "synthetic.enc");
        await File.WriteAllBytesAsync(encrypted, [1, 2, 3, 4]);
        var materials = Path.Combine(fixture.Root, "synthetic-materials.enc");
        await File.WriteAllBytesAsync(materials, [5, 6, 7, 8]);
        string? transmitted = null;
        using var handler = new PublicationHandler(request =>
        {
            Assert.Equal("/api/v1/exams/synthetic-exam-1/publications/upload", request.RequestUri!.AbsolutePath);
            Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);
            transmitted = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>(
                    "synthetic-server", "synthetic-node", 1, "synthetic-correlation", null, null))
            };
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001", "synthetic-token", "synthetic-operator");
        var request = new PublishExamBundleApiRequest("synthetic-request", 0, 0, "synthetic-resource",
            "{\"examId\":\"synthetic-exam-1\"}", "{\"answers\":[]}", [], "synthetic first import", true);
        var upload = new UploadExamBundleRequest(request,
            new PublicationPackageEnvelope("synthetic-resource", 1, 4096, "synthetic-nonce",
                new Dictionary<string, string>(), new Dictionary<string, string>()));
        await Assert.ThrowsAsync<AdminApiException>(() =>
            api.UploadPublicationAsync("synthetic-exam-1", upload, encrypted, materials));
        Assert.Contains("name=metadata", transmitted!, StringComparison.Ordinal);
        Assert.Contains("name=package", transmitted!, StringComparison.Ordinal);
        // 源材料归档是第二份密文：表单里只允许出现密文，明文题目与答案不得进入临时缓冲。
        Assert.Contains("name=materials", transmitted!, StringComparison.Ordinal);
        Assert.Contains("synthetic-request", transmitted!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedClickDuringConfirmationDoesNotOpenASecondConfirmation()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory);
        using var vm = new ExamImportViewModel(api, service: service);
        vm.SetCurrentExam(Summary("synthetic-exam-1")); vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        vm.ConfirmPublicationAsync = _ => { calls++; return confirmation.Task; };
        var first = vm.PublishAsync();
        Assert.True(vm.IsPublishing);
        await vm.PublishAsync();
        Assert.Equal(1, calls);
        confirmation.SetResult(false);
        await first;
        Assert.Equal(0, service.PublishCalls);
        Assert.False(vm.IsPublishing);
    }

    [Fact]
    public async Task SameExamNewSettingsInvalidateTheOldPreview()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory);
        using var vm = new ExamImportViewModel(api, service: service);
        var exam = Summary("synthetic-exam-1");
        vm.SetCurrentExam(exam); vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        Assert.True(vm.CanPublish);
        vm.SetCurrentExam(exam with { SettingsVersion = 2 });
        Assert.False(vm.CanPublish);
        Assert.Empty(vm.Questions);
    }

    [Fact]
    public async Task LostUploadReplyDoesNotQueryLocalVersionAndDeclareDurableSuccess()
    {
        using var fixture = new SourceFixture();
        var service = new FakeImportService(fixture.Directory) { LoseReceipt = true };
        var reads = 0;
        using var handler = new PublicationHandler(_ =>
        {
            reads++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>("synthetic-server", "node-a", 1,
                "synthetic", service.LastResult, null), options: JsonDefaults.Create()) };
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001", "synthetic-token", "synthetic-operator");
        using var vm = new ExamImportViewModel(api, service: service) { ConfirmPublicationAsync = _ => Task.FromResult(true) };
        vm.SetCurrentExam(Summary("synthetic-exam-1")); vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        await vm.PublishAsync();
        Assert.Equal(0, reads);
        Assert.DoesNotContain("正式发布成功", vm.Status);
        Assert.Contains("尚不能确认", vm.Status);
        Assert.True(vm.CanPublish);
    }

    [Fact]
    public async Task SameArtifactFromAnotherExamIsNotAcceptedAsSuccess()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory) { ResultExamId = "other-exam" };
        using var vm = new ExamImportViewModel(api, service: service) { ConfirmPublicationAsync = _ => Task.FromResult(true) };
        vm.SetCurrentExam(Summary("synthetic-exam-1")); vm.SourcePath = fixture.Directory;
        await vm.CheckAsync(); await vm.PublishAsync();
        Assert.DoesNotContain("正式发布成功", vm.Status);
        Assert.True(vm.CanPublish);
    }

    [Fact]
    public async Task SwitchingExamDuringPublicationDoesNotOverwriteTheNewContext()
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeImportService(fixture.Directory) { PublishRelease = gate };
        using var vm = new ExamImportViewModel(api, service: service) { ConfirmPublicationAsync = _ => Task.FromResult(true) };
        vm.SetCurrentExam(Summary("synthetic-exam-1")); vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        var publication = vm.PublishAsync();
        var oldRoot = service.LastPrepared!.Workspace.PackageRoot;
        vm.SetCurrentExam(Summary("synthetic-exam-2"));
        var newStatus = vm.Status;
        Assert.True(System.IO.Directory.Exists(oldRoot));
        gate.SetResult(); await publication;
        Assert.Equal(newStatus, vm.Status);
        Assert.Equal("synthetic-exam-2", vm.ExamId);
        Assert.False(System.IO.Directory.Exists(oldRoot));
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("changed-questions")]
    public async Task ChangingRevisionDetailsInvalidatesTheCheckedPreview(string field)
    {
        using var fixture = new SourceFixture();
        using var api = new AdminApiClient();
        var service = new FakeImportService(fixture.Directory);
        using var vm = new ExamImportViewModel(api, service: service);
        vm.SetCurrentExam(Summary("synthetic-exam-1"));
        vm.SourcePath = fixture.Directory;
        await vm.CheckAsync();
        Assert.True(vm.CanPublish);
        if (field == "reason") vm.PublishReason = "new reason";
        else vm.ChangedQuestionsText = "q2";
        Assert.False(vm.CanPublish);
        Assert.Empty(vm.Questions);
        Assert.Equal(0, service.PublishCalls);
    }

    private static ExamSummary Summary(string examId) => new(examId, "合成考试", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddHours(1), ExamLifecycle.Scheduled, false, false, 0, 0, 1);

    private static string FindSyntheticMinimal()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            var path = Path.Combine(root.FullName, "samples", "synthetic", "minimal");
            if (File.Exists(Path.Combine(path, "manifest.json"))) return path;
        }
        throw new DirectoryNotFoundException("找不到仓库中的合成最小样包。");
    }

    private sealed class FakeImportService(string source) : IExamImportService
    {
        public bool FailNextCheck { get; set; }
        public bool FailPublish { get; set; }
        public bool LoseReceipt { get; set; }
        public string? ResultExamId { get; set; }
        public TaskCompletionSource? PublishRelease { get; set; }
        public ExamVersionSnapshot? LastResult { get; private set; }
        public PreparedExamImport? LastPrepared { get; private set; }
        public int PublishCalls { get; private set; }
        public async Task<PreparedExamImport> PrepareAsync(string selected, string examId, CancellationToken ct = default,
            string? reason = null, IReadOnlyList<string>? changedQuestions = null)
        {
            if (FailNextCheck) throw new InvalidDataException("合成错误包");
            var workspace = await ExamImportWorkspace.CreateAsync(source, ct);
            var manifest = new ExamManifest(1, examId, "合成考试", [], [], [], []);
            var version = new ExamVersionSnapshot(examId, new VersionVector(1, 0, 0),
                new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
                ExamLifecycle.Scheduled, null, null, null, null,
                new Dictionary<string, ParticipantVersionStatus>());
            return LastPrepared = new PreparedExamImport(workspace, manifest, "{}", "{}", version,
                new Dictionary<string, TeXCompileResult>(),
                [new ImportPreviewQuestion("q-1", 1, "合成科目", "合成题组", "single", "1", "A、B", "synthetic.pdf")],
                selected, ImportPipeline.FirstImportReason, ["q-1"], []);
        }
        public async Task<ExamVersionSnapshot> PublishAsync(PreparedExamImport prepared, CancellationToken ct = default)
        {
            PublishCalls++;
            if (FailPublish) throw new InvalidDataException("合成发布失败");
            var paper = new PaperPublicationInfo(1, prepared.ResourceId, new PaperStructure([]), [],
                DateTimeOffset.UtcNow, "synthetic-operator", "first import");
            var grading = new GradingPublicationInfo(1, "bundle-rules-" + prepared.ResourceId, DateTimeOffset.UtcNow,
                "synthetic-operator", "first import");
            LastResult = prepared.Expected with
            { ExamId = ResultExamId ?? prepared.Expected.ExamId, Versions = new VersionVector(1, 1, 1), Paper = paper, Grading = grading };
            if (LoseReceipt) throw new HttpRequestException("synthetic lost receipt");
            if (PublishRelease is not null) await PublishRelease.Task;
            return LastResult;
        }
    }

    private sealed class AsyncWorkspace(ExamImportWorkspace workspace) : IAsyncDisposable
    {
        public ExamImportWorkspace Workspace => workspace;
        public ValueTask DisposeAsync() { workspace.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class PublicationHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class SourceFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "huaizhou-c05-test-" + Guid.NewGuid().ToString("N"));
        public string Directory => Path.Combine(Root, "directory");
        public string ZipPath => Path.Combine(Root, "source.zip");
        public SourceFixture()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "server-only"));
            File.WriteAllText(Path.Combine(Directory, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(Directory, "server-only", "answer-key.json"), "{}");
            ZipFile.CreateFromDirectory(Directory, ZipPath);
        }
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
