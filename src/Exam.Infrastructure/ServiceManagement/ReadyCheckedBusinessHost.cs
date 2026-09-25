using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>
/// A running service process is not sufficient evidence that the exam API can accept work.
/// This adapter waits for the existing /health/ready endpoint after starting the controlled host.
/// </summary>
public sealed class ReadyCheckedBusinessHost : IManagedBusinessHost
{
    private readonly IManagedBusinessHost _processHost;
    private readonly HttpClient _httpClient;
    private readonly Uri _readyEndpoint;
    private readonly string _serverId;
    private readonly string _nodeId;
    private readonly long _serverEpoch;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private volatile bool _ready;

    public ReadyCheckedBusinessHost(
        IManagedBusinessHost processHost,
        HttpClient httpClient,
        Uri readyEndpoint,
        string serverId,
        string nodeId,
        long serverEpoch,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (readyEndpoint is null || !readyEndpoint.IsAbsoluteUri ||
            readyEndpoint.Scheme != Uri.UriSchemeHttps ||
            readyEndpoint.AbsolutePath != "/health/ready" ||
            readyEndpoint.Query.Length != 0 || readyEndpoint.Fragment.Length != 0 ||
            readyEndpoint.UserInfo.Length != 0)
        {
            throw new ArgumentException("业务就绪地址必须是部署时固定的 HTTPS /health/ready。", nameof(readyEndpoint));
        }

        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId) || serverEpoch < 1)
            throw new ArgumentException("业务服务身份必须完整。");

        _readyEndpoint = readyEndpoint;
        _serverId = serverId;
        _nodeId = nodeId;
        _serverEpoch = serverEpoch;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_timeout <= TimeSpan.Zero || _pollInterval <= TimeSpan.Zero || _pollInterval > _timeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public BusinessServiceStatus Status
    {
        get
        {
            var process = _processHost.Status;
            if (process != BusinessServiceStatus.Running)
            {
                _ready = false;
                return process;
            }

            return _ready ? BusinessServiceStatus.Running : BusinessServiceStatus.Recovering;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        await _processHost.StartAsync(cancellationToken);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_processHost.Status != BusinessServiceStatus.Running)
                throw new InvalidOperationException("业务进程在等待就绪期间退出或改变状态。");

            var remaining = _timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("业务进程已启动，但未在期限内达到业务就绪状态。");

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(remaining);
            bool ready;
            try
            {
                ready = await CheckReadyAsync(attempt.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("业务就绪接口未在期限内完成响应。");
            }

            if (ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_processHost.Status != BusinessServiceStatus.Running)
                    throw new InvalidOperationException("业务进程在就绪核对后改变状态。");
                _ready = true;
                return;
            }

            remaining = _timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("业务进程已启动，但未在期限内达到业务就绪状态。");
            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        await _processHost.StopAsync(cancellationToken);
    }

    private async Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                _readyEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                return false;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("业务就绪接口返回非预期状态。");

            await response.Content.LoadIntoBufferAsync(8192, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) || status.GetString() != "ready" ||
                !root.TryGetProperty("serverId", out var serverId) || serverId.GetString() != _serverId ||
                !root.TryGetProperty("nodeId", out var nodeId) || nodeId.GetString() != _nodeId ||
                !root.TryGetProperty("serverEpoch", out var epoch) ||
                !epoch.TryGetInt64(out var serverEpoch) || serverEpoch != _serverEpoch)
            {
                throw new InvalidOperationException("业务就绪响应身份或状态不匹配。");
            }

            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("业务就绪响应格式无效。");
        }
    }
}
