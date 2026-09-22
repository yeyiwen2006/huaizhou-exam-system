using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.SignalR;

namespace Exam.Server.Realtime;

public sealed record RealtimeConnectRequest(
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    string ServerId,
    long ServerEpoch);

public sealed record RealtimeDeviceConnection(string ConnectionId, RealtimeConnectRequest Identity);

public sealed record RealtimeDispatchOptions(int MaxConnections, int MaxConcurrentDispatches);

public sealed class RealtimeDeviceAuthorizer(
    IDeviceCredentialValidator credentials,
    IParticipationAccessReader accessReader,
    ServerNodeEnvelope server,
    ActiveNodeGuard? activeNode = null)
{
    public async ValueTask<bool> AuthorizeAsync(
        RealtimeConnectRequest request,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (activeNode is not null && !(await activeNode.CheckAsync(cancellationToken)).Allowed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(credential) ||
            !string.Equals(request.ServerId, server.ServerId, StringComparison.Ordinal) ||
            request.ServerEpoch != server.ServerEpoch ||
            request.EntryVersion < 1 ||
            request.AuthorizationVersion < 1 ||
            !await credentials.ValidateActiveDeviceAsync(
                request.DeviceId,
                credential,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var access = await accessReader.FindAsync(request.ParticipationId, cancellationToken)
            .ConfigureAwait(false);
        return access is not null &&
               access.EntryVersion == request.EntryVersion &&
               string.Equals(access.AuthorizedDeviceId, request.DeviceId, StringComparison.Ordinal) &&
               access.AuthorizationVersion == request.AuthorizationVersion;
    }

    public async ValueTask<bool> IsStillAuthorizedAsync(
        RealtimeConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (activeNode is not null && !(await activeNode.CheckAsync(cancellationToken)).Allowed)
        {
            return false;
        }

        if (!string.Equals(request.ServerId, server.ServerId, StringComparison.Ordinal) ||
            request.ServerEpoch != server.ServerEpoch ||
            !await credentials.IsActiveDeviceAsync(request.DeviceId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var access = await accessReader.FindAsync(request.ParticipationId, cancellationToken)
            .ConfigureAwait(false);
        return access is not null &&
               access.EntryVersion == request.EntryVersion &&
               string.Equals(access.AuthorizedDeviceId, request.DeviceId, StringComparison.Ordinal) &&
               access.AuthorizationVersion == request.AuthorizationVersion;
    }
}

public sealed class RealtimeConnectionRegistry(RealtimeDispatchOptions options)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RealtimeDeviceConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _connectionByIdentity = new(StringComparer.Ordinal);

    public bool TryAdd(RealtimeDeviceConnection connection, out string? replacedConnectionId)
    {
        lock (_gate)
        {
            replacedConnectionId = null;
            var key = IdentityKey(connection.Identity);
            if (_connections.Count >= options.MaxConnections && !_connectionByIdentity.ContainsKey(key))
            {
                return false;
            }

            if (_connectionByIdentity.TryGetValue(key, out var previous) &&
                !string.Equals(previous, connection.ConnectionId, StringComparison.Ordinal))
            {
                replacedConnectionId = previous;
                _connections.Remove(previous);
            }

            _connectionByIdentity[key] = connection.ConnectionId;
            _connections[connection.ConnectionId] = connection;
            return true;
        }
    }

    public bool TryGet(string connectionId, out RealtimeDeviceConnection? connection) =>
        TryGetCore(connectionId, out connection);

    public void Remove(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out var removed))
            {
                return;
            }

            var key = IdentityKey(removed.Identity);
            if (_connectionByIdentity.TryGetValue(key, out var current) &&
                string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                _connectionByIdentity.Remove(key);
            }
        }
    }

    public IReadOnlyList<RealtimeDeviceConnection> Snapshot()
    {
        lock (_gate)
        {
            return _connections.Values.ToArray();
        }
    }

    private bool TryGetCore(string connectionId, out RealtimeDeviceConnection? connection)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(connectionId, out connection);
        }
    }

    private static string IdentityKey(RealtimeConnectRequest identity) =>
        $"{identity.ParticipationId.Length}:{identity.ParticipationId}|{identity.EntryVersion}|" +
        $"{identity.DeviceId.Length}:{identity.DeviceId}|{identity.AuthorizationVersion}";
}

