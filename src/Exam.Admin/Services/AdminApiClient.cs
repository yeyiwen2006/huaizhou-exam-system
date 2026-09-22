using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Versioning;

namespace Exam.Admin.Services;

public sealed class AdminApiClient : IDisposable
{
    private HttpClient _httpClient = new();
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();
    private string _operatorId = string.Empty;

    public bool IsConfigured => _httpClient.BaseAddress is not null &&
                                _httpClient.DefaultRequestHeaders.Authorization is not null &&
                                !string.IsNullOrWhiteSpace(_operatorId);

    public string OperatorId => _operatorId;

    public void Configure(string baseAddress, string operatorToken, string operatorId)
    {
        if (!Uri.TryCreate(baseAddress.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("服务器地址必须是有效的 http 或 https 地址。", nameof(baseAddress));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operatorToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

        var normalized = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);
        var configuredClient = new HttpClient { BaseAddress = normalized };
        configuredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var previousClient = Interlocked.Exchange(ref _httpClient, configuredClient);
        previousClient.Dispose();
        _operatorId = operatorId.Trim();
    }

    public async Task CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var response = await _httpClient.GetAsync("health/ready", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AdminApiException("SERVICE_NOT_READY", $"服务器就绪检查失败：HTTP {(int)response.StatusCode}。", response.StatusCode);
        }
    }

    public async Task<ExamVersionSnapshot> SaveScheduleAsync(
        string examId,
        ScheduleWindow schedule,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var current = await TryGetExamAsync(examId, cancellationToken);
        var requestId = Guid.NewGuid().ToString("N");
        if (current is null)
        {
            return await SendAsync<ExamVersionSnapshot>(
                HttpMethod.Post,
                "api/v1/versioning/exams",
                new { requestId, examId, schedule },
                cancellationToken);
        }

        return await SendAsync<ExamVersionSnapshot>(
            HttpMethod.Put,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}/schedule",
            new { requestId, expectedVersion = current.Versions.ScheduleVersion, schedule },
            cancellationToken);
    }

    public async Task<ActivationResult> ApproveActivationAsync(
        string activationRequestId,
        string? roomGroupId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ActivationResult>(
            HttpMethod.Post,
            "api/v1/activation/approve",
            new ApproveActivationRequest(Guid.NewGuid().ToString("N"), activationRequestId, Normalize(roomGroupId)),
            cancellationToken);

    public async Task<ActivationResult> FindDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ActivationResult>(
            HttpMethod.Get,
            $"api/v1/activation/devices/{Uri.EscapeDataString(deviceId)}",
            null,
            cancellationToken);

    public async Task<IReadOnlyList<ParticipationResult>> ListActualParticipationsAsync(
        string examId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<IReadOnlyList<ParticipationResult>>(
            HttpMethod.Get,
            $"api/v1/participation/exams/{Uri.EscapeDataString(examId)}/actual",
            null,
            cancellationToken);

    public async Task<DeviceDecisionResult> DecideDeviceAsync(
        string participationId,
        string selectedDeviceId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default) =>
        await SendAsync<DeviceDecisionResult>(
            HttpMethod.Post,
            "api/v1/participation/device-decisions",
            new DeviceDecisionRequest(
                Guid.NewGuid().ToString("N"),
                participationId,
                selectedDeviceId,
                expectedVersion,
                _operatorId,
                reason),
            cancellationToken);

    public async Task<CommandEventView> ResendCommandAsync(
        string eventId,
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        CancellationToken cancellationToken = default) =>
        await SendAsync<CommandEventView>(
            HttpMethod.Post,
            $"api/v1/commands/{Uri.EscapeDataString(eventId)}/resend",
            new
            {
                requestId = Guid.NewGuid().ToString("N"),
                participationId,
                entryVersion,
                deviceId,
                authorizationVersion
            },
            cancellationToken);

    private async Task<ExamVersionSnapshot?> TryGetExamAsync(string examId, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync<ExamVersionSnapshot>(
                HttpMethod.Get,
                $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}",
                null,
                cancellationToken);
        }
        catch (AdminApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativeUri,
        object? payload,
        CancellationToken cancellationToken)
        where T : class
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(method, relativeUri);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: _json);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        ApiEnvelope<T>? envelope = null;
        if (response.Content.Headers.ContentLength != 0)
        {
            envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(_json, cancellationToken);
        }

        if (!response.IsSuccessStatusCode || envelope?.Error is not null)
        {
            var error = envelope?.Error;
            throw new AdminApiException(
                error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                error?.Message ?? $"服务器请求失败：HTTP {(int)response.StatusCode}。",
                response.StatusCode);
        }

        return envelope?.Result ?? throw new AdminApiException(
            "EMPTY_RESPONSE",
            "服务器返回成功状态，但未包含结果。",
            response.StatusCode);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("请先填写服务器地址、考务身份和令牌并连接服务器。");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose() => _httpClient.Dispose();
}

public sealed class AdminApiException(string code, string message, HttpStatusCode statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
