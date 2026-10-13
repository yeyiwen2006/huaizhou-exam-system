using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.Archives;

// Portable archives contain the student ZIP, not a source server's private certificate or wrapped key.
public sealed record PortableExamResource(string ResourceId, string ExamId, string ResourceKind, long Version,
    byte[] Content, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record ReboundExamResource(VersionedEncryptedResource Resource, string StudentPackageSha256);

public sealed class PortableExamResources(IPackageKeyUnwrapper keyUnwrapper,
    IPublicationCertificateProvider certificates, PackageEncryptionService encryption)
{
    public async Task<PortableExamResource> ExportAsync(VersionedEncryptedResource resource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        RequirePaper(resource.ResourceKind);
        if (resource.ResourceId != resource.Envelope.PackageId || resource.Version != resource.Envelope.PaperVersion)
            throw new InvalidDataException("考试资源与加密信封的标识或版本不一致。");
        using var material = new PackageUnlockMaterial(resource.ResourceId, resource.Version,
            resource.Envelope.ChunkSize, resource.Envelope.NoncePrefix, keyUnwrapper.Unwrap(resource.Envelope));
        using var input = new MemoryStream(resource.EncryptedContent, writable: false);
        using var output = new BoundedMaterialBuffer(StudentPaperPackage.MaxPackageBytes);
        await PackageCiphertext.DecryptAsync(input, output, material, ct);
        var plaintext = output.ToArray();
        try
        {
            StudentPaperPackage.ReadManifest(plaintext, resource.ExamId, resource.Version, ct);
            return new(resource.ResourceId, resource.ExamId, resource.ResourceKind, resource.Version,
                plaintext, resource.CreatedAt, resource.CreatedBy);
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }

    public async Task<ReboundExamResource> RebindAsync(PortableExamResource resource,
        string newExamId, string newExamName, string newResourceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        RequirePaper(resource.ResourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(newExamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newExamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newResourceId);
        var manifest = StudentPaperPackage.ReadManifest(resource.Content, resource.ExamId, resource.Version, ct);
        var paths = manifest.Questions.ToDictionary(question => question.PdfPath,
            question => $"questions/{newExamName}_{question.Structure.QuestionNo}.pdf", StringComparer.Ordinal);
        var rebound = manifest with
        {
            ExamId = newExamId, ExamName = newExamName,
            Questions = manifest.Questions.Select(question => question with { PdfPath = paths[question.PdfPath] }).ToArray()
        };
        using var original = new MemoryStream(resource.Content, writable: false);
        using var source = new ZipArchive(original, ZipArchiveMode.Read);
        using var plaintext = new BoundedMaterialBuffer(StudentPaperPackage.MaxPackageBytes);
        using (var target = new ZipArchive(plaintext, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var path = paths.GetValueOrDefault(entry.FullName, entry.FullName);
                var copy = target.CreateEntry(path, CompressionLevel.Optimal);
                copy.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = copy.Open();
                if (entry.FullName == StudentPaperPackage.ManifestPath)
                    await JsonSerializer.SerializeAsync(output, rebound, ImportJson.Create(), ct);
                else if (entry.FullName == OptionRegionArtifact.FileName)
                {
                    using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
                    var regions = OptionRegionArtifactJson.Deserialize(await reader.ReadToEndAsync(ct));
                    if (regions.Artifact is null) throw new InvalidDataException("选项坐标文件无效。");
                    var bytes = Encoding.UTF8.GetBytes(OptionRegionArtifactJson.Serialize(regions.Artifact with { ExamId = newExamId }));
                    await output.WriteAsync(bytes, ct);
                }
                else
                {
                    using var input = entry.Open();
                    await input.CopyToAsync(output, ct);
                }
            }
        }
        var bytesToEncrypt = plaintext.ToArray();
        try
        {
            StudentPaperPackage.ReadManifest(bytesToEncrypt, newExamId, resource.Version, ct);
            var packageHash = Convert.ToHexString(SHA256.HashData(bytesToEncrypt)).ToLowerInvariant();
            using var input = new MemoryStream(bytesToEncrypt, writable: false);
            using var output = new MemoryStream();
            using var recipients = certificates.Acquire();
            var envelope = await encryption.EncryptAsync(input, output, newResourceId,
                resource.Version, recipients.Recipients, ct);
            return new(new(newResourceId, newExamId, resource.ResourceKind, resource.Version,
                output.ToArray(), envelope, resource.CreatedAt, resource.CreatedBy), packageHash);
        }
        finally { CryptographicOperations.ZeroMemory(bytesToEncrypt); }
    }

    public static PaperMaterialsRow RebindMaterials(PaperMaterialsRow row, string newExamId,
        string newExamName, string newStudentPackageSha256)
    {
        var archive = PaperMaterialsArchive.Read(row.Archive);
        if (!archive.Succeeded || archive.Materials is null || archive.Manifest is null ||
            archive.Manifest.ExamId != row.ExamId || archive.Manifest.PaperVersion != row.PaperVersion)
            throw new InvalidDataException("源材料归档与考试或试卷版本不一致。");
        var files = archive.Materials.Paths.ToDictionary(path => path, archive.Materials.Read, StringComparer.OrdinalIgnoreCase);
        var manifestPath = archive.Materials.Resolve("manifest.json") ?? throw new InvalidDataException("源材料缺少清单。");
        var answerPath = archive.Materials.Resolve("server-only/answer-key.json") ?? throw new InvalidDataException("源材料缺少独立答案。");
        var sourceManifest = JsonSerializer.Deserialize<ExamManifest>(files[manifestPath], ImportJson.Create())
            ?? throw new InvalidDataException("源材料清单无效。");
        var manifestJson = RebindManifestJson(Encoding.UTF8.GetString(files[manifestPath]), newExamId, newExamName);
        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())!;
        foreach (var question in sourceManifest.Questions)
        {
            var oldPath = archive.Materials.Resolve(question.Source) ?? throw new InvalidDataException("源材料缺少题目文件。");
            var newPath = ReboundSourcePath(question.Source, question.QuestionNo, newExamName);
            if (!StringComparer.OrdinalIgnoreCase.Equals(oldPath, newPath) && files.ContainsKey(newPath))
                throw new InvalidDataException("新考试名称对应的题目文件已被其他材料占用。");
            // Keep original paths as aliases so existing relative TeX includes keep resolving.
            files[newPath] = files[oldPath];
        }
        files[manifestPath] = Encoding.UTF8.GetBytes(manifestJson);
        files[answerPath] = Encoding.UTF8.GetBytes(RebindAnswerKeyJson(Encoding.UTF8.GetString(files[answerPath]), newExamId));
        var reboundArchive = PaperMaterialsArchive.Build(newExamId, row.PaperVersion, files);
        return row with
        {
            ExamId = newExamId, Archive = reboundArchive,
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(reboundArchive)).ToLowerInvariant(),
            PackageContentSha256 = newStudentPackageSha256,
            FormatVersion = PaperMaterialsArchive.CurrentFormatVersion,
            FileCount = files.Count,
            QuestionDigests = QuestionContentDigest.Compute(manifest, new InMemoryMaterialFileSource(files))
        };
    }

    public static string RebindManifestJson(string json, string newExamId, string newExamName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newExamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newExamName);
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("试卷清单无效。");
        root["examId"] = newExamId;
        root["examName"] = newExamName;
        if (root["questions"] is not JsonArray questions) throw new InvalidDataException("试卷清单缺少题目。");
        foreach (var question in questions)
        {
            if (question is not JsonObject item || item["source"] is not JsonValue path || item["questionNo"] is not JsonValue number)
                throw new InvalidDataException("试卷题目缺少源文件或题号。");
            item["source"] = ReboundSourcePath(path.GetValue<string>(), number.GetValue<int>(), newExamName);
        }
        return root.ToJsonString(ImportJson.Create());
    }

    public static string RebindAnswerKeyJson(string json, string newExamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newExamId);
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("独立答案无效。");
        root["examId"] = newExamId;
        return root.ToJsonString(ImportJson.Create());
    }

    private static string ReboundSourcePath(string source, int number, string examName)
    {
        var normalized = source.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return (separator < 0 ? "" : normalized[..(separator + 1)]) + examName + "_" +
            number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tex";
    }

    private static void RequirePaper(string kind)
    {
        if (kind != "paper-package") throw new InvalidDataException("归档包含不支持的加密资源类型。");
    }
}
