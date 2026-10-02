using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

/// <summary>
/// 试题导入页（C05 考务端接线）。全部使用**合成**材料路径与合成响应，不接触真实试卷材料，不需要 TeX。
/// </summary>
public sealed partial class PaperImportViewModelTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static readonly DateTimeOffset Start = new(2027, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));
    private const string ExamId = "synthetic-exam";

    internal const string PackageRoot = @"C:\synthetic\paper-package";

    internal static readonly ImportPipelineResult PreviewPassed = new(
        true, ImportStage.Completed, [], Preview: [new ImportQuestionPreview("synthetic-q1", true, 4, 4)]);

    internal static readonly ImportPipelineResult PreviewFailed = new(
        false, ImportStage.Compile, [new ImportPipelineIssue("CompileFailed", "合成编译失败。", "synthetic-q2")]);

    internal static readonly ImportPipelineResult PublishPassed = new(
        true, ImportStage.Completed, [], EncryptedPackagePath: @"C:\synthetic\_packages\p.epkg",
        ResourceId: "student-package-synthetic-exam-2", PublishedPaperVersion: 2);

    [Fact]
    public async Task Preview_SendsServerSideInputsWithoutPublishing()
    {
        var requests = new List<(string Path, JsonNode? Body)>();
        using var handler = new Handler(async (request, _) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, Body(request)));
            var path = request.RequestUri!.AbsolutePath;
            return path == "/api/v1/imports/preview"
                ? Success(PreviewPassed)
                : Success(PublishPassed);
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成预检";
        vm.TargetVersionText = "3";

        await vm.PreviewAsync();

        var preview = Assert.Single(requests);
        Assert.Equal("/api/v1/imports/preview", preview.Path);
        Assert.Equal(ExamId, preview.Body!["examId"]!.GetValue<string>());
        Assert.Equal(PackageRoot, preview.Body["packageRoot"]!.GetValue<string>());
        Assert.Equal(3, preview.Body["expectedPaperVersion"]!.GetValue<long>());
        Assert.Equal("合成预检", preview.Body["reason"]!.GetValue<string>());
        // 预检把编译产物写到所选目录的兄弟目录，不写回材料目录本身；路径已规范化。
        Assert.Equal(Path.GetFullPath(Path.Combine(PackageRoot, "..", "_compile")),
            preview.Body["compilationOutputDirectory"]!.GetValue<string>());
        Assert.DoesNotContain("..", preview.Body["compilationOutputDirectory"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(vm.PreviewPassed);
        Assert.True(vm.CanPublish);
        Assert.Equal("synthetic-q1", Assert.Single(vm.Questions).QuestionId);
    }

    [Fact]
    public async Task Preview_FailureStopsBeforePublishAndReportsTheStage()
    {
        var requests = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Success(PreviewFailed));
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成预检";

        await vm.PreviewAsync();

        Assert.Equal("/api/v1/imports/preview", Assert.Single(requests));
        Assert.False(vm.PreviewPassed);
        Assert.False(vm.CanPublish);
        Assert.Equal("Compile", vm.Stage);
        Assert.Equal("CompileFailed", Assert.Single(vm.Issues).Code);
        Assert.Contains("Compile", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_SendsTheTargetVersionAndNoCertificates_ThenReportsSuccess()
    {
        JsonNode? published = null;
        using var handler = new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/imports/publish")
            {
                published = Body(request);
                return Success(PublishPassed);
            }

            return request.RequestUri!.AbsolutePath.EndsWith("/versioning/exams/" + ExamId, StringComparison.Ordinal)
                ? Success(Snapshot(2))
                : Success(PreviewPassed);
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成发布";
        vm.TargetVersionText = "2";
        await vm.PreviewAsync();

        await vm.PublishAsync();

        Assert.NotNull(published);
        Assert.Equal(2, published["expectedPaperVersion"]!.GetValue<long>());
        // 加密收件人取服务器受管节点：界面不传证书，也不该出现 recipients 字段。
        Assert.False(published.AsObject().ContainsKey("recipients"));
        // 首次导入不需要声明变更题号：字段可以为缺省，也可以显式为 null，但不能带出题号。
        Assert.True(published["declaredChangedQuestionIds"] is null);
        Assert.Contains("试卷版本 2 已生效", vm.Status, StringComparison.Ordinal);
        // 发布成功后自动核对客户端更新（同一份考试版本快照口径）；
        // 本例快照里有参试者还没装到版本 2，所以必须如实报「尚未全部更新」，不能报成功。
        Assert.Contains("客户端尚未全部更新", vm.ClientUpdateSummary, StringComparison.Ordinal);
        Assert.Contains("已装到版本 2 的 1/3 名", vm.ClientUpdateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_SubmitsTheDeclaredChangedQuestionsForAMidExamRevision()
    {
        JsonNode? published = null;
        using var handler = new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/imports/publish")
            {
                published = Body(request);
                return Success(PublishPassed);
            }

            return request.RequestUri!.AbsolutePath.EndsWith("/versioning/exams/" + ExamId, StringComparison.Ordinal)
                ? Success(Snapshot(2))
                : Success(PreviewPassed);
        });
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };
        await vm.SelectPackageAsync();
        vm.Reason = "合成修订";
        vm.TargetVersionText = "2";
        await vm.PreviewAsync();

        // 空格、半角逗号、顿号、分号混用，且重复题号只提交一次。
        vm.DeclaredChangedQuestions = "synthetic-q1, synthetic-q2、synthetic-q3;synthetic-q1";
        await vm.PublishAsync();

        Assert.NotNull(published);
        Assert.Equal(
            new[] { "synthetic-q1", "synthetic-q2", "synthetic-q3" },
            published["declaredChangedQuestionIds"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task LoadTargetVersion_ReadsTheServerVersionAndOffersTheNextOne()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Success(Snapshot(4))));
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };

        await vm.LoadTargetVersionAsync();

        Assert.Equal("5", vm.TargetVersionText);
        Assert.Contains("当前试卷版本为 4", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshClientUpdates_CountsOnlyParticipantsStillInTheExam()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Success(Snapshot(2))));
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };

        await vm.RefreshClientUpdatesAsync();

        // 已退出（NotParticipating）的参试者不参与「全部客户端已更新」。
        Assert.Contains("已装到版本 2 的 1/3 名", vm.ClientUpdateSummary, StringComparison.Ordinal);
        Assert.Contains("2 名未更新", vm.ClientUpdateSummary, StringComparison.Ordinal);
        Assert.Contains("synthetic-p2", vm.ClientUpdateSummary, StringComparison.Ordinal);
        Assert.Contains("安装失败", vm.ClientUpdateSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-p4", vm.ClientUpdateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshClientUpdates_BeforeAnyPublicationSaysSoInsteadOfClaimingCompletion()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Success(Snapshot(0))));
        using var api = Configured(handler);
        var vm = new PaperImportViewModel(api, new FakeDialogs()) { ExamId = ExamId };

        await vm.RefreshClientUpdatesAsync();

        Assert.Contains("尚未发布", vm.ClientUpdateSummary, StringComparison.Ordinal);
    }

    internal static ExamVersionSnapshot Snapshot(long paperVersion) => new(
        ExamId,
        new VersionVector(1, paperVersion, 1),
        new ScheduleWindow(Start, Start.AddHours(2)),
        ExamLifecycle.Scheduled,
        null,
        null,
        null,
        null,
        new Dictionary<string, ParticipantVersionStatus>(StringComparer.Ordinal)
        {
            ["synthetic-p1"] = new("synthetic-p1", ParticipationBusinessState.Answering, paperVersion, paperVersion, PaperUpdateState.Updated, null),
            ["synthetic-p2"] = new("synthetic-p2", ParticipationBusinessState.Waiting, paperVersion, paperVersion - 1, PaperUpdateState.DownloadFailed, "合成下载失败"),
            ["synthetic-p3"] = new("synthetic-p3", ParticipationBusinessState.Submitted, paperVersion, 0, PaperUpdateState.NotReceived, null),
            ["synthetic-p4"] = new("synthetic-p4", ParticipationBusinessState.NotParticipating, paperVersion, 0, PaperUpdateState.NotReceived, null)
        });

    internal static JsonNode? Body(HttpRequestMessage request) =>
        request.Content is null ? null : JsonNode.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());

    internal static AdminApiClient Configured(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    internal static HttpResponseMessage Success<T>(T data) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "test", data, null), options: Json)
    };

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => route(request, token);
    }

    /// <summary>只提供合成路径，不接触任何真实材料；加密收件人由服务器受管节点决定，界面不选证书。</summary>
    internal sealed class FakeDialogs : IPaperImportFileDialogService
    {
        public string? Directory { get; set; } = PackageRoot;

        public string? OpenPackageDirectory() => Directory;
    }

}
