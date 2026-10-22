using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Common;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    private string _recoveryLoginNotice = string.Empty;
    public string RecoveryLoginNotice { get => _recoveryLoginNotice; private set => SetProperty(ref _recoveryLoginNotice, value); }
    public void ClearRecoveryLoginNotice() => RecoveryLoginNotice = string.Empty;
    private ManagementSessionSnapshot? ConfirmedManagementIdentity() => _connection?.CaptureManagementSession() ??
        _nodeSessions.Values.Select(node => node.Connection?.CaptureManagementSession()).FirstOrDefault(session => session is not null);

    public async Task<bool> LoginForRecoveryAsync(string businessAddress, string username, string password)
    {
        if (!HasConnectionConfiguration) throw new InvalidOperationException("尚未配置服务器管理连接，请联系部署人员。");
        if (IsSignedIn && _connection?.Actor is { } actor && !string.Equals(actor, username, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("请先退出当前考务账号，再登录其他账号。");
        Username = username;
        var host = new Uri(businessAddress).Host;
        Exception? last = null;
        foreach (var target in ConfiguredManagementTargets.OrderByDescending(target => new Uri(target.Address).Host == host))
        {
            if (ReadPendingRequest(target) is { } pending) _knownPendingRequests[target.Key] = pending;
            var previous = NodeSession(target).Connection;
            var connection = await AuthenticateNodeAsync(NodeSession(target), (client, token) => client.LoginAsync(username, password, token),
                username, _managementSessionVersion, _lifetime.Token);
            if (connection is null)
            {
                last = NodeSession(target).AuthenticationError;
                if (last is UnauthorizedAccessException) throw last;
                continue;
            }
            SelectCurrentConnection(connection, ReadPendingRequest(target));
            if (previous is not null && !ReferenceEquals(previous, connection)) previous.Dispose();
            IsSignedIn = true;
            Message = "考务服务暂不可用，已进入服务器管理";
            RecoveryLoginNotice = Message;
            _ = RefreshPeerStatusAsync(force: true);
            StartPolling();
            return true;
        }
        throw last ?? new System.Net.Http.HttpRequestException("考务服务和服务器管理均暂不可用。");
    }

    public async Task<ManagementBusinessSession?> CreateBusinessSessionAsync(CancellationToken token)
    {
        if (!IsSignedIn || _connection is not { Actor: not null } connection) return null;
        var generation = _generation;
        if (CurrentActiveNodeId is null || _displayState is null)
        {
            var observation = await connection.HighAvailabilityAsync(token);
            token.ThrowIfCancellationRequested();
            if (!Current(connection, generation) || !IsSignedIn) return null;
            ApplyHighAvailabilityReply(observation);
        }
        if (CurrentActiveNodeId is not { } active || _displayState is not { } state) return null;
        var reply = await connection.ExchangeBusinessSessionAsync(active, state.ServerEpoch, token);
        token.ThrowIfCancellationRequested();
        if (!Current(connection, generation) || !IsSignedIn) return null;
        if (reply.Envelope.Error?.Code is ErrorCodes.StaleServerEpoch or ErrorCodes.NotActiveServer)
        {
            var observation = await connection.HighAvailabilityAsync(token);
            token.ThrowIfCancellationRequested();
            if (!Current(connection, generation) || !IsSignedIn) return null;
            ApplyHighAvailabilityReply(observation);
        }
        return reply.Envelope.Error is null ? reply.Envelope.Result : null;
    }
}
