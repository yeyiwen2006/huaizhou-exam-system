using System.IO;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Importing;

namespace Exam.AdminUnitTests;

public sealed partial class ManifestPreflightViewModelTests
{
    [Fact]
    public void ValidManifest_ProjectsOnlyReadOnlyStructure()
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-three-types" };

        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("three-types")));

        var preview = Assert.IsType<ManifestStructurePreview>(viewModel.StructurePreview);
        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Equal((2, 4, 9), (preview.SubjectCount, preview.GroupCount, preview.QuestionCount));
        Assert.Equal(new[] { "科目甲", "科目乙" }, preview.Subjects.Select(subject => subject.Name));
        Assert.Equal(Enumerable.Range(1, 9), preview.Questions.Select(question => question.QuestionNo));
        var serialized = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("scoring", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correctOptionIds", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedCheckAndExamSwitch_ClearPreviousPreview()
    {
        using var api = new AdminApiClient();
        var viewModel = new ManifestPreflightViewModel(api) { ExamId = "synthetic-minimal" };
        var valid = File.ReadAllText(SampleManifest("minimal"));
        viewModel.ValidateManifestJson(valid);
        Assert.NotNull(viewModel.StructurePreview);

        viewModel.ValidateManifestJson("{");
        Assert.Null(viewModel.StructurePreview);
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains(viewModel.Issues, issue => issue.Rule == ManifestRuleIds.InvalidJson);

        viewModel.ValidateManifestJson(valid);
        Assert.NotNull(viewModel.StructurePreview);
        viewModel.ExamId = "synthetic-other";
        Assert.Null(viewModel.StructurePreview);
        Assert.Empty(viewModel.Issues);
        viewModel.ExamId = "synthetic-minimal";
        Assert.Null(viewModel.StructurePreview);
    }

    [Fact]
    public async Task CancelSelection_DropsOldAndPendingResults()
    {
        using var api = new AdminApiClient();
        var files = new CancelableManifestFiles();
        var viewModel = new ManifestPreflightViewModel(api, files) { ExamId = "synthetic-minimal" };
        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("minimal")));
        var pending = viewModel.SelectManifestAsync();
        await files.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        files.SelectedPath = null;
        await viewModel.SelectManifestAsync();
        files.Complete(File.ReadAllText(SampleManifest("minimal")));
        await pending;

        Assert.Null(viewModel.StructurePreview);
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.SelectedManifestPath);
        Assert.Contains("已取消选择", viewModel.Status, StringComparison.Ordinal);
    }

    private sealed class CancelableManifestFiles : IManifestFileDialogService
    {
        private readonly TaskCompletionSource<string> _content =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SelectedPath { get; set; } = "manifest.json";
        public string? OpenManifestFile() => SelectedPath;
        public Task<string> ReadManifestAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return _content.Task.WaitAsync(cancellationToken);
        }
        public void Complete(string content) => _content.TrySetResult(content);
    }
}
