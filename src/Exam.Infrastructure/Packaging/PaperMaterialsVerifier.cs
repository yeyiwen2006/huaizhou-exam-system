using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Exam.Contracts.Administration;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// 上传的**源材料归档**与「声明变更题号」的核对。全部在**进入发布事务之前**完成：
/// 解密 → 比对声明的明文哈希 → 解析材料 → 逐题内容摘要 → 与上一已发布版本的基线比对。
///
/// 核对口径（A 已确认）：服务器是权威方，客户端预览提示不能代替这里；
/// **漏报**实际变更的题与**把未变的题报成变更**都拒绝——前者会让该题考生收不到复核提示，
/// 后者会逼学生复核没变的题。没有任何内容变化时拒绝走试卷修订（应使用规则修订入口）。
///
/// 上一版本**没有归档**时失败关闭（`MaterialsBaselineMissing`），不当作首次发布：
/// 否则删掉一行就能绕过核对。
/// </summary>
public sealed class PaperMaterialsVerifier(
    IPackageKeyUnwrapper unwrapper,
    ManagedNodeEncryptionPolicy policy,
    PostgresPaperMaterialStore store)
{
    /// <summary>解密上限：归档上限再留一点头，避免把异常大的密文解进内存。</summary>
    private const int MaxPlaintextBytes = PaperMaterialsArchive.MaxArchiveBytes;

    public async Task<PaperMaterialsVerdict> VerifyAsync(string examId, PublishExamBundleApiRequest request,
        PublicationPackageEnvelope? envelope, byte[] ciphertext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (envelope is null || ciphertext is not { Length: > 0 } ||
            ciphertext.Length > MaxPlaintextBytes + 1024 * 1024 ||
            request.ExpectedPaperVersion is < 0 or long.MaxValue)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                "发布必须同时提交源材料归档：没有归档就无法核对实际变更题号。");
        }

        if (request.MaterialsSha256 is not { Length: 64 } declaredHash ||
            !declaredHash.All(Uri.IsHexDigit))
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.InvalidRequest,
                "发布请求缺少有效的源材料归档哈希。");
        }

        var expectedVersion = checked(request.ExpectedPaperVersion + 1);
        if (envelope.PackageId is not { Length: > 0 and <= 128 } ||
            envelope.WrappedKeys is not { Count: > 0 and <= 32 } ||
            envelope.CertificateThumbprints is not { Count: > 0 and <= 32 } ||
            envelope.PaperVersion != expectedVersion ||
            !policy.RequiredNodeIds.IsSubsetOf(envelope.WrappedKeys.Keys.ToHashSet(StringComparer.Ordinal)) ||
            !policy.RequiredNodeIds.IsSubsetOf(envelope.CertificateThumbprints.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                "源材料归档的信封、版本或受管节点覆盖不完整。");
        }

        // 传输信封（Contracts）与加密层信封（Security）是两个类型，字段一一对应。
        var transport = new EncryptedPackageEnvelope(envelope.PackageId, envelope.PaperVersion,
            envelope.ChunkSize, envelope.NoncePrefix, envelope.WrappedKeys, envelope.CertificateThumbprints);
        byte[] plaintext;
        byte[]? key = null;
        try
        {
            key = unwrapper.Unwrap(transport);
            using var material = new PackageUnlockMaterial(transport.PackageId, transport.PaperVersion,
                transport.ChunkSize, transport.NoncePrefix, key);
            using var input = new MemoryStream(ciphertext, writable: false);
            using var output = new BoundedMaterialBuffer(MaxPlaintextBytes);
            await PackageCiphertext.DecryptAsync(input, output, material, ct);
            if (output.Length is 0 or > MaxPlaintextBytes)
            {
                return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                    "源材料归档解密结果超出允许大小。");
            }

            plaintext = output.ToArray();
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or ArgumentException or FormatException)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                "源材料归档密文或节点解锁材料校验失败，未发布。");
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        // 先按收到的字节核对声明的哈希：不信任调用方。大小写不敏感，但两侧必须一致地比较。
        var actualHash = Convert.ToHexString(SHA256.HashData(plaintext));
        if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, declaredHash))
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                "源材料归档内容与请求声明的哈希不一致，未发布。");
        }

        var archive = PaperMaterialsArchive.Read(plaintext);
        if (!archive.Succeeded || archive.Materials is null || archive.Manifest is null)
        {
            var failure = archive.Failure ?? new KeyValuePair<string, string>("MaterialsArchiveInvalid", "材料归档不可用。");
            return PaperMaterialsVerdict.Failed(
                failure.Key == "MaterialsFormatUnsupported"
                    ? VersioningErrorCode.MaterialsFormatUnsupported
                    : VersioningErrorCode.MaterialsArchiveInvalid,
                failure.Value);
        }

        if (!StringComparer.Ordinal.Equals(archive.Manifest.ExamId, examId) ||
            archive.Manifest.PaperVersion != expectedVersion)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                "源材料归档的考试或试卷版本与本次发布不一致。");
        }

        // 逐题摘要按**请求里的清单**计算：它与已发布结构一致（版本服务 会再核对一次）。
        IReadOnlyDictionary<string, string> digests;
        try
        {
            // The downloadable source must describe exactly what the transaction publishes.
            // Otherwise a valid archive hash can hide missing/obsolete answers or a different manifest.
            foreach (var (path, json) in new[]
            {
                ("manifest.json", request.ManifestJson),
                ("server-only/answer-key.json", request.AnswerKeyJson)
            })
            {
                var source = archive.Materials.Resolve(path);
                if (source is null || !JsonNode.DeepEquals(JsonNode.Parse(archive.Materials.Read(source)), JsonNode.Parse(json)))
                    throw new InvalidDataException("归档清单或独立答案与本次发布请求不一致。");
            }
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ExamManifest>(
                request.ManifestJson, ImportJson.Create())
                ?? throw new InvalidDataException("清单无效。");
            digests = QuestionContentDigest.Compute(manifest, archive.Materials);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsArchiveInvalid,
                "无法按本次清单核对源材料、独立答案及逐题摘要，未发布。");
        }

        // 请求体可能是显式 null（JSON 里写 null 就是空声明），按空集处理而不是抛异常。
        var declared = (request.ChangedQuestionIds ?? [])
            .Where(questionId => !string.IsNullOrWhiteSpace(questionId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(questionId => questionId, StringComparer.Ordinal)
            .ToList();

        if (request.ExpectedPaperVersion == 0)
        {
            // 首次发布没有可比对的基线：整卷都是新的，逐题摘要写入基线供后续修订比对。
            // 声明要么留空、要么就是整卷——只声明一部分没有意义（学生届时还没有旧卷可复核）。
            if (declared.Count != 0 &&
                (declared.Count != digests.Count || declared.Any(questionId => !digests.ContainsKey(questionId))))
            {
                return PaperMaterialsVerdict.Failed(VersioningErrorCode.ChangedQuestionIdsMissing,
                    "首次发布只能声明整卷题目或留空，不能只声明其中一部分。");
            }

            return PaperMaterialsVerdict.Approve(plaintext, actualHash, archive, digests);
        }

        var baseline = await store.FindBaselineAsync(examId, request.ExpectedPaperVersion, ct);
        if (baseline is null)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsBaselineMissing,
                $"上一已发布版本（{request.ExpectedPaperVersion}）没有源材料归档，无法核对变更题号；" +
                "请恢复这一版本的原始归档并通过恢复检查；不能将修订当作首次发布绕过核对。");
        }

        if (baseline.FormatVersion != QuestionContentDigest.CurrentFormatVersion)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.MaterialsFormatUnsupported,
                $"已存材料摘要的格式版本 {baseline.FormatVersion} 与当前算法" +
                $"（{QuestionContentDigest.CurrentFormatVersion}）不一致，无法核对。");
        }

        var actual = QuestionContentDigest.Changed(baseline.QuestionDigests, digests);
        if (actual.Count == 0 && declared.Count == 0)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.NoContentChange,
                "本次材料没有任何题目内容变化：只改答案或计分规则请使用规则修订入口。");
        }

        var missing = actual.Where(questionId => !declared.Contains(questionId, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.ChangedQuestionIdsMissing,
                $"声明漏了实际变更的题目：{string.Join("、", missing.Take(5))}" +
                (missing.Count > 5 ? $" 等 {missing.Count} 题" : string.Empty) + "。");
        }

        var unexpected = declared.Where(questionId => !actual.Contains(questionId, StringComparer.Ordinal)).ToList();
        if (unexpected.Count > 0)
        {
            return PaperMaterialsVerdict.Failed(VersioningErrorCode.ChangedQuestionIdsUnexpected,
                $"这些题目本次实际没有变化，却被声明为变更：{string.Join("、", unexpected.Take(5))}" +
                (unexpected.Count > 5 ? $" 等 {unexpected.Count} 题" : string.Empty) + "。" +
                "把它们报成变更会让考生复核没变的题。");
        }

        return PaperMaterialsVerdict.Approve(plaintext, actualHash, archive, digests);
    }
}

