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
    public sealed class DownloadEndpointChangedException() : HttpRequestException("可信服务器已切换，重新下载同一版本题包。");
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
        if (_device?.Topology is not null)
            RequireResult(await GetServerTimeAsync(ct));

        HttpRequestMessage message;
        long generation;
        long identityGeneration;
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
            identityGeneration = _identityGeneration;
        }
        using var request = message;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        HttpResponseMessage received;
        try { received = await SendTrustedAsync(request, deadline.Token, HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception error) when (error is HttpRequestException and not UntrustedServerException and not IdentityRejectedException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (await TryReconnectAsync(identityGeneration, generation, ct)) throw new DownloadEndpointChangedException();
            throw;
        }
        using var response = received;
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
            EnsureDownloadContext(identityGeneration, generation);
            ValidateIdentity(serverId, Header(response, "X-Exam-Node-Id"), epoch);
            ValidateServer(serverId, epoch);
        }
        await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            int count;
            try { count = await content.ReadAsync(buffer, idle.Token); }
            catch (Exception error) when (error is IOException or HttpRequestException ||
                error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (await TryReconnectAsync(identityGeneration, generation, ct)) throw new DownloadEndpointChangedException();
                throw;
            }
            if (count == 0) { break; }
            total += count;
            if (total > descriptor.PackageLength) { throw new IOException("试题包长度超出准备记录。"); }
            lock (_connectionGate)
            {
                EnsureDownloadContext(identityGeneration, generation);
            }
            yield return buffer[..count];
        }
        lock (_connectionGate) { EnsureDownloadContext(identityGeneration, generation); }
        if (total != descriptor.PackageLength)
        {
            if (await TryReconnectAsync(identityGeneration, generation, ct)) throw new DownloadEndpointChangedException();
            throw new IOException("试题包未下载完整。");
        }
    }

    private void EnsureDownloadContext(long identityGeneration, long generation)
    {
        if (identityGeneration != _identityGeneration)
            throw new IdentityRejectedException("设备身份已切换，停止原题包请求；原文件已保留。");
        if (generation != _connectionGeneration) throw new DownloadEndpointChangedException();
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() ?? string.Empty : string.Empty;
}
