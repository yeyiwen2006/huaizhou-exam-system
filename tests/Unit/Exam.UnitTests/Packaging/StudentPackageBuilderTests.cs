using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.Packaging;

/// <summary>
/// 学生端组包的用例。夹具全是**合成**的最小包：一个合法清单、一道题的编译 PDF、
/// 一个题图、一个共享样式，以及 server-only 下的答案。
/// 加密往返用学生端的解码器读回，验证「开考前只有密文、包内不含答案解析」。
/// </summary>
public sealed class StudentPackageBuilderTests : IDisposable
{
    private const string ExamId = "synthetic-package";
    private const string PackageId = "paper-package-synthetic";
    private const long PaperVersion = 3;

    private const string ManifestJson = """
        {
          "schemaVersion": 1,
          "examId": "synthetic-package",
          "examName": "合成样题考试",
          "subjects": [
            { "subjectId": "subject-1", "name": "科目甲", "maxScore": "1" }
          ],
          "groups": [
            {
              "groupId": "group-1",
              "subjectId": "subject-1",
              "name": "一、单项选择题",
              "type": "single",
              "order": 1,
              "questionCount": 1,
              "maxScore": "1",
              "scoring": { "oneCorrect": { "correct": "1", "blank": "0", "wrong": "0" } }
            }
          ],
          "questions": [
            {
              "questionId": "q-001",
              "questionNo": 1,
              "subjectId": "subject-1",
              "groupId": "group-1",
              "source": "questions/合成样题考试_1.tex",
              "options": ["A", "B", "C", "D"]
            }
          ],
          "resourceRoots": ["figures", "styles"]
        }
        """;

    private readonly string _root;
    private readonly string _packageRoot;
    private readonly string _compilationOutput;
    private readonly string _studentDirectory;
    private readonly string _encryptedPath;
    private readonly PackageEncryptionService _encryption = new(4096);

