using System.Collections.ObjectModel;
using System.Net;
using Exam.Admin.Infrastructure;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed record ManagementNodeRow(string NodeId, string Role, string Reachability, string Recovery,
    string ReplayLag, string Keys, string Certificate, string ObservedAt);

public sealed partial class ServiceManagementViewModel
{
    private readonly IProtectionWarningStore? _protectionMemory;
    private HighAvailabilityStatus? _availability;
    private ProtectionWarningMemory? _protectionEvidence;
    private CancellationTokenSource? _protectionExpiry;
    private CancellationTokenSource? _availabilityPolling;
    private bool _lastDegraded, _hostOff, _networkIsolated, _destructiveRebuild;
    private string _haReason = "", _haTarget = "", _protection = "主备保护状态尚未核实；连接服务器管理后可查询。";
    public string ProtectionText { get => _protection; private set { if (SetProperty(ref _protection, value)) OnPropertyChanged(nameof(ShowProtectionWarning)); } }
    public bool ShowProtectionWarning => (IsSignedIn || _protectionEvidence is not null || _lastDegraded) &&
        (_availability is null || !_availability.SynchronousProtectionVerified);
    public string HighAvailabilityVersionText => _availability is { State: { } state, VerifiedAt: { } time }
        ? $"活动节点：{state.ActiveNodeId}；核实于 {time.ToOffset(TimeSpan.FromHours(8)):HH:mm:ss}"
        : "当前活动节点未核实";
    public string HighAvailabilityReason { get => _haReason; set => SetProperty(ref _haReason, value); }
    public string HighAvailabilityTarget { get => _haTarget; set => SetProperty(ref _haTarget, value); }
    public bool OldPrimaryPoweredOff { get => _hostOff; set => SetProperty(ref _hostOff, value); }
    public bool OldPrimaryNetworkIsolated { get => _networkIsolated; set => SetProperty(ref _networkIsolated, value); }
    public bool ConfirmDestructiveRebuild { get => _destructiveRebuild; set => SetProperty(ref _destructiveRebuild, value); }
    public ObservableCollection<ManagementNodeRow> Nodes { get; } = [];
    public AsyncRelayCommand TakeoverCommand { get; }
    public AsyncRelayCommand EmergencyCommand { get; }
    public AsyncRelayCommand RebuildCommand { get; }
    public AsyncRelayCommand ReprotectCommand { get; }

    private void StartHighAvailabilityPolling()
    {
        if (!_active || !IsSignedIn || _availabilityPolling is { IsCancellationRequested: false }) return;
        var polling = _availabilityPolling = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = PollHighAvailabilityAsync(polling);
    }

