using System.Collections.ObjectModel;
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
    private bool _lastDegraded, _hostOff, _networkIsolated, _destructiveRebuild;
    private string _haReason = "", _haTarget = "", _protection = "主备保护状态未核实；配置勾选不表示同步保护已经生效。";
    public string ProtectionText { get => _protection; private set { if (SetProperty(ref _protection, value)) OnPropertyChanged(nameof(ShowProtectionWarning)); } }
    public bool ShowProtectionWarning => _lastDegraded || _availability?.State is { ProtectionMode: not ProtectionMode.SynchronousDualNode };
    public string HighAvailabilityVersionText => _availability is { State: { } state, VerifiedAt: { } time }
        ? $"活动节点：{state.ActiveNodeId}；代次 {state.ServerEpoch}；状态版本 {state.Version}；核实于 {time.ToLocalTime():HH:mm:ss}"
        : "当前活动节点、代次和状态版本未核实";
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

    private bool CanIssueHighAvailability(ManagementOperationKind kind) =>
        kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect) ||
        _availability is { State: not null, VerifiedAt: { } time } && DateTimeOffset.UtcNow - time <= TimeSpan.FromSeconds(10);

    private HighAvailabilityRequest BuildHighAvailabilityRequest(ManagementOperationKind kind, ManagementTarget target, string actor, string id)
    {
        if (_availability is not { State: { } state, VerifiedAt: { } time } ||
            DateTimeOffset.UtcNow - time > TimeSpan.FromSeconds(10) || time > DateTimeOffset.UtcNow.AddSeconds(5))
            throw new ArgumentException("先重新查询当前主备状态与代次，再确认操作。");
        if (string.IsNullOrWhiteSpace(HighAvailabilityReason) || HighAvailabilityReason.Length > 500 ||
            string.IsNullOrWhiteSpace(HighAvailabilityTarget) || !state.Nodes.ContainsKey(HighAvailabilityTarget.Trim()))
            throw new ArgumentException("请输入本次操作的已配置目标节点与原因。");
        var isolation = kind == ManagementOperationKind.Takeover
            ? new IsolationEvidence(state.ActiveNodeId, OldPrimaryPoweredOff, OldPrimaryNetworkIsolated, actor, HighAvailabilityReason.Trim()) : null;
        if (kind == ManagementOperationKind.Takeover && isolation is not { IsSufficient: true })
            throw new ArgumentException("必须确认原主机断电或业务网络隔离；心跳丢失不足以接管。");
        if (kind == ManagementOperationKind.Rebuild && !ConfirmDestructiveRebuild)
            throw new ArgumentException("须先确认重建本机旧数据并保持隔离。");
        return new(id, target.ServerId, target.NodeId, actor, state.ServerEpoch, state.Version,
            HighAvailabilityTarget.Trim(), HighAvailabilityReason.Trim(), isolation, ConfirmDestructiveRebuild);
    }

    public void ApplyHighAvailability(HighAvailabilityStatus status)
    {
        var target = _connection?.Target;
        if (target is null || status.ServerId != target.ServerId || status.NodeId != target.NodeId || status.Checks is null ||
            status.State is { } s && (s.ServerId != target.ServerId || s.ServerEpoch <= 0 || s.Version <= 0 || s.Nodes.Count != 2 ||
                !Enum.IsDefined(s.ProtectionMode) || !s.Nodes.ContainsKey(s.ActiveNodeId) ||
                s.Nodes.Any(n => n.Key != n.Value.NodeId || !Enum.IsDefined(n.Value.Role))) ||
            status.SynchronousProtectionVerified && status.State?.ProtectionMode != ProtectionMode.SynchronousDualNode)
            throw new InvalidDataException("主备响应身份、状态或保护证据无效。");
        if (status.SynchronousProtectionVerified && (status.State!.Nodes.Values.Any(n => !n.Reachable || !n.KeyMaterialReady || !n.CertificateReady ||
                n.Role is not (DeploymentNodeRole.Primary or DeploymentNodeRole.Standby) ||
                n.Role == DeploymentNodeRole.Primary && n.DatabaseInRecovery || n.Role == DeploymentNodeRole.Standby && (!n.DatabaseInRecovery || n.ReplayLagBytes != 0) ||
                DateTimeOffset.UtcNow - n.ObservedAt > TimeSpan.FromSeconds(10) || n.ObservedAt > DateTimeOffset.UtcNow.AddSeconds(5)) ||
            status.Checks.Any(c => !c.Passed && c.BlocksWrites) ||
            !status.Checks.Any(c => c.Name == "failover-state" && c.Passed && c.BlocksWrites) ||
            !status.Checks.Any(c => c.Name == "synchronous-protection" && c.Passed && c.BlocksWrites)))
            throw new InvalidDataException("同步保护缺少实际节点及双侧确认检查证据。");
        if (status.VerifiedAt is not { } time || status.State is not { } state ||
            DateTimeOffset.UtcNow - time > TimeSpan.FromSeconds(10) || time > DateTimeOffset.UtcNow.AddSeconds(5))
        { MarkProtectionUnknown(); return; }
        if (_availability?.State is { } prior && (state.ServerEpoch < prior.ServerEpoch ||
            state.ServerEpoch == prior.ServerEpoch && state.Version < prior.Version))
        { MarkProtectionUnknown(); return; }
        _availability = status;
        if (state.ProtectionMode is ProtectionMode.EmergencySingleNode or ProtectionMode.Recovering) _lastDegraded = true;
        else if (status.SynchronousProtectionVerified) _lastDegraded = false;
        _protectionMemory?.Save(target, state.ServerEpoch, state.Version, time, _lastDegraded);
        ProtectionText = state.ProtectionMode switch {
            ProtectionMode.EmergencySingleNode => "单机应急：当前仅有单机持久确认，双机保护已降低。请尽快重建备用节点。",
            ProtectionMode.Recovering => "恢复中：旧节点保持隔离，尚未完成双侧持久确认，不能判定同步保护已恢复。",
            _ when status.SynchronousProtectionVerified => "同步双机保护已核实：实际复制、材料及双侧最新持久确认均通过。",
            _ => _lastDegraded ? "此前保护已降低；当前同步保护仍未核实，不能据在线或延迟为零解除警示。" : "当前同步保护未核实；节点在线或延迟为零不能替代双侧确认。" };
        EpochText = $"已核实代次：{state.ServerEpoch}";
        if (string.IsNullOrWhiteSpace(HighAvailabilityTarget)) HighAvailabilityTarget = target.NodeId;
        Nodes.Clear();
        foreach (var node in state.Nodes.Values.OrderBy(n => n.NodeId, StringComparer.Ordinal))
        {
            var fresh = DateTimeOffset.UtcNow - node.ObservedAt <= TimeSpan.FromSeconds(10) && node.ObservedAt <= DateTimeOffset.UtcNow.AddSeconds(5);
            Nodes.Add(new(node.NodeId, node.Role switch { DeploymentNodeRole.Primary => "主节点", DeploymentNodeRole.Standby => "备用", DeploymentNodeRole.Isolated => "隔离", _ => "重建" },
                fresh ? node.Reachable ? "可连接" : "不可连接" : "未核实", fresh && node.Reachable ? node.DatabaseInRecovery ? "备库" : "主库" : "未核实",
                fresh && node.Reachable && node.ReplayLagBytes != long.MaxValue ? node.ReplayLagBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "未核实",
                fresh && node.KeyMaterialReady ? "已核实" : "未核实", fresh && node.CertificateReady ? "已核实" : "未核实", fresh ? node.ObservedAt.ToLocalTime().ToString("HH:mm:ss") : "已过期"));
        }
        OnPropertyChanged(nameof(HighAvailabilityVersionText)); OnPropertyChanged(nameof(ShowProtectionWarning)); UpdateCommands();
    }
    private void MarkProtectionUnknown()
    {
        _availability = null; Nodes.Clear();
        ProtectionText = _lastDegraded ? "此前已进入单机应急或恢复中；当前保护未核实，警示保留。" : "主备保护状态未核实；配置勾选不表示同步保护已经生效。";
        OnPropertyChanged(nameof(HighAvailabilityVersionText)); OnPropertyChanged(nameof(ShowProtectionWarning));
    }
    private void LoadProtectionWarning(ManagementTarget? target = null)
    {
        try
        {
            target ??= new(Address, ServerId, NodeId);
            if (string.IsNullOrWhiteSpace(Address)) return;
            _lastDegraded = _protectionMemory?.Load(target)?.Degraded ?? false; MarkProtectionUnknown();
        }
        catch { _lastDegraded = true; MarkProtectionUnknown(); }
    }
}