public sealed class ExamEventHub(
    RealtimeDeviceAuthorizer authorizer,
    RealtimeConnectionRegistry connections,
    ReliableCommandService commands) : Hub
{
    private const string CredentialHeader = "X-Exam-Device-Credential";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext() ?? throw new HubException("无法读取实时连接上下文。");
        var request = ParseRequest(http.Request.Query);
        var credential = http.Request.Headers[CredentialHeader].ToString();
        if (request is null ||
            !await authorizer.AuthorizeAsync(request, credential, Context.ConnectionAborted))
        {
            Context.Abort();
            throw new HubException("实时连接的设备授权或服务代次无效。");
        }

        var connection = new RealtimeDeviceConnection(Context.ConnectionId, request);
        if (!connections.TryAdd(connection, out var replacedConnectionId))
        {
            Context.Abort();
            throw new HubException("实时连接数已达服务器上限。");
        }

        if (replacedConnectionId is not null)
        {
            await Clients.Client(replacedConnectionId).SendAsync(
                "ConnectionReplaced",
                "同一参试授权已建立新连接。",
                Context.ConnectionAborted);
        }
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(request.ParticipationId),
            Context.ConnectionAborted);
        await SendDueAsync(connection, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<IReadOnlyList<CommandDelivery>> CatchUp()
    {
        var connection = RequireConnection();
        await RequireStillAuthorizedAsync(connection.Identity);
        var result = await commands.GetDueForDeviceAsync(
            connection.Identity.ParticipationId,
            connection.Identity.EntryVersion,
            connection.Identity.DeviceId,
            connection.Identity.AuthorizationVersion,
            connection.Identity.ServerId,
            connection.Identity.ServerEpoch,
            Context.ConnectionAborted);
        if (!result.IsSuccess)
        {
            throw new HubException($"{result.ErrorCode}: {result.Message}");
        }

        return result.Value ?? [];
    }

    public async Task<CommandOperationResult<CommandAcknowledgement>> Acknowledge(
        AcknowledgeCommandRequest request)
    {
        var connection = RequireConnection().Identity;
        await RequireStillAuthorizedAsync(connection);
        if (!string.Equals(request.ParticipationId, connection.ParticipationId, StringComparison.Ordinal) ||
            request.EntryVersion != connection.EntryVersion ||
            !string.Equals(request.DeviceId, connection.DeviceId, StringComparison.Ordinal) ||
            request.AuthorizationVersion != connection.AuthorizationVersion)
        {
            throw new HubException("确认请求与当前实时连接的设备授权不一致。");
        }

        return await commands.AcknowledgeAsync(request, Context.ConnectionAborted);
    }

    private async Task SendDueAsync(
        RealtimeDeviceConnection connection,
        CancellationToken cancellationToken)
    {
        var identity = connection.Identity;
        var result = await commands.GetDueForDeviceAsync(
            identity.ParticipationId,
            identity.EntryVersion,
            identity.DeviceId,
            identity.AuthorizationVersion,
            identity.ServerId,
            identity.ServerEpoch,
            cancellationToken);
        if (!result.IsSuccess)
        {
            throw new HubException($"{result.ErrorCode}: {result.Message}");
        }

        foreach (var delivery in result.Value ?? [])
        {
            await Clients.Caller.SendAsync("Command", delivery, cancellationToken);
        }
    }

    private RealtimeDeviceConnection RequireConnection()
    {
        if (!connections.TryGet(Context.ConnectionId, out var connection) || connection is null)
        {
            throw new HubException(ErrorCodes.AuthorizationRevoked);
        }

        return connection;
    }

    private async Task RequireStillAuthorizedAsync(RealtimeConnectRequest identity)
    {
        if (!await authorizer.IsStillAuthorizedAsync(identity, Context.ConnectionAborted))
        {
            connections.Remove(Context.ConnectionId);
            // Keep the transport alive long enough for the stable error code to reach
            // the caller. The removed registration cannot obtain or acknowledge events.
            throw new HubException(ErrorCodes.AuthorizationRevoked);
        }
    }

    private static RealtimeConnectRequest? ParseRequest(IQueryCollection query) =>
        long.TryParse(query["entryVersion"], out var entryVersion) &&
        long.TryParse(query["authorizationVersion"], out var authorizationVersion) &&
        long.TryParse(query["serverEpoch"], out var serverEpoch) &&
        Required(query, "participationId") is { } participationId &&
        Required(query, "deviceId") is { } deviceId &&
        Required(query, "serverId") is { } serverId
            ? new RealtimeConnectRequest(
                participationId,
                entryVersion,
                deviceId,
                authorizationVersion,
                serverId,
                serverEpoch)
            : null;

    private static string? Required(IQueryCollection query, string name)
    {
        var value = query[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static string GroupName(string participationId) => $"participation:{participationId}";
}