    private async Task PollHighAvailabilityAsync(CancellationTokenSource polling)
    {
        try
        {
            while (_active && IsSignedIn && !polling.IsCancellationRequested)
            {
                // Refresh before the ten-second evidence expiry, independently of pending operations.
                await Task.Delay(TimeSpan.FromSeconds(3), _time, polling.Token);
                if (polling.IsCancellationRequested || !_active || !IsSignedIn || _readBusy || IsBusy || _connection is null) continue;
                var connection = _connection; var generation = _generation;
                using var reads = BeginRead();
                try
                {
                    var reply = await connection.HighAvailabilityAsync(reads.Token);
                    if (polling.IsCancellationRequested || reads.IsCancellationRequested || !Accept(connection, generation, reply.IsUnauthorized)) continue;
                    ApplyHighAvailabilityReply(reply);
                }
                catch
                {
                    // A failed observation must not leave verified controls enabled; retry on the next tick.
                    if (!polling.IsCancellationRequested && Current(connection, generation)) MarkProtectionUnknown();
                }
                finally { EndRead(reads); }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_availabilityPolling, polling)) _availabilityPolling = null; polling.Dispose(); }
    }

    private bool CanIssueHighAvailability(ManagementOperationKind kind) =>
        kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect) ||
        _availability is { State: not null, VerifiedAt: { } time } && _time.GetUtcNow() - time < TimeSpan.FromSeconds(10) &&
        time <= _time.GetUtcNow().AddSeconds(5) &&
        (kind != ManagementOperationKind.Takeover ||
            _availability.State.Nodes.TryGetValue(NodeId.Trim(), out var local) && local.Role == DeploymentNodeRole.Standby &&
            local.DatabaseInRecovery && _availability.State.ActiveNodeId != NodeId.Trim());

    private HighAvailabilityRequest BuildHighAvailabilityRequest(ManagementOperationKind kind, ManagementTarget target, string actor, string id)
    {
        if (_availability is not { State: { } state, VerifiedAt: { } time } ||
            _time.GetUtcNow() - time >= TimeSpan.FromSeconds(10) || time > _time.GetUtcNow().AddSeconds(5))
            throw new ArgumentException("先重新查询当前主备状态，再确认操作。");
        if (string.IsNullOrWhiteSpace(HighAvailabilityReason) || HighAvailabilityReason.Length > 500 ||
            string.IsNullOrWhiteSpace(HighAvailabilityTarget) || !state.Nodes.ContainsKey(HighAvailabilityTarget.Trim()))
            throw new ArgumentException("请输入本次操作的已配置目标节点与原因。");
        var isolation = kind == ManagementOperationKind.Takeover
            ? new IsolationEvidence(state.ActiveNodeId, OldPrimaryPoweredOff, OldPrimaryNetworkIsolated, actor, HighAvailabilityReason.Trim()) : null;
        if (kind == ManagementOperationKind.Takeover && isolation is not { IsSufficient: true })
            throw new ArgumentException("必须确认原主机断电或业务网络隔离；心跳丢失不足以接管。");
        if (kind == ManagementOperationKind.Rebuild && !ConfirmDestructiveRebuild)
            throw new ArgumentException("须先确认重建目标服务器旧数据并保持隔离。");
        return new(id, target.ServerId, target.NodeId, actor, state.ServerEpoch, state.Version,
            HighAvailabilityTarget.Trim(), HighAvailabilityReason.Trim(), isolation, ConfirmDestructiveRebuild);
    }

    private void ApplyHighAvailabilityReply(ManagementReply<HighAvailabilityStatus> reply)
    {
        if (reply.Envelope.Error is null && reply.Envelope.Result is { } status)
        { ApplyHighAvailability(status); return; }
        MarkProtectionUnknown();
        if (reply.StatusCode == HttpStatusCode.NotImplemented && reply.Envelope.Error?.Code == "PLATFORM_NOT_SUPPORTED")
            ProtectionText = _lastDegraded
                ? "此前处于单节点运行；当前管理服务不支持主备状态查询和切换。"
                : "当前管理服务不支持主备状态查询和切换。";
    }

    public void ApplyHighAvailability(HighAvailabilityStatus status)
    {
        var target = _connection?.Target;
        if (target is null || status.ServerId != target.ServerId || status.NodeId != target.NodeId || status.Checks is null ||
            status.State is { } s && (s.ServerId != target.ServerId || s.ServerEpoch <= 0 || s.Version <= 0 || s.Nodes.Count is < 1 or > 2 ||
                !Enum.IsDefined(s.ProtectionMode) || !s.Nodes.ContainsKey(s.ActiveNodeId) ||
                s.Nodes.Any(n => n.Key != n.Value.NodeId || !Enum.IsDefined(n.Value.Role))) ||
            status.SynchronousProtectionVerified && status.State?.Nodes.Count != 2)
            throw new InvalidDataException("主备响应身份、状态或保护证据无效。");
        if (status.SynchronousProtectionVerified && (status.State!.Nodes.Values.Any(n => !n.Reachable || !n.KeyMaterialReady || !n.CertificateReady ||
                n.Role is not (DeploymentNodeRole.Primary or DeploymentNodeRole.Standby) ||
                n.Role == DeploymentNodeRole.Primary && n.DatabaseInRecovery || n.Role == DeploymentNodeRole.Standby && (!n.DatabaseInRecovery || n.ReplayLagBytes != 0) ||
                _time.GetUtcNow() - n.ObservedAt >= TimeSpan.FromSeconds(10) || n.ObservedAt > _time.GetUtcNow().AddSeconds(5)) ||
            status.Checks.Any(c => !c.Passed && c.BlocksWrites) ||
            !status.Checks.Any(c => c.Name == "failover-state" && c.Passed && c.BlocksWrites) ||
            !status.Checks.Any(c => c.Name == "synchronous-protection" && c.Passed)))
            throw new InvalidDataException("同步保护缺少实际节点及双侧确认检查证据。");
        if (status.VerifiedAt is not { } time || status.State is not { } state ||
            _time.GetUtcNow() - time >= TimeSpan.FromSeconds(10) || time > _time.GetUtcNow().AddSeconds(5))
        { MarkProtectionUnknown(); return; }
        var persisted = _protectionMemory?.Load(target);
        if (persisted is not null && (_protectionEvidence is null || OlderThan(_protectionEvidence.Epoch,
                _protectionEvidence.Version, _protectionEvidence.ObservedAt, persisted)))
        { _protectionEvidence = persisted; _lastDegraded = persisted.Degraded; }
        if (_protectionEvidence is { } prior && OlderThan(state.ServerEpoch, state.Version, time, prior))
        { MarkProtectionUnknown(); return; }
        _availability = status;
        _lastDegraded = !status.SynchronousProtectionVerified;
        _protectionMemory?.Save(target, state.ServerEpoch, state.Version, time, _lastDegraded);
        _protectionEvidence = new(target.Key, state.ServerEpoch, state.Version, time, _lastDegraded);
        ProtectionText = status.SynchronousProtectionVerified
            ? "双节点保护正常：数据库、试题资源与最新持久确认已同步。"
            : state.ActiveNodeId == target.NodeId
                ? "单节点运行：本机可靠保存后即可继续业务，复制恢复后自动补齐；当前新数据可能只有一份。"
                : "当前为备用节点。原主故障时，请确认原主已断电或隔离全部业务网络，再点击“接管为主服务器”。";
        if (string.IsNullOrWhiteSpace(HighAvailabilityTarget)) HighAvailabilityTarget = target.NodeId;
        Nodes.Clear();
        foreach (var node in state.Nodes.Values.OrderBy(n => n.NodeId, StringComparer.Ordinal))
        {
            var fresh = _time.GetUtcNow() - node.ObservedAt < TimeSpan.FromSeconds(10) && node.ObservedAt <= _time.GetUtcNow().AddSeconds(5);
            Nodes.Add(new(node.NodeId, node.Role switch { DeploymentNodeRole.Primary => "主节点", DeploymentNodeRole.Standby => "备用", DeploymentNodeRole.Isolated => "隔离", _ => "重建" },
                fresh ? node.Reachable ? "可连接" : "不可连接" : "未核实", fresh && node.Reachable ? node.DatabaseInRecovery ? "备库" : "主库" : "未核实",
                fresh && node.Reachable && node.ReplayLagBytes != long.MaxValue ? node.ReplayLagBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "未核实",
                fresh && node.KeyMaterialReady ? "已核实" : "未核实", fresh && node.CertificateReady ? "已核实" : "未核实", fresh ? node.ObservedAt.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss") : "已过期"));
        }
        OnPropertyChanged(nameof(HighAvailabilityVersionText)); OnPropertyChanged(nameof(ShowProtectionWarning)); UpdateCommands();
        _protectionExpiry?.Cancel();
        _ = ExpireProtectionAsync(status, _protectionExpiry = new CancellationTokenSource());
    }
    private static bool OlderThan(long epoch, long version, DateTimeOffset observed, ProtectionWarningMemory prior) =>
        epoch < prior.Epoch || epoch == prior.Epoch && (version < prior.Version || version == prior.Version && observed < prior.ObservedAt);

    private async Task ExpireProtectionAsync(HighAvailabilityStatus expected, CancellationTokenSource expiry)
    {
        try
        {
            var observed = expected.VerifiedAt!.Value;
            if (expected.SynchronousProtectionVerified)
                observed = expected.State!.Nodes.Values.Select(n => n.ObservedAt).Append(observed).Min();
            var remaining = observed.AddSeconds(10) - _time.GetUtcNow();
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _time, expiry.Token);
            if (!expiry.IsCancellationRequested && ReferenceEquals(_availability, expected)) MarkProtectionUnknown();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_protectionExpiry, expiry)) _protectionExpiry = null; expiry.Dispose(); }
    }
    private void MarkProtectionUnknown()
    {
        _protectionExpiry?.Cancel();
        _availability = null; Nodes.Clear();
        ProtectionText = _lastDegraded ? "此前处于单节点运行；当前主备保护状态未核实，已保存的数据和原请求继续保留。" : "当前主备保护状态未核实，已保存的数据和原请求继续保留。";
        OnPropertyChanged(nameof(HighAvailabilityVersionText)); OnPropertyChanged(nameof(ShowProtectionWarning));
        UpdateCommands();
    }
    private void LoadProtectionWarning(ManagementTarget? target = null)
    {
        try
        {
            target ??= new(Address, ServerId, NodeId);
            if (string.IsNullOrWhiteSpace(Address)) return;
            _protectionEvidence = _protectionMemory?.Load(target);
            _lastDegraded = _protectionEvidence?.Degraded ?? false; MarkProtectionUnknown();
        }
        catch { _lastDegraded = true; MarkProtectionUnknown(); }
    }
}
