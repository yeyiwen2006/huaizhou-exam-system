using System.IO.Compression;
using System.Text;

using Exam.Domain.Importing;
using Exam.Infrastructure.Packaging;

namespace Exam.UnitTests.Packaging;

/// <summary>
/// 源材料归档格式的用例：确定性、可读写往返，以及**坏归档一律拒绝**。
/// 归档随发布一起交给服务器，坏归档被当成材料集合就会让「实际变更题号」核对失去意义，
/// 因此每个失败分支都要有稳定错误码。
/// </summary>
public sealed class PaperMaterialsArchiveTests
{
    private const string ExamId = "synthetic-materials-exam";

    [Fact]
    public void Build_IsDeterministic_ForTheSameMaterials()
    {
        var first = PaperMaterialsArchive.Build(ExamId, 1, Materials());
        var second = PaperMaterialsArchive.Build(ExamId, 1, Materials());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Build_ThenRead_RoundTripsEveryFileAndTheManifest()
    {
        var result = PaperMaterialsArchive.Read(PaperMaterialsArchive.Build(ExamId, 2, Materials()));

        Assert.True(result.Succeeded, result.Failure?.Value);
        Assert.Equal(ExamId, result.Manifest!.ExamId);
        Assert.Equal(2, result.Manifest.PaperVersion);
        Assert.Equal(PaperMaterialsArchive.CurrentFormatVersion, result.Manifest.FormatVersion);
        Assert.Equal(3, result.Manifest.Files.Count);
        Assert.Equal("题干", Encoding.UTF8.GetString(result.Materials!.Read("questions/q1.tex")));
        Assert.Equal(["figures/a.png", "manifest.json", "questions/q1.tex"], result.Materials.Paths);
    }

    [Fact]
    public void Read_RejectsContentThatDoesNotMatchTheDeclaredHash()
    {
        // 归档带一个声明了错哈希的清单：即使 ZIP 本身可读，也必须拒绝。
        var archive = Zip(
            ("questions/q1.tex", "题干"),
            (PaperMaterialsArchive.ManifestFileName, Manifest("""{"path":"questions/q1.tex","length":6,"sha256":"0000"}""")));

        var result = PaperMaterialsArchive.Read(archive);

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Read_RejectsAnUnsupportedFormatVersion()
    {
        var archive = Zip(
            ("questions/q1.tex", "题干"),
            (PaperMaterialsArchive.ManifestFileName,
                """{"formatVersion":99,"examId":"synthetic-materials-exam","paperVersion":1,"files":[]}"""));

        var result = PaperMaterialsArchive.Read(archive);

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsFormatUnsupported", result.Failure!.Value.Key);
    }

    [Fact]
    public void Read_RejectsAMissingManifest()
    {
        var result = PaperMaterialsArchive.Read(Zip(("questions/q1.tex", "题干")));

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Read_RejectsPathsThatEscapeThePackageRoot()
    {
        var archive = Zip(
            ("../evil.tex", "越界"),
            (PaperMaterialsArchive.ManifestFileName, Manifest(string.Empty)));

        var result = PaperMaterialsArchive.Read(archive);

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Read_RejectsCaseAliasedDuplicatePaths()
    {
        var archive = Zip(
            ("questions/Q1.tex", "甲"),
            ("questions/q1.tex", "乙"),
            (PaperMaterialsArchive.ManifestFileName, Manifest(string.Empty)));

        var result = PaperMaterialsArchive.Read(archive);

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Read_RejectsEntriesTheManifestDoesNotDeclare()
    {
        var archive = Zip(
            ("questions/q1.tex", "题干"),
            ("figures/undeclared.png", "没登记"),
            (PaperMaterialsArchive.ManifestFileName,
                Manifest("""{"path":"questions/q1.tex","length":6,"sha256":"0000"}""")));

        var result = PaperMaterialsArchive.Read(archive);

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Build_RejectsCaseAliasedDuplicates()
    {
        var files = new List<KeyValuePair<string, byte[]>>
        {
            new("questions/Q1.tex", [1]),
            new("questions/q1.tex", [2])
        };

        Assert.Throws<ArgumentException>(() => PaperMaterialsArchive.Build(ExamId, 1, files));
    }

    [Fact]
    public void Read_RejectsAnEntryThatDecompressesBeyondTheLimit()
    {
        // 压缩后很小、解压后超上限的条目（zip bomb 方向）必须拒绝：不能无界读进内存。
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("questions/q1.tex", CompressionLevel.Optimal).Open())
            {
                var chunk = new byte[1024 * 1024];
                var blocks = PaperMaterialsArchive.MaxArchiveBytes / chunk.Length + 1;
                for (var index = 0; index < blocks; index++)
                {
                    entry.Write(chunk);
                }
            }

            using var manifest = zip.CreateEntry(PaperMaterialsArchive.ManifestFileName).Open();
            manifest.Write(Encoding.UTF8.GetBytes(Manifest(string.Empty)));
        }

        var result = PaperMaterialsArchive.Read(buffer.ToArray());

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public async Task Extract_WritesAnEditableMaterialPackageAndRefusesAnExistingTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), "c05-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = PaperMaterialsArchive.Read(PaperMaterialsArchive.Build(ExamId, 1, Materials()));
            Assert.True(archive.Succeeded, archive.Failure?.Value);
            var target = Path.Combine(directory, "materials");

            await PaperMaterialsArchive.ExtractAsync(archive, target);

            // 解出来的就是可直接再检查/修订的材料包：清单、题目源文件、图片、独立答案。
            Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(target, "questions", "q1.tex")));
            Assert.True(File.Exists(Path.Combine(target, "figures", "a.png")));
            // 归档自身的清单不写出：它不是材料包的一部分。
            Assert.False(File.Exists(Path.Combine(target, PaperMaterialsArchive.ManifestFileName)));
            // 目标目录已存在就报错，不覆盖操作者已有的目录。
            await Assert.ThrowsAsync<IOException>(() => PaperMaterialsArchive.ExtractAsync(archive, target));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Read_RejectsNotAZip()
    {
        var result = PaperMaterialsArchive.Read([1, 2, 3, 4]);

        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Compute_WorksDirectlyOnArchivedMaterials()
    {
        // 归档读出的材料集合可直接喂给逐题摘要：两条链的坐标系必须一致。
        var manifest = new Exam.Contracts.Importing.ExamManifest(1, ExamId, "合成题",
            [new Exam.Contracts.Importing.ManifestSubject("s", "科目", "1")],
            [new Exam.Contracts.Importing.ManifestGroup("g", "s", "单选", "single", 1, 1, false, "1",
                new Exam.Contracts.Importing.ManifestGroupScoring(
                    new Exam.Contracts.Importing.ManifestOneCorrect("1", "0", "0"), null))],
            [new Exam.Contracts.Importing.ManifestQuestion("q1", 1, "s", "g", "questions/q1.tex", ["A", "B"], null)],
            []);

        var result = PaperMaterialsArchive.Read(PaperMaterialsArchive.Build(ExamId, 1, Materials()));
        Assert.True(result.Succeeded, result.Failure?.Value);

        var digests = QuestionContentDigest.Compute(manifest, result.Materials!);

        Assert.Equal("q1", Assert.Single(digests).Key);
        Assert.Equal(64, Assert.Single(digests).Value.Length);
    }

    [Fact]
    public void Read_RejectsDuplicateArchiveManifests()
    {
        var result = PaperMaterialsArchive.Read(Zip(
            (PaperMaterialsArchive.ManifestFileName, Manifest(string.Empty)),
            (PaperMaterialsArchive.ManifestFileName, Manifest(string.Empty))));
        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Fact]
    public void Read_RejectsDuplicateDeclarationsEvenWhenFileCountsMatch()
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("a"))).ToLowerInvariant();
        var declaration = $"{{\"path\":\"a.bin\",\"length\":1,\"sha256\":\"{hash}\"}}";
        var result = PaperMaterialsArchive.Read(Zip(("a.bin", "a"), ("b.bin", "b"),
            (PaperMaterialsArchive.ManifestFileName, Manifest(declaration + "," + declaration))));
        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"path\":null,\"length\":1,\"sha256\":\"invalid\"}")]
    [InlineData("{\"path\":\" \",\"length\":1,\"sha256\":\"invalid\"}")]
    public void Read_InvalidManifestEntriesReturnAStableFailureInsteadOfThrowing(string entry)
    {
        var result = PaperMaterialsArchive.Read(Zip(("a.bin", "a"),
            (PaperMaterialsArchive.ManifestFileName, Manifest(entry))));
        Assert.False(result.Succeeded);
        Assert.Equal("MaterialsArchiveInvalid", result.Failure!.Value.Key);
    }

    [Theory]
    [InlineData("../outside.tex")]
    [InlineData("materials.json")]
    [InlineData("MATERIALS.JSON")]
    [InlineData("folder/../source.tex")]
    public void Build_RejectsUnsafeOrReservedPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => PaperMaterialsArchive.Build(ExamId, 1,
            [new KeyValuePair<string, byte[]>(path, [1])]));
    }

    [Fact]
    public void Build_RejectsAggregateUncompressedSizeBeyondTheWorkspaceLimit()
    {
        // Reuse a single synthetic allocation; do not construct a decompression bomb in memory.
        var content = new byte[PaperMaterialsArchive.MaxArchiveBytes];
        var files = Enumerable.Range(0, 5).Select(i => new KeyValuePair<string, byte[]>($"{i}.bin", content));
        Assert.Throws<InvalidDataException>(() => PaperMaterialsArchive.Build(ExamId, 1, files));
    }

    private static IReadOnlyList<KeyValuePair<string, byte[]>> Materials() =>
    [
        new("questions/q1.tex", Encoding.UTF8.GetBytes("题干")),
        new("figures/a.png", [1, 2, 3]),
        new("manifest.json", Encoding.UTF8.GetBytes("{}"))
    ];

    private static string Manifest(string files) => string.Join(string.Empty,
        """{"formatVersion":""", PaperMaterialsArchive.CurrentFormatVersion,
        ""","examId":"synthetic-materials-exam","paperVersion":1,"files":[""",
        files, "]}");

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }
}
