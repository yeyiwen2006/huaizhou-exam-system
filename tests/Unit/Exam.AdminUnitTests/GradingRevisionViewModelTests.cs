using Exam.Infrastructure.Packaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

/// <summary>
/// 「只改答案与计分规则」页面的行为：从本地材料包提交规则修订，
/// 并把「发布成功」与「重算中 / 结果就绪」分开显示。合成材料，不接触真实试卷。
/// </summary>
public sealed class GradingRevisionViewModelTests
{
    private const string ExamId = "synthetic-grading-exam";
    private const string ExamName = "合成规则考试";
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Theory]
    [InlineData(null)]
    public void Describe_WithoutPublishedRules_SaysThereIsNothingToCompare(long? _)
    {
        var report = new GradingStatusReport(ExamId, null, 0, 0, null, [], DateTimeOffset.UtcNow);

        Assert.Contains("尚未发布任何计分规则", GradingRevisionViewModel.Describe(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SeparatesRecalculatingFromReady()
    {
        var outstanding = new[] { new RegradeItem("submission-1", RegradeReason.StaleVersion) };
        var recalculating = new GradingStatusReport(ExamId, 3, 5, 2, new GradingReadiness(3, false, outstanding, ["还有 3 份未按该版本计分"]), [], DateTimeOffset.UtcNow);
        var ready = new GradingStatusReport(ExamId, 3, 5, 5, new GradingReadiness(3, true, [], []), [], DateTimeOffset.UtcNow);

        var pending = GradingRevisionViewModel.Describe(recalculating);
        Assert.Contains("重算中", pending, StringComparison.Ordinal);
        Assert.Contains("未完成 1 份", pending, StringComparison.Ordinal);
        Assert.Contains("还有 3 份未按该版本计分", pending, StringComparison.Ordinal);

        var done = GradingRevisionViewModel.Describe(ready);
        Assert.Contains("结果就绪", done, StringComparison.Ordinal);
        Assert.Contains("已按版本 3 计分 5 份", done, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_SendsTheLocalManifestAndAnswersThenReportsBothStatesSeparately()
    {
        using var package = new MaterialPackage();
        JsonNode? sent = null;
        using var handler = new Handler(async (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/grading-publications", StringComparison.Ordinal))
            {
                sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                return Reply(Published(2, sent!["ruleSetId"]!.GetValue<string>()));
            }

            if (path.EndsWith("/status", StringComparison.Ordinal)) return Reply(Status(ready: false));
            if (path.StartsWith("/api/v1/versioning/exams/", StringComparison.Ordinal)) return Reply(Snapshot());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root)) { ConfirmPublicationAsync = _ => Task.FromResult(true), ExamId = ExamId, Reason = "合成规则修订" };
        await vm.SelectPackageAsync();
        await vm.CheckAsync();

        await vm.PublishAsync();

        Assert.NotNull(sent);
        Assert.Equal(1, sent!["expectedVersion"]!.GetValue<long>());
        Assert.Equal(ExamName, JsonSerializer.Deserialize<Exam.Contracts.Importing.ExamManifest>(
            sent["manifestJson"]!.GetValue<string>(), Exam.Contracts.Importing.ImportJson.Create())!.ExamName);
        Assert.True(sent["validation"]!["recalculationReady"]!.GetValue<bool>());
        Assert.Equal("synthetic-operator", sent["operator"]!.GetValue<string>());
        // 发布成功与重算状态分开显示：前者说规则版本，后者说成绩是否重算完。
        Assert.Contains("规则发布成功：规则版本 2", vm.PublishedText, StringComparison.Ordinal);
        Assert.Contains("发布成功只说明服务器已切换规则版本", vm.PublishedText, StringComparison.Ordinal);
        Assert.Contains("重算中", vm.RecalculationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_WithoutAPackageOrReason_DoesNotCallTheServer()
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/grading-publications", StringComparison.Ordinal)) calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var package = new MaterialPackage();
        var dialog = new FakeDialog(null);
        using var vm = new GradingRevisionViewModel(api, dialog) { ExamId = ExamId };

        await vm.PublishAsync();
        Assert.Equal(0, calls);
        Assert.Contains("请先选择材料包目录", vm.Status, StringComparison.Ordinal);

        // 选了目录但没填原因：仍不发请求。
        dialog.Path = package.Root;
        await vm.SelectPackageAsync();
        await vm.PublishAsync();
        Assert.Equal(0, calls);
        Assert.Contains("请填写本次修改原因", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_WithAnIncompleteMaterialPackage_FailsWithAReasonAndDoesNotCallTheServer()
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/grading-publications", StringComparison.Ordinal)) calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        // 只有清单、缺独立答案：换规则必须带完整参考答案。
        var incomplete = Path.Combine(Path.GetTempPath(), "c05-no-answer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(incomplete);
        try
        {
            File.WriteAllText(Path.Combine(incomplete, "manifest.json"), "{}", new UTF8Encoding(false));
            using var vm = new GradingRevisionViewModel(api, new FakeDialog(incomplete)) { ConfirmPublicationAsync = _ => Task.FromResult(true), ExamId = ExamId, Reason = "合成" };
            await vm.SelectPackageAsync();

            await vm.PublishAsync();

            Assert.Equal(0, calls);
            Assert.Contains("找不到 manifest.json 或 server-only/answer-key.json", vm.Status, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(incomplete)) Directory.Delete(incomplete, recursive: true);
        }
    }

    [Fact]
    public async Task StatusLookupFailure_DoesNotOverwriteThePublishResult()
    {
        using var package = new MaterialPackage();
        using var handler = new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/grading-publications", StringComparison.Ordinal)) return PublishedReplyAsync(request);
            if (path.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "test", null,
                        new ApiError("SYNTHETIC_UNAVAILABLE", "合成故障。")), options: Json)
                });
            return Task.FromResult(Reply(Snapshot()));
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root)) { ConfirmPublicationAsync = _ => Task.FromResult(true), ExamId = ExamId, Reason = "合成规则修订" };
        await vm.SelectPackageAsync();
        await vm.CheckAsync();

        await vm.PublishAsync();

        // 规则确实已发布：状态核对失败只能落在重算那一行，不能改写发布结果。
        Assert.Contains("规则发布成功：规则版本 2", vm.PublishedText, StringComparison.Ordinal);
        Assert.Contains("重算状态核对失败（发布结果不受影响）", vm.RecalculationText, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingExam_ClearsThePreviousPublicationAndStatus()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(Status(ready: true))));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(null)) { ExamId = ExamId };
        vm.Reason = "合成";
        _ = vm.SelectPackageAsync();

        vm.ExamId = "synthetic-other-exam";

        Assert.Equal(string.Empty, vm.PackagePath);
        Assert.Equal("尚未发布规则修订。", vm.PublishedText);
        Assert.Equal("尚未核对重算状态。", vm.RecalculationText);
    }

    [Fact]
    public async Task LostReceiptReplaysExactlyTheCheckedRequestDespiteNewProjectionAndChangedLocalFiles()
    {
        using var package = new MaterialPackage();
        var bodies = new List<string>();
        var committed = false;
        var ruleSet = "initial-rules";
        using var handler = new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/grading-publications", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(); bodies.Add(body);
                ruleSet = JsonNode.Parse(body)!["ruleSetId"]!.GetValue<string>(); committed = true;
                if (bodies.Count == 1) throw new HttpRequestException("synthetic lost durable receipt");
                return Reply(Published(2, ruleSet));
            }
            return request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? Reply(Status(true)) : Reply(committed ? Published(2, ruleSet) : Snapshot());
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root))
        { ExamId = ExamId, Reason = "synthetic correction", ConfirmPublicationAsync = _ => Task.FromResult(true) };
        await vm.SelectPackageAsync(); await vm.CheckAsync();
        Assert.Single(vm.Questions);
        await vm.PublishAsync();
        Assert.DoesNotContain("规则发布成功", vm.PublishedText);
        Assert.False(vm.CanSelectPackage);
        vm.Reason = "must not replace the frozen reason";
        await File.WriteAllTextAsync(Path.Combine(package.Root, "server-only", "answer-key.json"), "invalid external edit");
        await vm.CheckAsync();
        await vm.PublishAsync();
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal("synthetic correction", JsonNode.Parse(bodies[1])!["reason"]!.GetValue<string>());
        Assert.Equal(1, JsonNode.Parse(bodies[1])!["expectedVersion"]!.GetValue<long>());
        Assert.Contains("规则发布成功：规则版本 2", vm.PublishedText);
        await vm.PublishAsync();
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public async Task SavedGradingPreviewReplaysAcrossProcessesAndRejectsChangedAnswerFile()
    {
        using var package = new MaterialPackage();
        var bodies = new List<string>();
        using var handler = new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/grading-publications", StringComparison.Ordinal))
            {
                bodies.Add(await request.Content!.ReadAsStringAsync());
                return Reply(Published(2, JsonNode.Parse(bodies[^1])!["ruleSetId"]!.GetValue<string>()));
            }
            return Reply(Snapshot());
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        var output = Path.Combine(package.Root, "checked");
        using (var prepared = await PreparedGradingRevision.PrepareAsync(api, ExamId, package.Root, "synthetic"))
            await prepared.ExportAsync(output);
        using (var prepared = await PreparedGradingRevision.LoadAsync(output)) await prepared.PublishAsync(api);
        using (var prepared = await PreparedGradingRevision.LoadAsync(output)) await prepared.PublishAsync(api);
        Assert.Equal(2, bodies.Count); Assert.Equal(bodies[0], bodies[1]);
        await File.AppendAllTextAsync(Path.Combine(output, "answer-key.json"), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedGradingRevision.LoadAsync(output));
        Assert.Equal(2, bodies.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchingExamDuringCheckOrConfirmationCannotPublishIntoTheNewExam(bool duringCheck)
    {
        using var package = new MaterialPackage();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        using var handler = new Handler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return await PublishedReplyAsync(request); }
            if (duringCheck) { entered.TrySetResult(); await release.Task; }
            return Reply(Snapshot());
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root))
        { ExamId = ExamId, Reason = "synthetic", ConfirmPublicationAsync = async _ => { entered.TrySetResult(); await release.Task; return true; } };
        await vm.SelectPackageAsync();
        Task operation;
        if (duringCheck) operation = vm.CheckAsync();
        else { await vm.CheckAsync(); operation = vm.PublishAsync(); }
        await entered.Task;
        vm.ExamId = "other-exam";
        release.SetResult(); await operation;
        Assert.Equal(0, posts); Assert.Empty(vm.Questions);
        Assert.Equal("尚未发布规则修订。", vm.PublishedText);
    }

    [Fact]
    public async Task ConfirmationCancellationDoesNotPublishAndChangedReasonInvalidatesPreview()
    {
        using var package = new MaterialPackage(); var posts = 0;
        using var handler = new Handler((request, _) => { if (request.Method == HttpMethod.Post) posts++; return Task.FromResult(Reply(Snapshot())); });
        using var api = new AdminApiClient(handler); api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root))
        { ExamId = ExamId, Reason = "synthetic", ConfirmPublicationAsync = _ => Task.FromResult(false) };
        await vm.SelectPackageAsync(); await vm.CheckAsync(); await vm.PublishAsync();
        Assert.Equal(0, posts); Assert.True(vm.CanPublish);
        vm.Reason = "a different reason";
        Assert.False(vm.CanPublish); Assert.Empty(vm.Questions);
    }

    [Fact]
    public async Task WrongReceiptIsNotDisplayedAsSuccess()
    {
        using var package = new MaterialPackage();
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request.Method == HttpMethod.Post ? Published(2, "wrong-rules") : Snapshot())));
        using var api = new AdminApiClient(handler); api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root))
        { ExamId = ExamId, Reason = "synthetic", ConfirmPublicationAsync = _ => Task.FromResult(true) };
        await vm.SelectPackageAsync(); await vm.CheckAsync(); await vm.PublishAsync();
        Assert.DoesNotContain("规则发布成功", vm.PublishedText); Assert.False(vm.CanSelectPackage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewUsesDisplayedOptionCaptionsButKeepsInternalAnswerIdentifiers(bool useCaptions)
    {
        using var package = new MaterialPackage();
        var manifestPath = Path.Combine(package.Root, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        var question = manifest["questions"]![0]!;
        question["options"] = new JsonArray("internal-left", "internal-right");
        if (useCaptions) question["displayMetadata"] = new JsonObject { ["optionCaptions"] = new JsonArray("A.", "B.") };
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var answersPath = Path.Combine(package.Root, "server-only", "answer-key.json");
        var answers = JsonNode.Parse(await File.ReadAllTextAsync(answersPath))!;
        answers["answers"]![0]!["correctOptionIds"] = new JsonArray("internal-left");
        await File.WriteAllTextAsync(answersPath, answers.ToJsonString());
        var snapshot = Snapshot();
        snapshot = snapshot with { Paper = snapshot.Paper! with
        { Structure = new PaperStructure([new("q-001", 1, "s", "g", "single", ["internal-left", "internal-right"])]) } };
        using var handler = new Handler((_, _) => Task.FromResult(Reply(snapshot)));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var prepared = await PreparedGradingRevision.PrepareAsync(api, ExamId, package.Root, "合成映射核对");
        Assert.Equal(useCaptions ? "A." : "internal-left", Assert.Single(prepared.Preview).CorrectOptions);
        Assert.Equal("internal-left", JsonNode.Parse(prepared.Request.AnswerKeyJson!)!["answers"]![0]!["correctOptionIds"]![0]!.GetValue<string>());
        var output = Path.Combine(package.Root, "caption-preview");
        await prepared.ExportAsync(output);
        var displayed = await File.ReadAllTextAsync(Path.Combine(output, "preview.md"));
        Assert.Contains(useCaptions ? "参考答案 A." : "参考答案 internal-left", displayed);
        if (useCaptions) Assert.DoesNotContain("internal-left", displayed);
    }

    [Theory]
    [InlineData("VERSION_CONFLICT", true)]
    [InlineData("INVALID_INPUT", true)]
    [InlineData("PUBLICATION_NOT_READY", true)]
    [InlineData("REPLICA_UNAVAILABLE", false)]
    [InlineData("FORBIDDEN", false)]
    public async Task PublicationErrorsUseChineseWithoutExposingCodesOrUntrustedMessages(string code, bool definitive)
    {
        using var package = new MaterialPackage();
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            { Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "test", null,
                new ApiError(code, "不应显示的原始材料与内部信息 synthetic-private-marker")), options: Json) }
            : Reply(Snapshot())));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "synthetic-operator");
        using var vm = new GradingRevisionViewModel(api, new FakeDialog(package.Root))
        { ExamId = ExamId, Reason = "合成失败核对", ConfirmPublicationAsync = _ => Task.FromResult(true) };
        await vm.SelectPackageAsync(); await vm.CheckAsync(); await vm.PublishAsync();
        Assert.DoesNotContain(code, vm.Status);
        Assert.DoesNotContain("synthetic-private-marker", vm.Status);
        Assert.DoesNotContain("原始材料", vm.Status);
        Assert.DoesNotContain("规则发布成功", vm.PublishedText);
        Assert.Equal(definitive, vm.CanSelectPackage);
        Assert.Equal(!definitive, vm.CanPublish);
        if (!definitive) Assert.Contains("尚未确认", vm.Status);
    }

    private static async Task<HttpResponseMessage> PublishedReplyAsync(HttpRequestMessage request)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        return Reply(Published(2, body["ruleSetId"]!.GetValue<string>()));
    }

    private static ExamVersionSnapshot Snapshot() => new(ExamId, new VersionVector(1, 1, 1),
        new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2)), ExamLifecycle.Running,
        null, null,
        new PaperPublicationInfo(1, "synthetic-artifact", new PaperStructure([new("q-001", 1, "s", "g", "single", ["A", "B"])]), [], DateTimeOffset.UtcNow,
            "synthetic-operator", "首次导入并发布整套试卷"), new(1, "initial-rules", DateTimeOffset.UtcNow, "synthetic-operator", "initial"),
        new Dictionary<string, ParticipantVersionStatus>());

    private static ExamVersionSnapshot Published(long gradingVersion, string ruleSetId = "rules-synthetic") => Snapshot() with
    {
        Versions = new VersionVector(1, 1, gradingVersion),
        Grading = new GradingPublicationInfo(gradingVersion, ruleSetId, DateTimeOffset.UtcNow,
            "synthetic-operator", "合成规则修订")
    };

    private static GradingStatusReport Status(bool ready) => ready
        ? new GradingStatusReport(ExamId, 2, 3, 3, new GradingReadiness(2, true, [], []), [], DateTimeOffset.UtcNow)
        : new GradingStatusReport(ExamId, 2, 3, 1,
            new GradingReadiness(2, false, [new RegradeItem("s-2", RegradeReason.StaleVersion),
                new RegradeItem("s-3", RegradeReason.StaleVersion)], ["还有 2 份未按该版本计分"]), [], DateTimeOffset.UtcNow);

    private static HttpResponseMessage Reply<T>(T result) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "test", result, null),
            options: Json)
    };

    /// <summary>可用的合成材料包：清单的题目源文件名前缀与考试名称一致（清单校验 规则）。</summary>
    private sealed class MaterialPackage : IDisposable
    {
        public MaterialPackage()
        {
            Root = Path.Combine(Path.GetTempPath(), "c05-rule-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "server-only"));
            File.WriteAllText(Path.Combine(Root, "manifest.json"), """
                {
                  "schemaVersion": 1,
                  "examId": "synthetic-grading-exam",
                  "examName": "合成规则考试",
                  "subjects": [{ "subjectId": "s", "name": "科目", "maxScore": "1" }],
                  "groups": [{ "groupId": "g", "subjectId": "s", "name": "单选", "type": "single", "order": 1,
                    "questionCount": 1, "maxScore": "1",
                    "scoring": { "oneCorrect": { "correct": "1", "blank": "0", "wrong": "0" } } }],
                  "questions": [{ "questionId": "q-001", "questionNo": 1, "subjectId": "s", "groupId": "g",
                    "source": "questions/合成规则考试_1.tex", "options": ["A", "B"] }],
                  "resourceRoots": []
                }
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, "server-only", "answer-key.json"), """
                {"schemaVersion":1,"examId":"synthetic-grading-exam","answers":[{"questionId":"q-001","questionNo":1,"correctOptionIds":["A"]}]}
                """, new UTF8Encoding(false));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeDialog(string? path) : IExamImportSourceDialog
    {
        /// <summary>可由用例改：选择材料包目录这一步模拟操作者换目录或取消。</summary>
        public string? Path { get; set; } = path;

        public string? OpenDirectory() => Path;
        public string? OpenZip() => null;
        public string? OpenNewMaterialDirectory() => null;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            route(request, token);
    }
}
