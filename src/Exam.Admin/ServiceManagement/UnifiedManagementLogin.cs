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
        if (_disposed) return false;
        if (!HasConnectionConfiguration) throw new InvalidOperationException("尚未配置服务器管理连接，请联系部署人员。");
        if (IsSignedIn && _connection?.Actor is { } actor && !string.Equals(actor, username, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("请先退出当前考务账号，再登录其他账号。");
        Username = username;
        var version = _managementSessionVersion;
        var connection = await ConnectTargetsAsync(CurrentTarget(),
            (client, token) => client.LoginAsync(username, password, token), username, _lifetime.Token);
        if (connection is null || _disposed || version != _managementSessionVersion) return false;
        SelectCurrentConnection(connection, ReadPendingRequest(connection.Target));
        IsSignedIn = true;
        Message = "考务服务暂不可用，已进入服务器管理";
        RecoveryLoginNotice = Message;
        _ = RefreshPeerStatusAsync(force: true);
        StartPolling();
        return true;
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
        if (connection.Target.NodeId != active)
        {
            var target = ConfiguredManagementTargets.FirstOrDefault(value => value.NodeId == active);
            if (target is null) return null;
            var activeConnection = await EnsureTargetSessionAsync(target, token);
            token.ThrowIfCancellationRequested();
            if (activeConnection is null || !Current(connection, generation) || !IsSignedIn ||
                CurrentActiveNodeId != active || _displayState?.ServerEpoch != state.ServerEpoch) return null;
            SelectCurrentConnection(activeConnection, ReadPendingRequest(target));
            connection = activeConnection;
            generation = _generation;
        }
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
