using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.Infrastructure.Packaging;

/// <summary>Shared GUI/CLI validation for the binary operator-only source-material response.</summary>
public static class PaperMaterialsDownload
{
    public static async Task<ApiEnvelope<byte[]>> ReadAsync(HttpResponseMessage response,
        string examId, long paperVersion, CancellationToken ct = default)
    {
        var serverId = Header(response, "X-Exam-Server-Id");
        var nodeId = Header(response, "X-Exam-Node-Id");
        if (!long.TryParse(Header(response, "X-Exam-Server-Epoch"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var epoch) || epoch < 1 ||
            !long.TryParse(Header(response, "X-Exam-Paper-Version"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var version) || version != paperVersion || version < 1)
            throw new InvalidDataException("源材料响应的服务器代次或试卷版本无效。");
        var expectedHash = Header(response, "X-Exam-Materials-Sha256");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("源材料响应缺少有效的内容校验值。");
        var bytes = await ReadBoundedAsync(response.Content, PaperMaterialsArchive.MaxArchiveBytes, ct);
        try
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(expectedHash, Convert.ToHexString(SHA256.HashData(bytes))))
                throw new InvalidDataException("源材料下载内容与服务器声明的校验值不一致。");
            var archive = PaperMaterialsArchive.Read(bytes);
            if (!archive.Succeeded || archive.Manifest?.ExamId != examId || archive.Manifest.PaperVersion != paperVersion)
                throw new InvalidDataException("源材料归档无效，或不属于请求的考试和试卷版本。");
            return new(serverId, nodeId, epoch, "materials-download", bytes, null);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public static async Task<ApiEnvelope<object>?> ReadFailureAsync(HttpResponseMessage response,
        CancellationToken ct = default)
    {
        try
        {
            var bytes = await ReadBoundedAsync(response.Content, 64 * 1024, ct);
            try { return JsonSerializer.Deserialize<ApiEnvelope<object>>(bytes, JsonDefaults.Create()); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return null; // Never echo a proxy body or an oversized response.
        }
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            throw new InvalidDataException("源材料响应缺少服务器身份或版本校验头。");
        var items = values.ToArray();
        if (items.Length != 1 || string.IsNullOrWhiteSpace(items[0]) || items[0].Length > 256)
            throw new InvalidDataException("源材料响应的校验头无效或重复。");
        return items[0];
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum)
            throw new InvalidDataException("源材料响应超过大小上限。");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new BoundedMaterialBuffer(maximum);
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
