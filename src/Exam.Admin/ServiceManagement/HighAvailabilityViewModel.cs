using System.Collections.ObjectModel;
using System.Net;
using Exam.Admin.Infrastructure;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed record ManagementNodeRow(string NodeId, string Role, string Reachability, string Recovery,
    string ReplayLag, string Keys, string Certificate, string ObservedAt, bool IsPlaceholder = false, bool IsCurrent = false);

public sealed partial class ServiceManagementViewModel
{
    private readonly IProtectionWarningStore? _protectionMemory;
    private HighAvailabilityStatus? _availability;
    private ServerDeploymentState? _displayState;
    private string _displayServerId = string.Empty;
    private string _displayTargetKey = string.Empty;
    private string _observedEpochServerId = string.Empty;
    private long _highestObservedServerEpoch;
    private DateTimeOffset? _displayVerifiedAt;
    private ManagementNodeRow? _selectedNode;
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
        : _displayState is { } previous ? $"当前活动节点未核实；上次观察的活动节点：{previous.ActiveNodeId}" : "当前活动节点未核实";
    public string HighAvailabilityReason { get => _haReason; set => SetProperty(ref _haReason, value); }
    public string HighAvailabilityTarget { get => _haTarget; set { if (SetProperty(ref _haTarget, value)) UpdateCommands(); } }
    public ManagementNodeRow? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value)) return;
            HighAvailabilityTarget = value is { IsPlaceholder: false } && _displayState?.Nodes.ContainsKey(value.NodeId) == true
                ? value.NodeId : string.Empty;
            UpdateCommands();
        }
    }
    public string PrimaryServerStatusText => NodeStatusText(primary: true);
    public string StandbyServerStatusText => NodeStatusText(primary: false);
    public string ManagedServersText
    {
        get
        {
            var primary = _displayState?.Nodes.Values.FirstOrDefault(n => n.NodeId == _displayState.ActiveNodeId);
            var standby = _displayState?.Nodes.Values.FirstOrDefault(n => n.NodeId != _displayState.ActiveNodeId);
            var configured = string.Join("；", ConfiguredManagementTargets.Select(t => $"{t.NodeId} · {t.Address}"));
            return $"当前管理服务器：主机 {primary?.NodeId ?? "待核实"}；备机 {standby?.NodeId ?? "待核实"}" +
                (configured.Length == 0 ? "" : $"\n已配置：{configured}");
        }
    }
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
        foreach (var target in ConfiguredManagementTargets)
            _ = PollLocalNodeHealthAsync(target, _managementSessionVersion, polling.Token);
        _ = PollHighAvailabilityAsync(polling);
    }

    private async Task PollHighAvailabilityAsync(CancellationTokenSource polling)
    {
        try
        {
            while (_active && IsSignedIn && !polling.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _time, polling.Token);
                if (polling.IsCancellationRequested || !_active || !IsSignedIn || _connection is null) continue;
                var connection = _connection; var generation = _generation;
                // A slow log or operation query must not starve the independent HA observation.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(polling.Token, _lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                _ = RefreshPeerStatusAsync();
                try { await ObserveHighAvailabilityAsync(connection, generation, deadline.Token); }
                catch (OperationCanceledException) when (polling.IsCancellationRequested) { break; }
                catch { /* The failed observation invalidates only its own proof and retains the node rows. */ }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_availabilityPolling, polling)) _availabilityPolling = null; polling.Dispose(); }
    }

    private async Task ObserveHighAvailabilityAsync(IManagementConnection connection, long generation, CancellationToken token)
    {
        var expected = _availability;
        try
        {
            var reply = await connection.HighAvailabilityAsync(token);
            if (token.IsCancellationRequested || !Accept(connection, generation, reply.IsUnauthorized)) return;
            // A late failed request cannot invalidate a newer successful observation.
            if (reply.Envelope.Error is not null && !ReferenceEquals(expected, _availability)) return;
            if (reply.Envelope.Result is { State: null } && !ReferenceEquals(expected, _availability)) return;
            ApplyHighAvailabilityReply(reply);
        }
        catch
        {
            if (Current(connection, generation) && ReferenceEquals(expected, _availability)) MarkProtectionUnknown();
            throw;
        }
    }

    private bool CanIssueHighAvailability(ManagementOperationKind kind)
    {
        if (kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect)) return true;
        if (_availability is not { State: { } state, VerifiedAt: { } time } ||
            _time.GetUtcNow() - time >= TimeSpan.FromSeconds(10) || time > _time.GetUtcNow().AddSeconds(5)) return false;
        if (kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild)) return true;
        return SelectedNode is { IsPlaceholder: false } selected && selected.NodeId == HighAvailabilityTarget.Trim() &&
            selected.Role == (kind == ManagementOperationKind.Takeover ? "备机" : "隔离") &&
            state.Nodes.TryGetValue(selected.NodeId, out var node) && IsHighAvailabilityTargetRoutable(node.NodeId) &&
            IsHighAvailabilityTarget(kind, state, node);
    }

    private bool IsHighAvailabilityTarget(ManagementOperationKind kind, ServerDeploymentState state, NodeState node) =>
        state.ActiveNodeId != node.NodeId && (kind == ManagementOperationKind.Rebuild
            ? node.Role == DeploymentNodeRole.Isolated
            : node.Role == DeploymentNodeRole.Standby && node.Reachable && node.DatabaseInRecovery &&
                _time.GetUtcNow() - node.ObservedAt < TimeSpan.FromSeconds(10) && node.ObservedAt <= _time.GetUtcNow().AddSeconds(5));

    private HighAvailabilityRequest BuildHighAvailabilityRequest(ManagementOperationKind kind, ManagementTarget target, string actor, string id, IsolationEvidence? confirmedIsolation = null, bool confirmedRebuild = false)
    {
        if (_availability is not { State: { } state, VerifiedAt: { } time } ||
            _time.GetUtcNow() - time >= TimeSpan.FromSeconds(10) || time > _time.GetUtcNow().AddSeconds(5))
            throw new ArgumentException("先重新查询当前主备状态，再确认操作。");
        if (HighAvailabilityReason.Length > 500 ||
            string.IsNullOrWhiteSpace(HighAvailabilityTarget) || !state.Nodes.ContainsKey(HighAvailabilityTarget.Trim()))
            throw new ArgumentException("请选择已配置的目标节点；备注最多500字。");
        if (kind is ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild &&
            (!CanIssueHighAvailability(kind) || HighAvailabilityTarget.Trim() != target.NodeId))
            throw new ArgumentException("所选节点角色或管理连接已变化，请重新选择目标。");
        var isolation = kind == ManagementOperationKind.Takeover ? confirmedIsolation : null;
        if (kind == ManagementOperationKind.Takeover && (isolation is not { IsSufficient: true } ||
            isolation.IsolatedNodeId != state.ActiveNodeId || isolation.ConfirmedBy != actor || string.IsNullOrWhiteSpace(isolation.Reason)))
            throw new ArgumentException("必须确认原主机断电或业务网络隔离；心跳丢失不足以接管。");
        if (kind == ManagementOperationKind.Rebuild && !confirmedRebuild)
            throw new ArgumentException("须先确认重建目标服务器旧数据并保持隔离。");
        return new(id, target.ServerId, target.NodeId, actor, state.ServerEpoch, state.Version,
            HighAvailabilityTarget.Trim(), HighAvailabilityReason.Trim(), isolation, confirmedRebuild);
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

    public void ApplyHighAvailability(HighAvailabilityStatus status) => ApplyHighAvailability(status, proofAlreadyPersisted: false);

    private void ApplyHighAvailability(HighAvailabilityStatus status, bool proofAlreadyPersisted)
    {
        var target = _connection?.Target;
        if (target is null) throw new InvalidDataException("当前管理连接尚未建立。");
        ValidateHighAvailabilityStatus(status, target);
        if (status.State is { } reported)
        {
            var knownEpoch = proofAlreadyPersisted ? _highestObservedServerEpoch : ReadKnownServerEpoch(target);
            if (reported.ServerEpoch < knownEpoch)
            {
                // State versions are node-local, but a deployment epoch identifies the active
                // authority for the whole server. A peer cannot revive an older authority.
                if (_availability?.State is not { } current || current.ServerEpoch < knownEpoch) MarkProtectionUnknown();
                return;
            }
            _highestObservedServerEpoch = reported.ServerEpoch;
        }
        if (status.State is { } display && (_displayState is null || _displayTargetKey != target.Key || display.ServerEpoch > _displayState.ServerEpoch ||
            display.ServerEpoch == _displayState.ServerEpoch && (display.Version > _displayState.Version ||
                display.Version == _displayState.Version && (_displayVerifiedAt is null || status.VerifiedAt >= _displayVerifiedAt))))
        {
            _displayState = display; _displayServerId = target.ServerId; _displayTargetKey = target.Key; _displayVerifiedAt = status.VerifiedAt;
        }
        if (status.VerifiedAt is not { } time || status.State is not { } state ||
            _time.GetUtcNow() - time >= TimeSpan.FromSeconds(10) || time > _time.GetUtcNow().AddSeconds(5))
        {
            if (status.State is null || status.VerifiedAt is null || _availability is null || status.VerifiedAt >= _availability.VerifiedAt)
                MarkProtectionUnknown();
            return;
        }
        var persisted = proofAlreadyPersisted ? null : _protectionMemory?.Load(target);
        if (persisted is not null && (_protectionEvidence is null || _protectionEvidence.TargetKey != target.Key || OlderThan(_protectionEvidence.Epoch,
                _protectionEvidence.Version, _protectionEvidence.ObservedAt, persisted)))
        { _protectionEvidence = persisted; _lastDegraded = persisted.Degraded; }
        if (_protectionEvidence is { } prior && prior.TargetKey == target.Key && OlderThan(state.ServerEpoch, state.Version, time, prior))
        {
            if (_availability is null) MarkProtectionUnknown();
            return;
        }
        _availability = status;
        _lastDegraded = !status.SynchronousProtectionVerified;
        if (!proofAlreadyPersisted) _protectionMemory?.Save(target, state.ServerEpoch, state.Version, time, _lastDegraded);
        _protectionEvidence = new(target.Key, state.ServerEpoch, state.Version, time, _lastDegraded);
        ProtectionText = status.SynchronousProtectionVerified
            ? "双节点保护正常：数据库、试题资源与最新持久确认已同步。"
            : state.ActiveNodeId == target.NodeId
                ? "单节点运行：本机可靠保存后即可继续业务，复制恢复后自动补齐；当前新数据可能只有一份。"
                : "当前为备用节点。原主故障时，请确认原主已断电或隔离全部业务网络，再点击“接管为主服务器”。";
        UpdateDisplayedNodes();
        OnPropertyChanged(nameof(HighAvailabilityVersionText)); OnPropertyChanged(nameof(ShowProtectionWarning)); UpdateCommands();
        _protectionExpiry?.Cancel();
        _ = ExpireProtectionAsync(status, _protectionExpiry = new CancellationTokenSource());
    }
    private void ValidateHighAvailabilityStatus(HighAvailabilityStatus status, ManagementTarget target)
    {
        if (status.ServerId != target.ServerId || status.NodeId != target.NodeId || status.Checks is null ||
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
    }

    private static bool OlderThan(long epoch, long version, DateTimeOffset observed, ProtectionWarningMemory prior) =>
        epoch < prior.Epoch || epoch == prior.Epoch && (version < prior.Version || version == prior.Version && observed < prior.ObservedAt);

    private long ReadKnownServerEpoch(ManagementTarget target)
    {
        if (_observedEpochServerId != target.ServerId)
        { _observedEpochServerId = target.ServerId; _highestObservedServerEpoch = 0; }
        foreach (var configured in ConfiguredManagementTargets.Where(value => value.ServerId == target.ServerId))
        {
            var proof = _protectionMemory?.Load(configured);
            if (proof is null) continue;
            if (proof.TargetKey != configured.Key) throw new InvalidDataException("持久主备观察与配置节点不一致。");
            _highestObservedServerEpoch = Math.Max(_highestObservedServerEpoch, proof.Epoch);
        }
        return _highestObservedServerEpoch;
    }

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
        _availability = null; UpdateDisplayedNodes();
        ProtectionText = _lastDegraded ? "此前处于单节点运行；当前主备保护状态未核实，已保存的数据和原请求继续保留。" : "当前主备保护状态未核实，已保存的数据和原请求继续保留。";
        OnPropertyChanged(nameof(HighAvailabilityVersionText)); OnPropertyChanged(nameof(ShowProtectionWarning));
        UpdateCommands();
    }
    private void UpdateDisplayedNodes()
    {
        if (_displayServerId.Length > 0 && _displayServerId != ServerId.Trim())
        { _displayState = null; _displayVerifiedAt = null; _displayServerId = string.Empty; _displayTargetKey = string.Empty; }
        var rows = new List<ManagementNodeRow>();
        if (_displayState is { } state)
        {
            foreach (var node in state.Nodes.Values.OrderBy(n => n.NodeId == state.ActiveNodeId ? 0 : 1).ThenBy(n => n.NodeId, StringComparer.Ordinal))
            {
                var local = LocalObservation(node.NodeId);
                var observed = local?.State ?? node;
                // Local health remains independent, but an old local database cannot revive a retired role.
                if (local is not null && (local.Epoch <= state.ServerEpoch || !observed.Reachable))
                    observed = observed with { Role = node.Role };
                var fresh = (local is not null || _availability is not null) && _time.GetUtcNow() - observed.ObservedAt < TimeSpan.FromSeconds(10) &&
                    observed.ObservedAt <= _time.GetUtcNow().AddSeconds(5);
                rows.Add(LocalNodeRow(observed, fresh));
            }
        }
        foreach (var target in ConfiguredManagementTargets)
            if (rows.Count < 2 && rows.All(n => n.NodeId != target.NodeId))
                rows.Add(LocalObservation(target.NodeId) is { } local
                    ? LocalNodeRow(local.State, _time.GetUtcNow() - local.State.ObservedAt < TimeSpan.FromSeconds(10)) with
                        { Role = local.Epoch == 0 ? "角色待核实" : LocalNodeRow(local.State, false).Role }
                    : PlaceholderNode(target.NodeId));
        if (rows.Count == 0) rows.Add(PlaceholderNode(string.IsNullOrWhiteSpace(NodeId) ? "主机（待连接）" : NodeId.Trim()));
        if (rows.Count == 1) rows.Add(PlaceholderNode("另一节点（待核实）"));
        var selection = SelectedNode?.NodeId;
        // Avoid Reset/Clear: the grid keeps both rows visible while fresh evidence is pending.
        for (var i = 0; i < rows.Count; i++)
            if (i >= Nodes.Count) Nodes.Add(rows[i]); else if (Nodes[i] != rows[i]) Nodes[i] = rows[i];
        while (Nodes.Count > rows.Count) Nodes.RemoveAt(Nodes.Count - 1);
        if (selection is not null) SelectedNode = Nodes.FirstOrDefault(n => n.NodeId == selection);
        OnPropertyChanged(nameof(PrimaryServerStatusText)); OnPropertyChanged(nameof(StandbyServerStatusText));
        OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(ManagedServersText));
    }
    private static ManagementNodeRow PlaceholderNode(string id) =>
        new(id, "角色待核实", "待查询", "未核实", "未核实", "未核实", "未核实", "尚未返回", true);

    private string NodeStatusText(bool primary)
    {
        var node = _displayState?.Nodes.Values.FirstOrDefault(n => primary ? n.NodeId == _displayState.ActiveNodeId : n.NodeId != _displayState.ActiveNodeId);
        if (node is null)
        {
            var configured = ConfiguredManagementTargets.ElementAtOrDefault(primary ? 0 : 1);
            return configured is null ? "节点角色与运行状态待查询。" :
                $"已配置节点 {configured.NodeId}\n角色待核实；{GetNodeProcessStatusText(configured.NodeId)}。";
        }
        var row = Nodes.FirstOrDefault(n => n.NodeId == node.NodeId);
        var observed = node.ObservedAt == default ? "尚无观察" : node.ObservedAt.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss");
        var latest = row is { IsCurrent: true };
        var process = GetNodeProcessStatusText(node.NodeId);
        return $"{node.NodeId} · {row?.Role}\n{process}\n" +
            (latest ? $"{row!.Reachability}；数据库：{row.Recovery}；重放差值：{row.ReplayLag} 字节。" :
                $"当前状态未核实。上次观察：{(node.Reachable ? "可连接" : "不可连接")}；{(node.DatabaseInRecovery ? "备库" : "主库")}。") +
            $"\n观察时间：{observed}" + (latest ? "" : "（仅供参考，请刷新）");
    }

    private void LoadProtectionWarning(ManagementTarget? target = null)
    {
        try
        {
            target ??= new(Address, ServerId, NodeId);
            UpdateDisplayedNodes();
            if (string.IsNullOrWhiteSpace(Address)) return;
            _protectionEvidence = null;
            _protectionEvidence = _protectionMemory?.Load(target);
            _lastDegraded = _protectionEvidence?.Degraded ?? false; MarkProtectionUnknown();
        }
        catch { _lastDegraded = true; MarkProtectionUnknown(); }
    }
}
