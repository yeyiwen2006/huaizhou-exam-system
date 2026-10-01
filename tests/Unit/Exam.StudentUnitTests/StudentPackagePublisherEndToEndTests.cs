using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// D03 发布流程的端到端用例：**真编译**带标记的合成题 → 发布（抽取坐标 + 组包 + 加密）→
/// 按学生端路径解密 → 学生清单 → 学生端读取器读出坐标。
///
/// 用真实 XeLaTeX 才能产生带隐藏外观链接的 PDF，因此标记 Category=Latex：
/// CI 运行机没有 TeX 发行版，必须在流水线里排除；本机没有 XeLaTeX 时明确失败而不是跳过。
/// 夹具全部为合成内容，未使用真实试卷材料。
/// </summary>
public sealed partial class StudentPackagePublisherEndToEndTests : IDisposable
{
    private const string ExamId = "synthetic-publish";
    private const long PaperVersion = 1;
    private const string QuestionId = "q-001";
    private const string SourceRelativePath = "questions/合成选项题_1.tex";

    private static readonly string[] OptionIds = ["A", "B", "C", "D"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "exam-publish-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    [Trait("Category", "Latex")]
    public async Task Publish_RealCompiledQuestion_YieldsV2PackageReadableByStudentReader()
    {
        var packageRoot = Path.Combine(_root, "package");
        var compilation = Path.Combine(_root, "compiled");
        var target = Path.Combine(_root, "paper.epkg");
        WritePackage(packageRoot);

        var compiled = await CompileAsync(packageRoot, compilation);
        using var node = CreateNode();
        var service = new PackageEncryptionService(4096);
        var result = await new StudentPackagePublisher(service).PublishAsync(
            ImportManifest(),
            new StudentPackageRequest(packageRoot, compilation, Path.Combine(_root, "preview"),
                "package", PaperVersion, target, [new("node", node)]) { CompiledQuestions = compiled });

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(PaperVersion, result.Plan.PaperVersion);
        Assert.Contains(OptionRegionArtifact.FileName, result.Plan.IncludedPaths);

        // 按学生端路径解密，交给学生清单与学生端读取器核对。
        var plaintext = await DecryptAsStudentAsync(service, result.Envelope!, node, target);
        var manifest = StudentPaperPackage.ReadManifest(plaintext, ExamId, PaperVersion);
        Assert.Equal(StudentPaperPackage.NavigationSchemaVersion, manifest.SchemaVersion);

        var map = OptionRegionMap.TryReadFromPackage(plaintext, manifest);
        Assert.NotNull(map);
        var options = map!.ForQuestion(QuestionId)!;
        Assert.Equal(OptionIds, options.Keys.OrderBy(id => id, StringComparer.Ordinal));

        // 真编译得到的区域应满足契约的取值约束，并落在同一个页面列附近。
        foreach (var (_, region) in options)
        {
            Assert.Equal(0, region.PageIndex);
            Assert.InRange(region.X, 0, 1);
            Assert.InRange(region.Y, 0, 1);
            Assert.True(region.Width > 0 && region.Height > 0);
            Assert.True(region.X + region.Width <= 1 && region.Y + region.Height <= 1);
        }

        var ordered = options.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToList();
        for (var index = 1; index < ordered.Count; index++)
            Assert.True(ordered[index].Y > ordered[index - 1].Y, "选项应按纵向递增排列");
    }

    [Fact]
    public async Task Publish_WithoutCompileResults_IsRejectedSoNoNonClickablePackageIsProduced()
    {
        var packageRoot = Path.Combine(_root, "package");
        WritePackage(packageRoot);

        var result = await new StudentPackagePublisher(new PackageEncryptionService(4096)).PublishAsync(
            ImportManifest(),
            new StudentPackageRequest(packageRoot, Path.Combine(_root, "compiled"), Path.Combine(_root, "preview"),
                "package", PaperVersion));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StudentPackageIssueCode.OptionRegionInvalid);
        Assert.False(Directory.Exists(Path.Combine(_root, "preview")));
    }

    private static async Task<Dictionary<string, TeXCompileResult>> CompileAsync(string packageRoot, string compilation)
    {
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        Assert.True(toolchain is not null,
            "本机没有可用的 XeLaTeX。Category=Latex 的用例需要 TeX 发行版（本项目要求 XeLaTeX）。");

        var runner = new ExternalProcessRunner();
        var version = await runner.RunAsync(toolchain!.ExecutablePath, ["--version"],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10));
        Assert.Equal(0, version.ExitCode);
        var platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
            ? XeLaTeXPlatform.MiKTeX : XeLaTeXPlatform.TeXLive;

        var compiler = new XeLaTeXCompiler(runner, toolchain with { Platform = platform }, TimeSpan.FromMinutes(3));
        var result = await compiler.CompileAsync(new TeXCompileRequest(packageRoot, SourceRelativePath, compilation));
        Assert.True(result.Succeeded, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return new Dictionary<string, TeXCompileResult> { [QuestionId] = result };
    }

    private static async Task<byte[]> DecryptAsStudentAsync(
        PackageEncryptionService service, EncryptedPackageEnvelope envelope, X509Certificate2 node, string target)
    {
        var material = new PackageUnlockMaterial(envelope.PackageId, envelope.PaperVersion, envelope.ChunkSize,
            envelope.NoncePrefix, PackageEncryptionService.UnwrapDataKey(envelope, "node", node));
        await using var plaintext = new MemoryStream();
        await using var ciphertext = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        await PackageCiphertext.DecryptAsync(ciphertext, plaintext, material);
        return plaintext.ToArray();
    }

    private static X509Certificate2 CreateNode()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-publish-node", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

        // 考试名称必须与题目源文件名前缀一致：合成选项题_1.tex → 合成选项题。
    private static ExamManifest ImportManifest() => new(1, ExamId, "合成选项题",
        [new("subject", "合成科目", "1")],
        [new("group", "subject", "单项选择", "single", 1, 1, false, "1",
            new(new("1", "0", "0"), null))],
        [new(QuestionId, 1, "subject", "group", SourceRelativePath, OptionIds, null)], []);

    private void WritePackage(string packageRoot)
    {
        Directory.CreateDirectory(Path.Combine(packageRoot, "questions"));
        File.WriteAllText(Path.Combine(packageRoot, "manifest.json"),
            JsonSerializer.Serialize(ImportManifest(), ImportJson.Create()), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(packageRoot, SourceRelativePath), """
            \documentclass[UTF8]{ctexart}
            \pagestyle{empty}
            \usepackage{hyperref}
            \hypersetup{hidelinks}
            \DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
            \begin{document}
            合成题：下列选项正确的是（\quad）。

            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[\optmark{q-001}{D}{D.}] 丁
            \end{enumerate}
            \end{document}
            """, new UTF8Encoding(false));
    }

    private static string Describe(StudentPackageResult result) =>
        string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
}
