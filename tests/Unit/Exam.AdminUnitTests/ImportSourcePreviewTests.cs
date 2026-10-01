using System.IO;
using System.IO.Compression;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed partial class ManifestPreflightViewModelTests
{
    [Fact]
    public async Task DirectorySource_ValidSyntheticManifestShowsReadOnlyPreview()
    {
        using var api = new AdminApiClient();
        var source = new SelectedImportSource { DirectoryPath = Path.GetDirectoryName(SampleManifest("minimal")) };
        var viewModel = new ManifestPreflightViewModel(api, null, source) { ExamId = "synthetic-minimal" };

        await viewModel.SelectDirectoryAsync();

        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Equal(1, Assert.IsType<ManifestStructurePreview>(viewModel.StructurePreview).QuestionCount);
        Assert.Equal(source.DirectoryPath, viewModel.SelectedManifestPath);
    }

    [Fact]
    public async Task ZipSource_ValidThenInvalidClearsOldPreviewAndDoesNotExtract()
    {
        var directory = Directory.CreateTempSubdirectory("exam-c05-zip-preview-");
        try
        {
            var valid = Path.Combine(directory.FullName, "valid.zip");
            using (var archive = ZipFile.Open(valid, ZipArchiveMode.Create))
            {
                await using var entry = archive.CreateEntry("manifest.json").Open();
                await using var writer = new StreamWriter(entry);
                await writer.WriteAsync(File.ReadAllText(SampleManifest("minimal")));
            }
            var invalid = Path.Combine(directory.FullName, "invalid.zip");
            using (var archive = ZipFile.Open(invalid, ZipArchiveMode.Create))
                archive.CreateEntry("elsewhere/manifest.json");

            using var api = new AdminApiClient();
            var source = new SelectedImportSource { ZipPath = valid };
            var viewModel = new ManifestPreflightViewModel(api, null, source) { ExamId = "synthetic-minimal" };
            await viewModel.SelectZipAsync();
            Assert.True(viewModel.StaticPreflightPassed);
            Assert.NotNull(viewModel.StructurePreview);

            source.ZipPath = invalid;
            await viewModel.SelectZipAsync();
            Assert.False(viewModel.StaticPreflightPassed);
            Assert.Null(viewModel.StructurePreview);
            Assert.Equal(invalid, viewModel.SelectedManifestPath);
            Assert.Contains("根目录缺少唯一 manifest.json", viewModel.Status, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "elsewhere")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_MissingManifestClearsPreviousSuccess()
    {
        var directory = Directory.CreateTempSubdirectory("exam-c05-empty-source-");
        try
        {
            using var api = new AdminApiClient();
            var source = new SelectedImportSource { DirectoryPath = Path.GetDirectoryName(SampleManifest("minimal")) };
            var viewModel = new ManifestPreflightViewModel(api, null, source) { ExamId = "synthetic-minimal" };
            await viewModel.SelectDirectoryAsync();
            Assert.NotNull(viewModel.StructurePreview);

            source.DirectoryPath = directory.FullName;
            await viewModel.SelectDirectoryAsync();
            Assert.Null(viewModel.StructurePreview);
            Assert.False(viewModel.StaticPreflightPassed);
            Assert.Contains("读取清单失败", viewModel.Status, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class SelectedImportSource : IImportSourceFileDialogService
    {
        private readonly FileDialogService _reader = new();
        public string? DirectoryPath { get; set; }
        public string? ZipPath { get; set; }
        public string? OpenImportDirectory() => DirectoryPath;
        public string? OpenImportZip() => ZipPath;
        public Task<string> ReadManifestFromDirectoryAsync(string directory, long maxBytes, CancellationToken cancellationToken = default) =>
            _reader.ReadManifestFromDirectoryAsync(directory, maxBytes, cancellationToken);
        public Task<string> ReadManifestFromZipAsync(string zipPath, long maxBytes, CancellationToken cancellationToken = default) =>
            _reader.ReadManifestFromZipAsync(zipPath, maxBytes, cancellationToken);
    }
}
