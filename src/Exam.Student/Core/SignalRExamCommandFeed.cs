using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.Student.Core;

/// <summary>Authenticated WSS with durable CatchUp on connect, reconnect and every 30 seconds.</summary>
public sealed class SignalRExamCommandFeed(DeviceRecord device, ServerNodeEnvelope server,
    Func<Uri, HubConnection>? connectionFactory = null, TimeSpan? catchUpInterval = null) : IExamCommandFeed
{
    // 实时连接与业务请求共用 ServerTrust 信任策略（部署指纹 pin 或标准链校验）。
    private readonly HttpMessageInvoker _socketHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = { RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
            device.Topology is { } topology ? ServerTrust.ValidateNodeCertificate(topology, sender, certificate, errors)
                : ServerTrust.ValidateCertificate(sender, certificate, chain, errors) }
    });
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private const int MaxPendingCommands = 16;
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingDelivery> _pending = new(StringComparer.Ordinal);
    private HubConnection? _connection;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private Task? _authorizationLoss;
    private ParticipationResult? _participation;
    private int _pendingCommands;
    private bool _pendingEnd;
    private volatile bool _replaced;
    private volatile bool _stopping;
    private bool _disposed;
    private string? _connectionError;
    private string? _processingError;
    private readonly TimeSpan _catchUpInterval = catchUpInterval ?? TimeSpan.FromSeconds(30);
    public Func<CancellationToken, Task>? AuthorizationLost { get; set; }
    public string? LastError => _replaced
        ? "当前设备授权或连接已失效，已停止处理试题，请重新核对参加考试状态。"
        : _processingError ?? _connectionError;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected && !_replaced;

    public async Task StartAsync(ParticipationResult participation,
        Func<CommandDelivery, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stopping) { throw new InvalidOperationException("指令连接已停止，请建立新连接。"); }
        if (_loop is not null) { return; }
        if (device.ServerId != server.ServerId || string.IsNullOrWhiteSpace(server.NodeId) ||
            server.ServerEpoch < device.ServerEpoch || server.ServerEpoch < 1 ||
            participation.AuthorizedDeviceId != device.DeviceId || participation.EntryVersion < 1 || participation.AuthorizationVersion < 1)
        {
            throw new InvalidOperationException("实时连接身份与当前参加考试授权不匹配。");
        }
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, device.Host, device.Port, "/hubs/exam-events")
        {
            Query = $"participationId={Uri.EscapeDataString(participation.ParticipationId)}" +
                    $"&entryVersion={participation.EntryVersion.ToString(CultureInfo.InvariantCulture)}" +
                    $"&deviceId={Uri.EscapeDataString(device.DeviceId)}" +
                    $"&authorizationVersion={participation.AuthorizationVersion.ToString(CultureInfo.InvariantCulture)}" +
                    $"&serverId={Uri.EscapeDataString(server.ServerId)}&serverEpoch={server.ServerEpoch.ToString(CultureInfo.InvariantCulture)}"
        }.Uri;
        _lifetime = new CancellationTokenSource();
        _participation = participation;
        var lifetime = _lifetime.Token;
        _connection = connectionFactory?.Invoke(endpoint) ?? new HubConnectionBuilder()
            .WithUrl(endpoint, options =>
            {
                // Direct WebSockets avoid negotiation redirects carrying device credentials to another origin.
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.ApplicationMaxBufferSize = 1024 * 1024;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    if (context.Uri.Scheme != "wss" || context.Uri.Host != endpoint.Host || context.Uri.Port != endpoint.Port)
                    {
                        throw new HttpRequestException("实时连接拒绝不可信的服务器地址。");
                    }
                    var socket = new ClientWebSocket();
                    socket.Options.SetRequestHeader("X-Exam-Device-Credential", device.Credential);
                    try
                    {
                        await socket.ConnectAsync(context.Uri, _socketHttp, cancellationToken);
                        return socket;
                    }
                    catch { socket.Dispose(); throw; }
                };
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        _connection.ServerTimeout = TimeSpan.FromSeconds(45);
        _connection.HandshakeTimeout = TimeSpan.FromSeconds(10);
        // SignalR waits for an async On callback before dispatching the next invocation. Never
        // return a slow START task to it: later END/revocation must reach the manager immediately.
        _connection.On<CommandDelivery>("Command", delivery => { QueueDelivery(delivery, handler, lifetime); });
        _connection.On<string>("ConnectionReplaced", _ => { LoseAuthorizationAsync(); });
        _connection.On<string>("AuthorizationRevoked", _ => { LoseAuthorizationAsync(); });
        try
        {
            using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
            startDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            await _connection.StartAsync(startDeadline.Token);
            await CatchUpAsync(handler, startDeadline.Token);
            _loop = RunAsync(handler, lifetime);
        }
        catch (Exception error)
        {
            if (error is Microsoft.AspNetCore.SignalR.HubException &&
                error.Message.Contains(ErrorCodes.AuthorizationRevoked, StringComparison.Ordinal))
            {
                await LoseAuthorizationAsync();
            }
            await StopAsync();
            await _connection.DisposeAsync();
            _connection = null;
            _lifetime.Dispose();
            _lifetime = null;
            throw;
        }
    }

    private async Task RunAsync(Func<CommandDelivery, CancellationToken, Task> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_replaced)
        {
            try
            {
                await Task.Delay(IsConnected ? _catchUpInterval : TimeSpan.FromSeconds(5), ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                if (_connection!.State == HubConnectionState.Disconnected) { await _connection.StartAsync(deadline.Token); }
                if (_connection.State == HubConnectionState.Connected)
                {
                    await CatchUpAsync(handler, deadline.Token);
                    _connectionError = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Microsoft.AspNetCore.SignalR.HubException error) when
                (error.Message.Contains(ErrorCodes.AuthorizationRevoked, StringComparison.Ordinal))
            {
                await LoseAuthorizationAsync();
                return;
            }
            catch (Exception error) when (error is HttpRequestException or WebSocketException or IOException or
                                             InvalidOperationException or OperationCanceledException or TimeoutException or
                                             System.Text.Json.JsonException or Microsoft.AspNetCore.SignalR.HubException)
            {
                // Events remain unacknowledged on the server and are fetched again after recovery.
                _connectionError = "服务器指令连接暂不可用，正在重连；不能据此视为开考或交卷成功。";
            }
        }
    }

    private Task LoseAuthorizationAsync()
    {
        lock (_pendingGate)
        {
            if (_authorizationLoss is not null) { return _authorizationLoss; }
            if (_stopping) { return Task.CompletedTask; }
            _replaced = true;
            _authorizationLoss = Task.Run(async () =>
            {
                var cancellation = Task.CompletedTask;
                try
                {
                    // Cancel old network work before waiting for the manager's persistence gate.
                    // Do not wait for cancellation callbacks before invalidating visible content.
                    // Revocation itself must not use that now-cancelled token.
                    if (_lifetime is not null) { cancellation = _lifetime.CancelAsync(); }
                    if (AuthorizationLost is { } invalidate) { await invalidate(CancellationToken.None); }
                }
                catch (Exception)
                {
                    _processingError = "授权失效后的本地停用处理未完成，请联系考务人员。";
                }
                finally
                {
                    try { await cancellation; }
                    catch (Exception) { _processingError = "旧指令的取消处理未完成，请联系考务人员。"; }
                }
            });
            return _authorizationLoss;
        }
    }

    private async Task CatchUpAsync(Func<CommandDelivery, CancellationToken, Task> handler, CancellationToken ct)
    {
        var events = await _connection!.InvokeAsync<IReadOnlyList<CommandDelivery>>("CatchUp", ct)
            ?? throw new HttpRequestException("服务器未返回有效指令列表。");
        // Process the newest schedule first. Within one schedule END still precedes START;
        // an older END cannot freeze answers before a newer extension/reopening is applied.
        foreach (var delivery in events.OrderByDescending(delivery => delivery?.EventType is
                     CommandEventType.Start or CommandEventType.End or CommandEventType.ScheduleChanged
                     ? delivery.StateVersion : 0)
                 .ThenBy(delivery => delivery?.EventType == CommandEventType.End ? 0 : 1))
        {
            await QueueDelivery(delivery, handler, _lifetime!.Token).WaitAsync(ct);
        }
    }

    private Task QueueDelivery(CommandDelivery? delivery,
        Func<CommandDelivery, CancellationToken, Task> handler, CancellationToken ct)
    {
        if (delivery is null || delivery.Server is null || string.IsNullOrWhiteSpace(delivery.EventId) ||
            delivery.Server.ServerId != server.ServerId || delivery.Server.NodeId != server.NodeId ||
            delivery.Server.ServerEpoch != server.ServerEpoch || _participation is not { } participation ||
            delivery.ExamId != participation.ExamId || delivery.ParticipationId != participation.ParticipationId ||
            delivery.EntryVersion != participation.EntryVersion || delivery.TargetDeviceId != device.DeviceId ||
            (delivery.TargetAuthorizationVersion != participation.AuthorizationVersion &&
             !(delivery.EventType == CommandEventType.DeviceDecided &&
               delivery.TargetAuthorizationVersion > participation.AuthorizationVersion)))
        {
            return Task.CompletedTask;
        }

        lock (_pendingGate)
        {
            if (_replaced || _stopping || ct.IsCancellationRequested) { return Task.CompletedTask; }
            if (_pending.TryGetValue(delivery.EventId, out var existing)) { return existing.Task; }
            var isEnd = delivery.EventType == CommandEventType.End;
            if (isEnd ? _pendingEnd : _pendingCommands >= MaxPendingCommands)
            {
                // Deliberately leave overflow unacknowledged. Durable CatchUp retries it; no
                // unbounded task/queue growth is needed, and END has its own reserved slot.
                _processingError = "正在处理此前指令，未处理的服务器指令将继续重试。";
                return Task.CompletedTask;
            }

            if (isEnd) { _pendingEnd = true; } else { _pendingCommands++; }
            var task = Task.Run(() => ProcessDeliveryAsync(delivery, handler, isEnd, ct));
            _pending.Add(delivery.EventId, new PendingDelivery(task));
            return task;
        }
    }

    private async Task ProcessDeliveryAsync(CommandDelivery delivery,
        Func<CommandDelivery, CancellationToken, Task> handler, bool isEnd, CancellationToken ct)
    {
        var enteredGate = false;
        try
        {
            if (!isEnd)
            {
                await _deliveryGate.WaitAsync(ct);
                enteredGate = true;
            }
            if (_replaced || _stopping || ct.IsCancellationRequested) { return; }
            await handler(delivery, ct);
            _processingError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            // Every background task observes its failure. The event is not acknowledged by
            // this transport and remains eligible for durable retry after the cause is fixed.
            _processingError = "服务器指令尚未处理完成，将继续使用原事件重试。";
        }
        finally
        {
            if (enteredGate) { _deliveryGate.Release(); }
            lock (_pendingGate)
            {
                _pending.Remove(delivery.EventId);
                if (isEnd) { _pendingEnd = false; } else { _pendingCommands--; }
            }
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Task[] work;
        lock (_pendingGate)
        {
            _stopping = true;
            work = _pending.Values.Select(value => value.Task)
                .Concat(_authorizationLoss is null ? [] : new[] { _authorizationLoss })
                .ToArray();
        }
        if (_lifetime is not null) { await _lifetime.CancelAsync(); }
        var stopConnection = _connection?.StopAsync(ct) ?? Task.CompletedTask;
        await Task.WhenAll(work.Append(_loop ?? Task.CompletedTask).Append(stopConnection)).WaitAsync(ct);
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        await StopAsync();
        if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
        _lifetime?.Dispose();
        _socketHttp.Dispose();
        _deliveryGate.Dispose();
    }

    private sealed record PendingDelivery(Task Task);
}
