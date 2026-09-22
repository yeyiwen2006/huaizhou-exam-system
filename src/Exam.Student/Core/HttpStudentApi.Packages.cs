using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;

namespace Exam.Student.Core;

public sealed partial class HttpStudentApi : IExamPackageApi
{
    public Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(
        string preparationId, string sessionId, CancellationToken ct = default) =>
        SendAsync<InstructionsDocument>(HttpMethod.Get,
            $"/api/v1/student/instructions?preparationId={Uri.EscapeDataString(preparationId)}&sessionId={Uri.EscapeDataString(sessionId)}",
            null, true, ct);

    public Task<ApiEnvelope<ParticipationResult>> ConfirmParticipationAsync(
        ConfirmParticipationRequest request, CancellationToken ct = default) =>
        SendAsync<ParticipationResult>(HttpMethod.Post, "/api/v1/participation/confirm", request, true, ct);

    public Task<ApiEnvelope<ParticipationResult>> WithdrawParticipationAsync(
        WithdrawParticipationRequest request, CancellationToken ct = default) =>
        SendAsync<ParticipationResult>(HttpMethod.Post, "/api/v1/participation/withdraw", request, true, ct);

    public Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(
        PackageUnlockRequest request, CancellationToken ct = default) =>
        SendAsync<PackageUnlockMaterial>(HttpMethod.Post, "/api/v1/resources/unlock", request, true, ct);

    public Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(
        AcknowledgeCommandRequest request, CancellationToken ct = default) =>
        SendAsync<CommandAcknowledgement>(HttpMethod.Post, "/api/v1/commands/acknowledgements", request, true, ct);

    public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(
        ExamResourceDescriptor descriptor, string preparationId, string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (descriptor.PackageLength is <= 0 or > 1024L * 1024 * 1024 || descriptor.PaperVersion < 1)
        {
            throw new InvalidOperationException("试题包长度或版本无效。");
        }

        HttpRequestMessage message;
        long generation;
        lock (_connectionGate)
        {
            var resource = ResolveSameServer(descriptor.EncryptedPackageUri);
            if (!resource.AbsolutePath.StartsWith("/api/v1/resources/", StringComparison.Ordinal) ||
                resource.AbsolutePath["/api/v1/resources/".Length..].Contains('/') ||
                !string.IsNullOrEmpty(resource.Query))
            {
                throw new InvalidOperationException("题包地址不属于受支持的版本资源接口。");
            }
            var path = resource.AbsoluteUri + $"?preparationId={Uri.EscapeDataString(preparationId)}&sessionId={Uri.EscapeDataString(sessionId)}";
            message = CreateRequest(HttpMethod.Get, path, true);
            generation = _connectionGeneration;
        }
        using var request = message;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/vnd.huaizhou.exam-package" ||
            (response.Content.Headers.ContentLength is { } advertised && advertised != descriptor.PackageLength))
        {
            throw new HttpRequestException("试题获取失败！服务器未返回匹配的加密题包。");
        }
        var serverId = Header(response, "X-Exam-Server-Id");
        if (!long.TryParse(Header(response, "X-Exam-Server-Epoch"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ||
            !long.TryParse(Header(response, "X-Exam-Paper-Version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version != descriptor.PaperVersion || string.IsNullOrWhiteSpace(Header(response, "X-Exam-Node-Id")))
        {
            throw new HttpRequestException("题包响应缺少有效的服务器身份或版本。");
        }
        lock (_connectionGate)
        {
            if (generation != _connectionGeneration) { throw new HttpRequestException("连接已切换，停止旧连接下载。"); }
            ValidateServer(serverId, epoch);
        }
        await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await content.ReadAsync(buffer, idle.Token);
            if (count == 0) { break; }
            total += count;
            if (total > descriptor.PackageLength) { throw new IOException("试题包长度超出准备记录。"); }
            lock (_connectionGate)
            {
                if (generation != _connectionGeneration) { throw new HttpRequestException("连接已切换，停止旧连接下载。"); }
            }
            yield return buffer[..count];
        }
        if (total != descriptor.PackageLength) { throw new IOException("试题包未下载完整。"); }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() ?? string.Empty : string.Empty;
}