    public StudentPackageBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "exam-student-package-" + Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_root, "package");
        _compilationOutput = Path.Combine(_root, "compiled");
        _studentDirectory = Path.Combine(_root, "student");
        _encryptedPath = Path.Combine(_root, "artifact", PackageId + ".pkg");

        WriteFile(Path.Combine(_packageRoot, "manifest.json"), ManifestJson);
        WriteFile(Path.Combine(_packageRoot, "questions", "合成样题考试_1.tex"), "\\documentclass{ctexart}");
        WriteFile(Path.Combine(_packageRoot, "figures", "chemistry", "q-figure-01.png"), "synthetic-png");
        File.WriteAllBytes(Path.Combine(_packageRoot, "figures", "chemistry", "q-figure-01.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        WriteFile(Path.Combine(_packageRoot, "styles", "合成样式.sty"), "% 合成共享样式");
        WriteFile(
            Path.Combine(_packageRoot, "server-only", "answer-key.json"),
            """{"schemaVersion":1,"examId":"synthetic-package","answers":[{"questionId":"q-001","questionNo":1,"correctOptionIds":["A"]}]}""");

        Directory.CreateDirectory(_compilationOutput);
        WriteFile(Path.Combine(_compilationOutput, "合成样题考试_1.pdf"), "%PDF-1.4 合成题目");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task Build_SeparatesStudentPackageFromServerSideAnswers()
    {
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(ExamId, result.Plan.ExamId);

        // 进包的是：无答案元数据、该题 PDF、清单声明的资源。
        Assert.Contains("manifest.json", result.Plan.IncludedPaths);
        Assert.Contains("questions/合成样题考试_1.pdf", result.Plan.IncludedPaths);
        Assert.Contains("figures/chemistry/q-figure-01.png", result.Plan.IncludedPaths);
        Assert.DoesNotContain("styles/合成样式.sty", result.Plan.IncludedPaths);

        // 输出目录里不得出现任何服务器专用内容。
        var written = Directory
            .GetFiles(_studentDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_studentDirectory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(result.Plan.IncludedPaths, written);
        Assert.DoesNotContain(written, path => path.Contains("answer", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(_studentDirectory, "server-only", "answer-key.json")));
    }

    [Fact]
    public async Task Build_EncryptsArtifactThatStudentDecoderCanReadWithoutAnswers()
    {
        using var node = CreateCertificate("node-a");
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(
            Request() with
            {
                EncryptedPackagePath = _encryptedPath,
                Recipients = [new NodeKeyRecipient("node-a", node)]
            });

        Assert.True(result.Succeeded, Describe(result));
        Assert.NotNull(result.Envelope);
        Assert.True(File.Exists(_encryptedPath));

        var plaintext = await DecryptAsStudentWould(result.Envelope!, node);
        var studentPaper = StudentPaperPackage.ReadManifest(plaintext, ExamId, PaperVersion);
        Assert.Equal(1, studentPaper.Groups[0].Order);
        Assert.Single(studentPaper.Questions);
        using var archive = new ZipArchive(new MemoryStream(plaintext), ZipArchiveMode.Read);

        var entries = archive.Entries
            .Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(result.Plan.IncludedPaths, entries);

        // 无答案元数据：清单里不得出现 correctOptionIds。
        var metadata = ReadEntry(archive, "manifest.json");
        Assert.DoesNotContain("correctOptionIds", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answers", metadata, StringComparison.OrdinalIgnoreCase);

        // 整个明文包里不得出现答案字段。
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.DoesNotContain("correctOptionIds", reader.ReadToEnd(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("figures")]
    [InlineData("FIGURES")]
    [InlineData(".")]
    public async Task Build_NormalizesResourceRootsIntoExplicitReadableDisplayPaths(string resourceRoot)
    {
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(ManifestJson)!;
        manifest["resourceRoots"] = new System.Text.Json.Nodes.JsonArray(resourceRoot);
        WriteFile(Path.Combine(_packageRoot, "manifest.json"), manifest.ToJsonString());
        using var node = CreateCertificate("node-a");
        var packaged = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with
        {
            EncryptedPackagePath = _encryptedPath, Recipients = [new("node-a", node)]
        });
        Assert.True(packaged.Succeeded, Describe(packaged));
        var displayed = StudentPaperPackage.ReadManifest(await DecryptAsStudentWould(packaged.Envelope!, node), ExamId, PaperVersion);
        var path = Assert.Single(displayed.DisplayResources);
        Assert.Equal("figures/chemistry/q-figure-01.png", path, ignoreCase: true);
    }

    [Fact]
    public async Task Build_ExcludesAnswerNamedResourceButKeepsTheRest()
    {
        WriteFile(Path.Combine(_packageRoot, "figures", "答案-1.png"), "synthetic-answer-figure");

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.True(result.Succeeded, Describe(result));
        Assert.DoesNotContain("figures/答案-1.png", result.Plan.IncludedPaths);

        var exclusion = Assert.Single(result.Plan.Excluded, item => item.Path.Contains("答案-1", StringComparison.Ordinal));
        Assert.Contains("答案", exclusion.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_studentDirectory, "figures", "答案-1.png")));
    }

    [Fact]
    public async Task Build_MissingQuestionPdf_IsReportedAndStopsWholePackage()
    {
        File.Delete(Path.Combine(_compilationOutput, "合成样题考试_1.pdf"));

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.False(result.Succeeded);
        var issue = Assert.Single(result.Issues, item => item.Code == StudentPackageIssueCode.QuestionPdfMissing);
        Assert.Equal("questions/合成样题考试_1.pdf", issue.Path);
        Assert.False(Directory.Exists(_studentDirectory) && Directory.EnumerateFiles(_studentDirectory).Any());
    }

    [Fact]
    public async Task Build_MissingResourceRoot_IsReported()
    {
        Directory.Delete(Path.Combine(_packageRoot, "styles"), recursive: true);

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, item => item.Code == StudentPackageIssueCode.ResourceRootMissing);
    }

    [Fact]
    public async Task Build_ManifestNotFound_IsReported()
    {
        File.Delete(Path.Combine(_packageRoot, "manifest.json"));

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, item => item.Code == StudentPackageIssueCode.MetadataInvalid);
    }

    [Fact]
    public async Task Build_ManifestWithUndeclaredAnswerField_IsRejected()
    {
        // D01 的严格读取会先挡下未定义字段；内容检查是第二层，两层都指向「不得夹带答案」。
        WriteFile(
            Path.Combine(_packageRoot, "manifest.json"),
            ManifestJson.Replace(
                "\"resourceRoots\"",
                "\"correctOptionIds\": [\"A\"], \"resourceRoots\"",
                StringComparison.Ordinal));

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, item => item.Code == StudentPackageIssueCode.MetadataInvalid);
    }

    [Fact]
    public async Task Build_OutputDirectoryInsidePackageRoot_IsRejected()
    {
        var builder = new StudentPackageBuilder(_encryption);

        await Assert.ThrowsAsync<ArgumentException>(() => builder.BuildAsync(
            Request() with { StudentPackageDirectory = Path.Combine(_packageRoot, "student") }));

        await Assert.ThrowsAsync<ArgumentException>(() => builder.BuildAsync(
            Request() with { EncryptedPackagePath = Path.Combine(_packageRoot, "artifact.pkg") }));
    }

    [Fact]
    public async Task Build_NonEmptyStudentDirectory_IsRejected()
    {
        Directory.CreateDirectory(_studentDirectory);
        WriteFile(Path.Combine(_studentDirectory, "leftover.txt"), "上次的残留");

        var builder = new StudentPackageBuilder(_encryption);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Request()));
    }

    [Fact]
    public async Task Build_MaterialDirectoryIsLeftUntouched()
    {
        var before = Snapshot(_packageRoot);

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(
            Request() with
            {
                EncryptedPackagePath = _encryptedPath,
                Recipients = [new NodeKeyRecipient("node-a", CreateCertificate("node-a"))]
            });

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(before, Snapshot(_packageRoot));
    }

    [Fact]
    public async Task Build_WithOptionRegions_ProducesV3ManifestAndCoordinateFile()
    {
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(
            Request() with { OptionRegions = OptionRegions() });

        Assert.True(result.Succeeded, Describe(result));
        Assert.Contains(OptionRegionArtifact.FileName, result.Plan.IncludedPaths);
        Assert.True(File.Exists(Path.Combine(_studentDirectory, OptionRegionArtifact.FileName)));
        Assert.Equal(StudentPaperPackage.NavigationSchemaVersion, PackagedSchemaVersion());
    }

    [Theory]
    [InlineData("single", "3", "4", "3分")]
    [InlineData("multiple", "3", "4", "4分")]
    [InlineData("indeterminate", "3", "4", "3分/4分")]
    [InlineData("indeterminate", "3", "3", "3分")]
    [InlineData("indeterminate", "{正确选项总数}", "{正确选项总数}", "{正确选项总数}分")]
    public async Task Build_PublishesOnlyTheConfiguredFullScoreDisplay(string type, string one, string many, string expected)
    {
        var input = JsonNode.Parse(ManifestJson)!;
        var group = input["groups"]![0]!;
        group["type"] = type;
        group.AsObject().Remove("maxScore");
        var scoring = new JsonObject();
        if (type != "multiple") scoring["oneCorrect"] = new JsonObject
        {
            ["correct"] = one, ["blank"] = "0", ["wrong"] = "-1"
        };
        if (type != "single") scoring["multipleCorrect"] = new JsonObject
        {
            ["allCorrect"] = many, ["partialCorrect"] = "1", ["blank"] = "0", ["wrong"] = "-2"
        };
        group["scoring"] = scoring;
        input["subjects"]![0]!["maxScore"] = "100";
        WriteFile(Path.Combine(_packageRoot, "manifest.json"), input.ToJsonString());

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with { OptionRegions = OptionRegions() });

        Assert.True(result.Succeeded, Describe(result));
        var json = File.ReadAllText(Path.Combine(_studentDirectory, "manifest.json"), Encoding.UTF8);
        var manifest = JsonSerializer.Deserialize<StudentPaperManifest>(json, ImportJson.Create())!;
        Assert.Equal("100", Assert.Single(manifest.Subjects).MaxScoreText);
        Assert.Equal(expected, Assert.Single(manifest.Groups).PerQuestionScoreText);
        foreach (var field in new[] { "scoring", "correctOptionIds", "partialCorrect", "blank", "wrong" })
            Assert.DoesNotContain($"\"{field}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_WithoutOptionRegions_StaysV1AndCarriesNoCoordinateFile()
    {
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.True(result.Succeeded, Describe(result));
        Assert.DoesNotContain(OptionRegionArtifact.FileName, result.Plan.IncludedPaths);
        Assert.False(File.Exists(Path.Combine(_studentDirectory, OptionRegionArtifact.FileName)));
        Assert.Equal(StudentPaperPackage.LegacySchemaVersion, PackagedSchemaVersion());
    }

    [Fact]
    public async Task Build_OptionRegionsNotMatchingManifest_IsRejectedWithoutWriting()
    {
        var cases = new (string Name, OptionRegionArtifact Artifact)[]
        {
            ("考试不一致", OptionRegions() with { ExamId = "other-exam" }),
            ("试卷版本不一致", OptionRegions() with { PaperVersion = PaperVersion + 1 }),
            ("坐标规范不符", OptionRegions() with { AnchorScheme = "pageTopLeftV2" }),
            ("产物版本不符", OptionRegions() with { ArtifactVersion = OptionRegionArtifact.CurrentArtifactVersion + 1 }),
            ("缺少题目", OptionRegions() with { Questions = [new OptionRegionQuestion("q-999", [Region("A")])] }),
            ("缺少选项", OptionRegions(["A", "B", "C"])),
            ("清单外选项", OptionRegions(["A", "B", "C", "D", "E"]))
        };

        foreach (var item in cases)
        {
            var result = await new StudentPackageBuilder(_encryption).BuildAsync(
                Request() with { OptionRegions = item.Artifact });

            Assert.False(result.Succeeded);
            Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.OptionRegionInvalid);
            Assert.False(Directory.Exists(_studentDirectory) &&
                Directory.EnumerateFiles(_studentDirectory, "*", SearchOption.AllDirectories).Any());
        }
    }

    [Theory]
    [InlineData("duplicate-option", false)]
    [InlineData("duplicate-option", true)]
    [InlineData("null-questions", false)]
    [InlineData("null-questions", true)]
    [InlineData("null-question", false)]
    [InlineData("null-question", true)]
    [InlineData("null-options", false)]
    [InlineData("null-options", true)]
    [InlineData("null-option", false)]
    [InlineData("null-option", true)]
    [InlineData("negative-page", false)]
    [InlineData("negative-page", true)]
    [InlineData("out-of-bounds", false)]
    [InlineData("out-of-bounds", true)]
    [InlineData("non-finite", false)]
    [InlineData("non-finite", true)]
    public async Task Build_InvalidCoordinates_ReportCoordinateFailureWithoutPublishing(string defect, bool encrypted)
    {
        var valid = OptionRegions();
        var question = valid.Questions[0];
        var options = question.Options.ToArray();
        var invalid = defect switch
        {
            "duplicate-option" => OptionRegions(["A", "A", "C", "D"]),
            "null-questions" => valid with { Questions = null! },
            "null-question" => valid with { Questions = [null!] },
            "null-options" => valid with { Questions = [question with { Options = null! }] },
            "null-option" => valid with { Questions = [question with { Options = [null!, options[1], options[2], options[3]] }] },
            "negative-page" => valid with { Questions = [question with { Options = [options[0] with { PageIndex = -1 }, options[1], options[2], options[3]] }] },
            "out-of-bounds" => valid with { Questions = [question with { Options = [options[0] with { X = 0.99, Width = 0.2 }, options[1], options[2], options[3]] }] },
            "non-finite" => valid with { Questions = [question with { Options = [options[0] with { X = double.NaN }, options[1], options[2], options[3]] }] },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var encryptedPath = _studentDirectory + ".epkg";

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(
            Request() with { OptionRegions = invalid, EncryptedPackagePath = encrypted ? encryptedPath : null });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.OptionRegionInvalid);
        Assert.Null(result.Envelope);
        Assert.Null(result.EncryptedPackagePath);
        Assert.False(File.Exists(encryptedPath));
        Assert.False(Directory.Exists(_studentDirectory) &&
            Directory.EnumerateFiles(_studentDirectory, "*", SearchOption.AllDirectories).Any());
    }

    private int PackagedSchemaVersion()
    {
        var text = File.ReadAllText(Path.Combine(_studentDirectory, StudentPaperPackage.ManifestPath));
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return document.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    private static OptionRegion Region(string optionId) => new(optionId, 0, 0.1, 0.1, 0.02, 0.02);

    private static OptionRegionArtifact OptionRegions(IReadOnlyList<string>? optionIds = null) =>
        new(OptionRegionArtifact.CurrentArtifactVersion,
            OptionRegionArtifact.AnchorSchemeName,
            ExamId,
            PaperVersion,
            [new OptionRegionQuestion("q-001", (optionIds ?? ["A", "B", "C", "D"]).Select(Region).ToList())]);

    private StudentPackageRequest Request() =>
        new(_packageRoot, _compilationOutput, _studentDirectory, PackageId, PaperVersion)
        { DisplayResourcePaths = ["figures/chemistry/q-figure-01.png"] };

    [Theory]
    [InlineData("\"resourceRoots\": [\"figures\", \"styles\"]", "\"resourceRoots\": [\"../outside\"]")]
    [InlineData("\"questions\": [", "\"questions\": null, \"ignored\": [")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 99")]
    [InlineData("\"questionNo\": 1", "\"questionNo\": 2")]
    public async Task Build_InvalidManifest_IsRejectedBeforeWriting(string before, string after)
    {
        WriteFile(Path.Combine(_root, "outside", "private.txt"), "synthetic secret");
        WriteFile(Path.Combine(_packageRoot, "manifest.json"), ManifestJson.Replace(before, after));

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.MetadataInvalid);
        Assert.False(Directory.Exists(_studentDirectory) && Directory.EnumerateFiles(_studentDirectory, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Build_ResourceDirectoryIsNotAFileAllowlist()
    {
        WriteFile(Path.Combine(_packageRoot, "figures", "notes.dat"), "{\"correctOptionIds\":[\"B\"]}");
        WriteFile(Path.Combine(_packageRoot, "styles", "notes.txt"), new string(' ', 70000) + "{\"answers\":[\"B\"]}");

        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());

        Assert.True(result.Succeeded, Describe(result));
        Assert.DoesNotContain("figures/notes.dat", result.Plan.IncludedPaths);
        Assert.DoesNotContain("styles/notes.txt", result.Plan.IncludedPaths);
        Assert.DoesNotContain("styles/合成样式.sty", result.Plan.IncludedPaths);
    }

    [Fact]
    public async Task Build_EncryptionDoesNotWritePlaintextFiles()
    {
        using var node = CreateCertificate("node-a");
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with
        {
            EncryptedPackagePath = _encryptedPath,
            Recipients = [new NodeKeyRecipient("node-a", node)]
        });

        Assert.True(result.Succeeded, Describe(result));
        Assert.False(Directory.Exists(_studentDirectory) && Directory.EnumerateFileSystemEntries(_studentDirectory).Any());
    }

    [Fact]
    public async Task Build_ExistingEncryptedVersionIsNeverOverwritten()
    {
        WriteFile(_encryptedPath, "previous version");
        using var node = CreateCertificate("node-a");
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with
        {
            EncryptedPackagePath = _encryptedPath,
            Recipients = [new NodeKeyRecipient("node-a", node)]
        });

        Assert.False(result.Succeeded);
        Assert.Equal("previous version", File.ReadAllText(_encryptedPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_encryptedPath)!, "*.partial"));
    }

    [Fact]
    public async Task Build_EncryptionFailureLeavesNoPartialOrPlaintextAndCanRetry()
    {
        using var node = CreateCertificate("node-a");
        var builder = new StudentPackageBuilder(new PackageEncryptionService(4096,
            new ManagedNodeEncryptionPolicy(new HashSet<string> { "node-b" })));
        var request = Request() with
        {
            EncryptedPackagePath = _encryptedPath,
            Recipients = [new NodeKeyRecipient("node-a", node)]
        };

        var failed = await builder.BuildAsync(request);

        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Issues, issue => issue.Code == StudentPackageIssueCode.EncryptionFailed);
        Assert.False(File.Exists(_encryptedPath));
        Assert.Empty(Directory.GetFiles(_root, "*.partial", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(_studentDirectory) && Directory.EnumerateFileSystemEntries(_studentDirectory).Any());
        Assert.True((await new StudentPackageBuilder(_encryption).BuildAsync(request)).Succeeded);
    }

    private async Task<byte[]> DecryptAsStudentWould(EncryptedPackageEnvelope envelope, X509Certificate2 node)
    {
        var dataKey = PackageEncryptionService.UnwrapDataKey(envelope, "node-a", node);
        using var material = new PackageUnlockMaterial(
            envelope.PackageId,
            envelope.PaperVersion,
            envelope.ChunkSize,
            envelope.NoncePrefix,
            dataKey);

        await using var ciphertext = new FileStream(_encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var plaintext = new MemoryStream();
        await PackageCiphertext.DecryptAsync(ciphertext, plaintext, material);
        return plaintext.ToArray();
    }

    [Theory]
    [InlineData("questions")]
    [InlineData("resourceRoots")]
    public async Task Build_NullManifestCollectionsReturnAnIssue(string property)
    {
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(ManifestJson)!;
        manifest[property] = null;
        WriteFile(Path.Combine(_packageRoot, "manifest.json"), manifest.ToJsonString());
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.MetadataInvalid);
    }

    [Theory]
    [InlineData("styles/合成样式.sty")]
    [InlineData("figures/missing.png")]
    [InlineData("../private.png")]
    public async Task Build_RequiredResourceFailureStopsWholePackage(string path)
    {
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with { DisplayResourcePaths = [path] });
        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(_studentDirectory));
    }

    [Fact]
    public async Task Build_AnswerFieldsAfter64KiBAreRejectedEvenWithImageHeader()
    {
        var image = Path.Combine(_packageRoot, "figures", "chemistry", "q-figure-01.png");
        await File.AppendAllTextAsync(image, new string(' ', 70000) + "{\"correctOptionIds\":[\"B\"]}", Encoding.UTF8);
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.RequiredResourceRejected);
        Assert.False(Directory.Exists(_studentDirectory));
    }

    [Fact]
    public async Task Build_DefaultResourceListContainsOnlyMetadataAndQuestionPdfs()
    {
        var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with { DisplayResourcePaths = [] });
        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(new[] { "manifest.json", "questions/合成样题考试_1.pdf" }, result.Plan.IncludedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_LinkedResourceOrOutputIsRejected(bool outputLink)
    {
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        var link = outputLink ? _studentDirectory : Path.Combine(_packageRoot, "figures", "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, external }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, external);
        try
        {
            var result = await new StudentPackageBuilder(_encryption).BuildAsync(Request());
            Assert.False(result.Succeeded);
            Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.PathEscaped);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Build_CancelledRequestDoesNotWriteArtifacts()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StudentPackageBuilder(_encryption).BuildAsync(Request(), cts.Token));
        Assert.False(Directory.Exists(_studentDirectory));
    }

    [Fact]
    [Trait("Category", "Latex")]
    public async Task Build_RealD02CompilationResultsRoundTripThroughStudentDecoder()
    {
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        Assert.NotNull(toolchain);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(ManifestJson)!;
        var question2 = manifest["questions"]![0]!.DeepClone();
        question2["questionId"] = "q-002";
        question2["questionNo"] = 2;
        question2["source"] = "questions/合成样题考试_2.tex";
        manifest["questions"]!.AsArray().Add(question2);
        manifest["groups"]![0]!["questionCount"] = 2;
        manifest["groups"]![0]!["maxScore"] = "2";
        manifest["subjects"]![0]!["maxScore"] = "2";
        WriteFile(Path.Combine(_packageRoot, "manifest.json"), manifest.ToJsonString());
        var compiler = new XeLaTeXCompiler(new ExternalProcessRunner(), toolchain!);
        var compiled = new Dictionary<string, TeXCompileResult>();
        for (var number = 1; number <= 2; number++)
        {
            var source = $"questions/合成样题考试_{number}.tex";
            WriteFile(Path.Combine(_packageRoot, source),
                "\\documentclass{article}\n\\begin{document}\nSynthetic question " + number + ". A B C D.\n\\end{document}");
            var result = await compiler.CompileAsync(new TeXCompileRequest(_packageRoot, source, _compilationOutput));
            Assert.True(result.Succeeded, string.Join("; ", result.Issues.Select(issue => issue.Message)));
            compiled.Add($"q-00{number}", result);
        }
        Assert.NotEqual(Path.GetDirectoryName(compiled["q-001"].OutputPdfPath), Path.GetDirectoryName(compiled["q-002"].OutputPdfPath));
        using var node = CreateCertificate("node-a");
        var packaged = await new StudentPackageBuilder(_encryption).BuildAsync(Request() with
        {
            CompiledQuestions = compiled, EncryptedPackagePath = _encryptedPath,
            Recipients = [new NodeKeyRecipient("node-a", node)], DisplayResourcePaths = []
        });
        Assert.True(packaged.Succeeded, Describe(packaged));
        var plaintext = await DecryptAsStudentWould(packaged.Envelope!, node);
        var displayed = StudentPaperPackage.ReadManifest(plaintext, ExamId, PaperVersion);
        Assert.Equal(2, displayed.Questions.Count);
        Assert.All(displayed.Questions, q => Assert.True(StudentPaperPackage.ReadQuestionPdf(plaintext, displayed, q.Structure.QuestionId).Length > 1000));
        using var archive = new ZipArchive(new MemoryStream(plaintext));
        Assert.Equal(3, archive.Entries.Count);
        Assert.All(archive.Entries.Where(entry => entry.FullName.EndsWith(".pdf", StringComparison.Ordinal)), entry => Assert.True(entry.Length > 1000));
        Assert.False(Directory.Exists(_studentDirectory));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = Assert.Single(archive.Entries, item => item.FullName == name);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<string> Snapshot(string directory) =>
        Directory
            .GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Describe(StudentPackageResult result) =>
        result.Succeeded
            ? "成功"
            : string.Join(" | ", result.Issues.Select(issue => $"{issue.Code}:{issue.Path} {issue.Message}"));
}