/// <summary>
/// 归档校验结论。接受时给出：**明文归档字节**（按既有惯例以明文入库，独立答案本来就以明文存在于
/// 版本事件里）、明文哈希、已解析的归档与逐题摘要；调用方据此补齐学生包哈希与操作者后入库。
/// 拒绝时给出稳定错误码与说明。
/// </summary>
public sealed record PaperMaterialsVerdict(
    bool Accepted,
    byte[]? Plaintext,
    string? ArchiveSha256,
    PaperMaterialsArchiveResult? Archive,
    IReadOnlyDictionary<string, string> Digests,
    VersioningErrorCode ErrorCode,
    string? Message)
{
    /// <summary>按调用方掌握的包哈希与操作者构造待入库的归档行。</summary>
    public PaperMaterialsRow ToRow(string packageContentSha256, string actor, DateTimeOffset createdAt) => new(
        Archive!.Manifest!.ExamId,
        Archive.Manifest.PaperVersion,
        Plaintext!,
        ArchiveSha256!,
        packageContentSha256,
        Archive.Manifest.FormatVersion,
        Archive.Manifest.Files.Count,
        Digests,
        createdAt,
        actor);

    public static PaperMaterialsVerdict Failed(VersioningErrorCode code, string message) =>
        new(false, null, null, null, new Dictionary<string, string>(StringComparer.Ordinal), code, message);

    public static PaperMaterialsVerdict Approve(byte[] plaintext, string archiveSha256,
        PaperMaterialsArchiveResult archive, IReadOnlyDictionary<string, string> digests) =>
        new(true, plaintext, archiveSha256, archive, digests, VersioningErrorCode.None, null);
}
