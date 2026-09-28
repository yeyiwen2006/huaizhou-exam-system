using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

public sealed partial class ManifestPreflightViewModelTests
{
    [Fact]
    public void ValidSyntheticManifest_UsesD01AndKeepsCompletionBoundaryExplicit()
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-minimal" };

        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("minimal")));

        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);
        Assert.Contains("不等于编译、计分、发布或客户端更新完成", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("独立答案", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type-invalid", ManifestRuleIds.GroupTypeInvalid, "/groups/0/type")]
    [InlineData("expression-syntax", ManifestRuleIds.ScoreExpressionInvalid, "/groups/0/scoring")]
    public void SyntheticErrors_ExposeD01RuleAndJsonPointer(
        string caseName,
        string expectedRule,
        string pointerPrefix)
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-error-case" };

        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("errors", caseName)));

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains(viewModel.Issues, issue =>
            issue.Rule == expectedRule && issue.Location.StartsWith(pointerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatAndExamSwitch_ClearOldResult()
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-minimal" };
        var valid = File.ReadAllText(SampleManifest("minimal"));
        viewModel.ValidateManifestJson("{");
        Assert.Contains(viewModel.Issues, issue => issue.Rule == ManifestRuleIds.InvalidJson);

        viewModel.ValidateManifestJson(valid);
        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);

        viewModel.ExamId = "other-exam";
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);
        viewModel.ValidateManifestJson(valid);
        Assert.Contains("考试 ID 不一致", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredField_DoesNotReuseMalformedJsonIssue()
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-minimal" };
        viewModel.ValidateManifestJson("{");

        var missing = JsonNode.Parse(File.ReadAllText(SampleManifest("minimal")))!;
        missing.AsObject().Remove("examName");
        viewModel.ValidateManifestJson(missing.ToJsonString());

        Assert.Contains(viewModel.Issues, issue =>
            issue.Rule == ManifestRuleIds.RequiredMissing && issue.Location == "/examName");
        Assert.DoesNotContain(viewModel.Issues, issue => issue.Rule == ManifestRuleIds.InvalidJson);
    }

    [Theory]
    [InlineData(StudentPaperPackage.LegacySchemaVersion)]
    [InlineData(StudentPaperPackage.OptionRegionSchemaVersion)]
    public void StudentPackageManifest_CannotReplaceImportSourceManifest(int studentSchemaVersion)
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-minimal" };
        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("minimal")));
        Assert.True(viewModel.StaticPreflightPassed);

        // Serialize the formal D03 student DTO, not a hand-written lookalike schema.
        var studentManifest = new StudentPaperManifest(studentSchemaVersion, "synthetic-minimal", "合成考试", 1,
            [new StudentPaperSubject("subject", "合成科目")],
            [new StudentPaperGroup("group", "subject", "单项选择", 1)],
            [new StudentPaperQuestion(new PaperQuestionStructure("q-001", 1, "subject", "group", "single", ["A", "B"]),
                "questions/合成考试_1.pdf", null)], []);
        viewModel.ValidateManifestJson(JsonSerializer.Serialize(studentManifest, ImportJson.Create()));

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains(viewModel.Issues, issue =>
            issue.Rule == ManifestRuleIds.RequiredMissing && issue.Location == "/resourceRoots");
        Assert.Contains("导入源清单", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("学生包清单", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerFileName_IsRejectedBeforeRead()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("answer-key.json");
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };

        await viewModel.SelectManifestAsync();

        Assert.False(files.WasRead);
        Assert.Contains("独立答案不在考务端", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateRead_CannotPolluteChangedExam()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("manifest.json");
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        var pending = viewModel.SelectManifestAsync();
        await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.ExamId = "other-exam";
        files.Complete(File.ReadAllText(SampleManifest("minimal")));
        await pending;

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);
        Assert.Contains("考试 ID 已变更", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidReselection_CancelsPendingDisplayState()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("manifest.json");
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        var pending = viewModel.SelectManifestAsync();
        await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsPreflighting);

        files.SelectedPath = "answer-key.json";
        await viewModel.SelectManifestAsync();
        Assert.False(viewModel.IsPreflighting);
        Assert.Contains("独立答案不在考务端", viewModel.Status, StringComparison.Ordinal);

        files.Complete(File.ReadAllText(SampleManifest("minimal")));
        await pending;
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);
    }

    [Fact]
    public async Task OversizeManifest_ShowsBoundedFailureWithoutPassing()
    {
        using var api = new AdminApiClient();
        var files = new ControlledManifestFiles("manifest.json");
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        var pending = viewModel.SelectManifestAsync();
        await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        files.Fail(new InvalidDataException("private path must not appear"));

        await pending;

        Assert.False(viewModel.IsPreflighting);
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains("10 MB", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("private path", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionSwitch_ClearsAcceptedResult()
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-minimal" };
        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("minimal")));
        Assert.True(viewModel.StaticPreflightPassed);

        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.Issues);
        Assert.Contains("连接已变更", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileReader_RejectsOversizeAndInvalidUtf8()
    {
        var directory = Directory.CreateTempSubdirectory("exam-c05-");
        try
        {
            var reader = new FileDialogService();
            var oversized = Path.Combine(directory.FullName, "oversized.json");
            await using (var stream = File.Create(oversized))
            {
                stream.SetLength(ManifestPreflightViewModel.MaxManifestBytes + 1);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.ReadManifestAsync(oversized, ManifestPreflightViewModel.MaxManifestBytes));

            var invalidUtf8 = Path.Combine(directory.FullName, "invalid.json");
            await File.WriteAllBytesAsync(invalidUtf8, [0xC3, 0x28]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() =>
                reader.ReadManifestAsync(invalidUtf8, ManifestPreflightViewModel.MaxManifestBytes));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string SampleManifest(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "synthetic")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Path.Combine([root.FullName, "samples", "synthetic", .. segments, "manifest.json"]);
    }

    private sealed class ControlledManifestFiles(string path) : IManifestFileDialogService
    {
        private readonly TaskCompletionSource<string> _content =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasRead { get; private set; }
        public string? SelectedPath { get; set; } = path;

        public string? OpenManifestFile() => SelectedPath;

        public Task<string> ReadManifestAsync(
            string selectedPath,
            long maxBytes,
            CancellationToken cancellationToken = default)
        {
            WasRead = true;
            ReadStarted.TrySetResult();
            return _content.Task.WaitAsync(cancellationToken);
        }

        public void Complete(string content) => _content.TrySetResult(content);
        public void Fail(Exception exception) => _content.TrySetException(exception);
    }
}
