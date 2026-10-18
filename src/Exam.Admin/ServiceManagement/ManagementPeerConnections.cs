using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Exam.Admin.Services;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    private sealed class ManagedNodeSession(ManagementTarget target)
    {
        public ManagementTarget Target { get; } = target;
        public IManagementConnection? Connection;
        public Task<IManagementConnection?>? Authentication;
        public Exception? AuthenticationError;
        public AdminSessionSnapshot? AttemptedBusinessSession;
        public DateTimeOffset NextBusinessAuthenticationAt;
        public long StatusSequence;
        public object Gate { get; } = new();
    }
    private sealed record NodeStatusObservation(ManagementStatus? Status, DateTimeOffset ObservedAt,
        long Sequence, bool Unavailable);

    private ManagementTarget? _configuredManagementTarget;
    private ManagementTarget? _peerLoginTarget;
    private readonly ConcurrentDictionary<string, ManagedNodeSession> _nodeSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeStatusObservation> _nodeStatuses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingManagementRequest> _knownPendingRequests = new(StringComparer.Ordinal);
    private CancellationTokenSource? _peerAuthentication;
    private CancellationTokenSource? _routingCancellation;
    private long _managementSessionVersion;
    private bool _routingConnection;
    private bool _peerStatusRefreshing;
    private Task? _peerStatusRefresh;
    private Task? _pendingReconciliation;
    private DateTimeOffset _lastPeerStatusRefresh = DateTimeOffset.MinValue;

    public bool ShowManagementLogin => IsSignedOut || _peerLoginTarget is not null;
    public bool CanEnterManagementCredentials => !IsBusy && ShowManagementLogin;
    private bool HasUnresolvedManagementRequests => _pending is not null || !_knownPendingRequests.IsEmpty;

    private bool HasConflictingManagementRequests(ManagementOperationKind kind)
    {
        if (kind != ManagementOperationKind.Takeover) return HasUnresolvedManagementRequests;
        var target = HighAvailabilityTarget.Trim();
        // Retain the isolated peer's read-only request for later audit. Never move it
        // to another node, discard it, or ignore an unresolved mutating operation.
        return _knownPendingRequests.Values.Append(_pending).Any(request => request is not null &&
            (request.Kind != ManagementOperationKind.Checks || request.Target.NodeId == target));
    }

    private ManagementTarget CurrentTarget()
    {
        var root = new ManagementTarget(Address.Trim(), ServerId.Trim(), NodeId.Trim());
        if (_configuredManagementTarget is { } configured && SameEndpoint(root, configured))
            return configured;
        return root;
    }
    private static bool SameEndpoint(ManagementTarget left, ManagementTarget right)
    {
        try { return left.Key == right.Key; }
        catch (ArgumentException) { return false; }
    }
    private void RememberConfiguredTarget(ManagementTarget target)
    {
        target.Validate();
        _configuredManagementTarget = target;
        Address = target.Address; ServerId = target.ServerId; NodeId = target.NodeId;
        UpdateDisplayedNodes();
    }
    private IReadOnlyList<ManagementTarget> ConfiguredManagementTargets
    {
        get
        {
            try
            {
                var root = CurrentTarget();
                root.Validate();
                return root.PeerTarget is { } peer ? [root, peer] : [root];
            }
            catch (ArgumentException) { return []; }
        }
    }
    private ManagedNodeSession NodeSession(ManagementTarget target) =>
        _nodeSessions.GetOrAdd(target.Key, _ => new(target));

    private bool IsHighAvailabilityTargetRoutable(string nodeId) =>
        ConfiguredManagementTargets.Any(target => target.NodeId == nodeId);

    private async Task<IManagementConnection?> AuthenticateNodeAsync(ManagedNodeSession node,
        Func<IManagementConnection, CancellationToken, Task> authenticate, string username, long sessionVersion,
        CancellationToken token)
    {
        IManagementConnection? connection = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            connection = _factory(node.Target);
            if (!SameEndpoint(connection.Target, node.Target))
                throw new InvalidDataException("管理连接工厂返回了其他节点的连接。");
            await authenticate(connection, deadline.Token).WaitAsync(deadline.Token);
            if (string.IsNullOrWhiteSpace(connection.Actor) ||
                !string.Equals(connection.Actor, username.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("管理节点返回的考务账号不一致。");
            lock (node.Gate)
            {
                if (token.IsCancellationRequested || _disposed || sessionVersion != _managementSessionVersion)
                    return null;
                node.AuthenticationError = null;
                node.Connection = connection;
                connection = null; // Ownership belongs to the per-node session until logout/reset.
            }
            if (_peerLoginTarget is { } awaiting && SameEndpoint(awaiting, node.Target)) SetPeerLoginTarget(null);
            UpdateCommands();
            if (IsSignedIn && !ReferenceEquals(node.Connection, _connection))
                _ = RefreshPeerStatusAsync(force: true);
            return node.Connection;
        }
        catch (Exception error)
        {
            if (sessionVersion == _managementSessionVersion && !token.IsCancellationRequested)
                node.AuthenticationError = error;
            return null;
        }
        finally { connection?.Dispose(); }
    }

    private Task<IManagementConnection?> StartNodeAuthentication(ManagementTarget target,
        Func<IManagementConnection, CancellationToken, Task> authenticate, string username)
    {
        var node = NodeSession(target);
        lock (node.Gate)
        {
            if (node.Connection is { Actor: not null } existing) return Task.FromResult<IManagementConnection?>(existing);
            if (node.Authentication is { IsCompleted: false } pending) return pending;
            _peerAuthentication ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var task = node.Authentication = AuthenticateNodeAsync(node, authenticate, username, _managementSessionVersion, _peerAuthentication.Token);
            _ = task.ContinueWith(completed =>
            {
                // A finished independent login must not retain a closure containing the password.
                lock (node.Gate) if (ReferenceEquals(node.Authentication, completed)) node.Authentication = null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private Task<IManagementConnection?> StartBusinessAuthentication(ManagementTarget target, AdminSessionSnapshot session)
    {
        var node = NodeSession(target);
        lock (node.Gate)
        {
            if (node.Connection is { Actor: not null } ready) return Task.FromResult<IManagementConnection?>(ready);
            if (node.Authentication is { IsCompleted: false } pending) return pending;
            // An isolated database may not contain a session issued by the new primary.
            // Back off repeated exchanges; retry later because a rebuilt replica may
            // eventually contain it. Explicit password authentication bypasses this gate.
            if (node.AttemptedBusinessSession?.SameAuthentication(session) == true &&
                _time.GetUtcNow() < node.NextBusinessAuthenticationAt)
                return Task.FromResult<IManagementConnection?>(null);
            node.AttemptedBusinessSession = session;
            node.NextBusinessAuthenticationAt = _time.GetUtcNow().AddSeconds(30);
            return StartNodeAuthentication(target, (connection, token) => connection.UseBusinessSessionAsync(session, token), session.Username);
        }
    }

    private async Task<IManagementConnection?> ConnectTargetsAsync(ManagementTarget root,
        Func<IManagementConnection, CancellationToken, Task> authenticate, string username, CancellationToken token)
    {
        root.Validate();
        var targets = root.PeerTarget is { } peer ? new[] { root, peer } : [root];
        // Read every configured target's original request before allowing a new mutation.
        // A damaged request store must never be interpreted as "no pending operation".
        foreach (var target in targets)
            if (ReadPendingRequest(target) is { } pending) _knownPendingRequests[target.Key] = pending;
        var attempts = targets.Select(target => StartNodeAuthentication(target, authenticate, username)).ToList();
        // The business client already verified the active node. A pending request on
        // the isolated peer must not make that peer the page's current authority.
        var preferred = Array.FindIndex(targets, target => target.NodeId == _businessSession?.NodeId);
        if (preferred >= 0 && await attempts[preferred].WaitAsync(token) is { } active) return active;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts).WaitAsync(token);
            attempts.Remove(finished);
            if (await finished is not { } available) continue;
            // If both replies have already arrived, resume an original request on its own node.
            foreach (var node in _nodeSessions.Values)
                if (node.Connection is { } ready && _knownPendingRequests.ContainsKey(node.Target.Key))
                    return ready;
            return available;
        }
        token.ThrowIfCancellationRequested();
        throw _nodeSessions.Values.Select(node => node.AuthenticationError).FirstOrDefault(error => error is UnauthorizedAccessException)
            ?? _nodeSessions.Values.Select(node => node.AuthenticationError).FirstOrDefault(error => error is not null)
            ?? new HttpRequestException("已配置的主备管理服务均暂不可用。");
    }

    private void LoadCurrentPendingRequest()
    {
        if (_connection is null) return;
        SetCurrentPendingRequest(ReadPendingRequest(_connection.Target));
    }

    private async Task FollowBusinessManagementAsync(AdminSessionSnapshot session)
    {
        if (!IsSignedIn || IsBusy || _routingConnection || _businessSession?.Matches(session) != true ||
            session.NodeId is null || _connection?.Target.NodeId == session.NodeId) return;
        var target = ConfiguredManagementTargets.FirstOrDefault(value => value.NodeId == session.NodeId);
        if (target is null) return;
        var version = _managementSessionVersion;
        _routingConnection = true;
        try
        {
            var connection = await EnsureTargetSessionAsync(target, _lifetime.Token);
            if (connection is null || _disposed || version != _managementSessionVersion ||
                _businessSession?.Matches(session) != true || IsBusy) return;
            SelectCurrentConnection(connection, ReadPendingRequest(target));
            Message = "服务器管理已跟随当前活动节点，原节点的请求继续分别核对。";
            await RefreshAsync();
        }
        catch (Exception)
        { if (!_disposed && version == _managementSessionVersion) Message = "当前活动节点的管理连接暂不可用，原连接和请求继续保留。"; }
        finally { if (version == _managementSessionVersion) { _routingConnection = false; StartPolling(); } }
    }

    private PendingManagementRequest? ReadPendingRequest(ManagementTarget target)
    {
        var request = _store.Load(target);
        if (request is not null && !SameEndpoint(request.Target, target))
            throw new InvalidDataException("原请求记录与其管理节点不一致，不能创建新请求。");
        return request;
    }

    private void SetCurrentPendingRequest(PendingManagementRequest? request)
    {
        if (_connection is null) return;
        var target = _connection.Target;
        _pending = request;
        if (_pending is { } current) _knownPendingRequests[target.Key] = current;
        else _knownPendingRequests.TryRemove(target.Key, out _);
        var original = _pending ?? _knownPendingRequests.Values.OrderBy(value => value.Target.NodeId, StringComparer.Ordinal).FirstOrDefault();
        if (original is not null)
        {
            _lastRequestId = original.RequestId; RequestIdToQuery = original.RequestId;
            if (_pending is null) OperationText = $"节点 {original.Target.NodeId} 有待查询的原请求：{original.RequestId}";
        }
        NotifyPending();
        OnPropertyChanged(nameof(HighAvailabilityVersionText));
    }

    private void SetPeerLoginTarget(ManagementTarget? target)
    {
        _peerLoginTarget = target;
        OnPropertyChanged(nameof(ShowManagementLogin)); OnPropertyChanged(nameof(CanEnterManagementCredentials));
        OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(ConnectionStatus)); UpdateCommands();
    }

    private async Task<IManagementConnection?> EnsureTargetSessionAsync(ManagementTarget target, CancellationToken token,
        bool requestCredentials = false)
    {
        var version = _managementSessionVersion;
        bool CurrentAttempt() => !token.IsCancellationRequested && !_disposed && version == _managementSessionVersion;
        if (!CurrentAttempt()) return null;
        var node = NodeSession(target);
        if (node.Connection is { Actor: not null } ready) return ready;
        if (node.Authentication is { IsCompleted: false } pending)
        {
            var result = await pending.WaitAsync(token);
            if (!CurrentAttempt()) return null;
            if (result is not null) return result;
        }
        if (!CurrentAttempt()) return null;
        if (_businessSession is { } session)
        {
            var result = await StartBusinessAuthentication(target, session).WaitAsync(token);
            if (!CurrentAttempt()) return null;
            if (result is not null) return result;
            if (node.AuthenticationError is not UnauthorizedAccessException)
            {
                if (requestCredentials) Message = $"节点 {target.NodeId} 的管理连接暂不可用；当前管理连接及原请求继续保留。";
                return null;
            }
        }
        if (requestCredentials)
        {
            SetPeerLoginTarget(target);
            Message = $"需要验证节点 {target.NodeId} 的管理身份，请在本页输入当前考务账号的密码。当前管理连接继续保留。";
        }
        return null;
    }

    private async Task LoginPendingPeerAsync(string password)
    {
        if (_peerLoginTarget is not { } target || _connection?.Actor is not { } actor || IsBusy) return;
        var version = _managementSessionVersion;
        IsBusy = true;
        try
        {
            var result = await StartNodeAuthentication(target, (connection, cancellation) => connection.LoginAsync(actor, password, cancellation),
                actor).WaitAsync(_lifetime.Token);
            if (version != _managementSessionVersion || _disposed) return;
            if (result is null)
            { Message = $"节点 {target.NodeId} 的管理登录未完成，请核对密码及连接；当前管理会话继续保留。"; return; }
            SetPeerLoginTarget(null);
            Message = $"已验证节点 {target.NodeId} 的管理身份，请核对目标后继续操作。";
            _ = RefreshPeerStatusAsync(force: true);
        }
        catch (Exception) when (version != _managementSessionVersion || _disposed) { }
        finally { if (version == _managementSessionVersion) IsBusy = false; }
    }

    private void SelectCurrentConnection(IManagementConnection candidate, PendingManagementRequest? pending)
    {
        if (ReferenceEquals(candidate, _connection))
        { SetCurrentPendingRequest(pending); return; }
        _generation++;
        _reads?.Cancel(); _reads = null; _readBusy = false;
        _polling?.Cancel(); _availabilityPolling?.Cancel(); _restoreProgress?.Cancel();
        _connection = candidate; _configurationVersion = null; _operation = null; _restoreOperation = null;
        CanResumeRestore = false; _missingOriginal = false; Checks.Clear(); Logs.Clear();
        SetCurrentPendingRequest(pending);
        LoadProtectionWarning(candidate.Target);
        RestartBackupMonitoring();
        ServerStatusText = GetNodeProcessStatusText(candidate.Target.NodeId) ?? "服务器程序状态未确认";
        OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(ConfigurationVersionText));
    }

    private async Task<bool> RouteHighAvailabilityToSelectedNodeAsync(ManagementOperationKind kind)
    {
        if (_routingConnection || IsBusy || !IsSignedIn || _connection is null || HasConflictingManagementRequests(kind)) return false;
        var selected = HighAvailabilityTarget.Trim();
        var target = ConfiguredManagementTargets.FirstOrDefault(value => value.NodeId == selected);
        if (target is null || !CanVerifyHighAvailabilityTarget(kind))
        { Message = kind == ManagementOperationKind.Rebuild ? "请选择角色为隔离且已配置管理地址的节点。" : "请选择已核实且已配置管理地址的备用节点。"; return false; }
        _routingConnection = true; IsBusy = true;
        var version = _managementSessionVersion;
        var previous = _connection;
        try
        {
            using var deadline = _routingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var candidate = await EnsureTargetSessionAsync(target, deadline.Token, requestCredentials: true);
            if (candidate is null || version != _managementSessionVersion || !IsSignedIn || selected != HighAvailabilityTarget.Trim()) return false;
            if (!string.Equals(candidate.Actor, previous.Actor, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("两个管理节点的考务账号不一致。");
            var reply = await candidate.HighAvailabilityAsync(deadline.Token);
            if (deadline.IsCancellationRequested || version != _managementSessionVersion || !IsSignedIn ||
                selected != HighAvailabilityTarget.Trim()) return false;
            if (reply.IsUnauthorized || reply.Envelope.Error is not null || reply.Envelope.Result is not { } status)
            { MarkNodeSessionUnavailable(candidate, unauthorized: reply.IsUnauthorized); Message = "目标节点暂未返回有效主备状态，当前管理连接继续保留。"; return false; }
            ValidateHighAvailabilityStatus(status, candidate.Target);
            if (status.State is not { } state || status.VerifiedAt is not { } observed ||
                _time.GetUtcNow() - observed >= HighAvailabilityStatus.ObservationLifetime || observed > _time.GetUtcNow().AddSeconds(5) ||
                !state.Nodes.TryGetValue(selected, out var node) || !IsHighAvailabilityTarget(kind, state, node) ||
                state.ServerEpoch < ReadKnownServerEpoch(candidate.Target) ||
                _protectionEvidence is { } previousProof && previousProof.TargetKey == candidate.Target.Key && (state.ServerEpoch < previousProof.Epoch ||
                    state.ServerEpoch == previousProof.Epoch && state.Version < previousProof.Version))
            { Message = "目标节点的角色或主备状态尚未核实，当前管理连接继续保留。"; return false; }
            var pending = ReadPendingRequest(candidate.Target);
            if (pending is not null) _knownPendingRequests[candidate.Target.Key] = pending;
            var targetProof = _protectionMemory?.Load(candidate.Target);
            if (targetProof is not null && OlderThan(state.ServerEpoch, state.Version, observed, targetProof))
            { Message = "目标节点返回的主备版本落后于本机已保存的观察，当前管理连接继续保留。"; return false; }
            // Complete fallible local persistence before replacing a working route. No token is
            // copied: the candidate continues using only its independently acquired session.
            _protectionMemory?.Save(candidate.Target, state.ServerEpoch, state.Version, observed, !status.SynchronousProtectionVerified);
            // Change the active route only after this target's own authentication and HA read succeeded.
            SelectCurrentConnection(candidate, pending);
            ApplyHighAvailability(status, proofAlreadyPersisted: true);
            if (pending is not null)
            { Message = "目标节点存在原请求，请先查询其处理状态。"; return false; }
            return true;
        }
        catch (Exception error)
        {
            if (version == _managementSessionVersion)
                Message = error is UnauthorizedAccessException ? "目标节点的管理认证未通过，当前管理连接继续保留。" :
                    "目标节点连接或主备查询暂不可用，当前管理连接继续保留。";
            return false;
        }
        finally
        {
            if (version == _managementSessionVersion)
            { _routingCancellation = null; _routingConnection = false; IsBusy = false; StartPolling(); }
        }
    }

    private async Task<bool> RoutePendingQueryAsync(string id)
    {
        var request = _knownPendingRequests.Values.FirstOrDefault(value => value.RequestId == id);
        if (request is null || _connection is null || SameEndpoint(request.Target, _connection.Target)) return true;
        if (_routingConnection || IsBusy || _pending is not null)
        { Message = "请先完成当前节点原请求的状态查询。"; return false; }
        var target = ConfiguredManagementTargets.FirstOrDefault(value => SameEndpoint(value, request.Target));
        if (target is null) return false;
        var version = _managementSessionVersion;
        var generation = _generation;
        _routingConnection = true; IsBusy = true;
        try
        {
            using var deadline = _routingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var candidate = await EnsureTargetSessionAsync(target, deadline.Token, requestCredentials: true);
            if (candidate is null || deadline.IsCancellationRequested || version != _managementSessionVersion ||
                generation != _generation || !IsSignedIn) return false;
            if (!string.Equals(candidate.Actor, request.Actor, StringComparison.OrdinalIgnoreCase))
            { Message = "此原请求属于其他考务账号，请使用原账号登录后查询。"; return false; }
            var current = ReadPendingRequest(candidate.Target);
            SelectCurrentConnection(candidate, current);
            return true;
        }
        catch
        {
            if (version == _managementSessionVersion) Message = "原请求所属节点暂不可用；原请求与当前管理连接继续保留。";
            return false;
        }
        finally
        {
            if (version == _managementSessionVersion)
            { _routingCancellation = null; _routingConnection = false; IsBusy = false; StartPolling(); }
        }
    }

    private long BeginNodeStatusObservation(ManagementTarget target) =>
        Interlocked.Increment(ref NodeSession(target).StatusSequence);

    private bool RememberNodeStatus(ManagementTarget target, ManagementStatus status, long? sequence = null)
    {
        ValidateManagementStatus(status, target);
        var next = new NodeStatusObservation(status, _time.GetUtcNow(), sequence ?? BeginNodeStatusObservation(target), false);
        var accepted = _nodeStatuses.AddOrUpdate(target.Key, next, (_, old) => next.Sequence >= old.Sequence ? next : old);
        if (!ReferenceEquals(accepted, next)) return false;
        OnPropertyChanged(nameof(PrimaryServerStatusText)); OnPropertyChanged(nameof(StandbyServerStatusText));
        return true;
    }
    private void MarkNodeStatusUnavailable(ManagementTarget target, long sequence)
    {
        var unavailable = new NodeStatusObservation(null, DateTimeOffset.MinValue, sequence, true);
        _nodeStatuses.AddOrUpdate(target.Key, unavailable, (_, old) => sequence >= old.Sequence ? old with { Sequence = sequence, Unavailable = true } : old);
        OnPropertyChanged(nameof(PrimaryServerStatusText)); OnPropertyChanged(nameof(StandbyServerStatusText));
    }
    private static void ValidateManagementStatus(ManagementStatus value, ManagementTarget target)
    {
        if (!Enum.IsDefined(value.ProcessStatus) || !Enum.IsDefined(value.Readiness) || value.Checks is null ||
            value.Configuration is { } configuration && (configuration.ServerId != target.ServerId || configuration.NodeId != target.NodeId) ||
            value.Readiness == BusinessReadiness.Ready && (value.ProcessStatus != BusinessServiceStatus.Running ||
                value.Checks.Count == 0 || value.Checks.Any(check => check.BlocksWrites && !check.Passed)))
            throw new InvalidDataException("业务就绪证据或节点身份不一致。");
    }
    private static string ProcessStatusText(ManagementStatus status) => status.ProcessStatus switch
    {
        BusinessServiceStatus.Stopped => "服务器已停止",
        BusinessServiceStatus.Faulted => "服务器程序运行异常",
        BusinessServiceStatus.Running when status.Readiness == BusinessReadiness.Ready => "服务器程序正常运行",
        BusinessServiceStatus.Running when status.Readiness == BusinessReadiness.NotReady => "服务器程序已启动，业务尚未就绪",
        _ => "服务器程序状态未确认"
    };
    private string? GetNodeProcessStatusText(string nodeId)
    {
        var target = ConfiguredManagementTargets.FirstOrDefault(value => value.NodeId == nodeId);
        if (target is null) return "管理状态待查询";
        if (!_nodeStatuses.TryGetValue(target.Key, out var value) || value.Status is null) return "管理状态待查询";
        var fresh = !value.Unavailable && _time.GetUtcNow() - value.ObservedAt < TimeSpan.FromSeconds(30);
        var standby = fresh && value.Status is { ProcessStatus: BusinessServiceStatus.Running, Readiness: BusinessReadiness.NotReady } &&
            _displayState?.Nodes.GetValueOrDefault(nodeId) is { Role: DeploymentNodeRole.Standby,
                Reachable: true, DatabaseInRecovery: true, KeyMaterialReady: true, CertificateReady: true } &&
            Nodes.Any(row => row.NodeId == nodeId && row.IsCurrent);
        return standby ? "服务器程序已启动（备机待命）" : fresh ? ProcessStatusText(value.Status) :
            $"上次程序状态：{ProcessStatusText(value.Status)}；当前未核实（{value.ObservedAt.ToOffset(TimeSpan.FromHours(8)):HH:mm:ss}）";
    }
    private void MarkNodeSessionUnavailable(IManagementConnection connection, bool unauthorized)
    {
        MarkNodeStatusUnavailable(connection.Target, BeginNodeStatusObservation(connection.Target));
        if (!unauthorized) return;
        if (ReferenceEquals(connection, _connection)) { HandleUnauthorizedNode(connection); return; }
        if (_nodeSessions.TryGetValue(connection.Target.Key, out var node))
        {
            lock (node.Gate)
                if (ReferenceEquals(node.Connection, connection)) { node.Connection = null; connection.Dispose(); }
        }
        UpdateCommands();
    }

    private void HandleUnauthorizedNode(IManagementConnection failed)
    {
        if (!ReferenceEquals(failed, _connection)) return;
        var expectedActor = _businessSession?.Username ?? Username.Trim();
        MarkNodeStatusUnavailable(failed.Target, BeginNodeStatusObservation(failed.Target));
        if (_nodeSessions.TryGetValue(failed.Target.Key, out var failedNode))
        {
            lock (failedNode.Gate)
                if (ReferenceEquals(failedNode.Connection, failed)) failedNode.Connection = null;
        }
        failed.Dispose();
        // A 401 belongs to one management session. Keep another node's independently
        // authenticated connection and every original request, including failed-node requests.
        var available = _nodeSessions.Values.Select(node => node.Connection).FirstOrDefault(connection =>
            connection is not null && !ReferenceEquals(connection, failed) &&
            string.Equals(connection.Actor, expectedActor, StringComparison.OrdinalIgnoreCase));
        if (available is not null)
        {
            try
            {
                var pending = ReadPendingRequest(available.Target);
                SelectCurrentConnection(available, pending);
                IsBusy = false;
                Message = $"节点 {failed.Target.NodeId} 的管理会话已失效；已保留节点 {available.Target.NodeId} 的独立管理会话。原请求继续按所属节点查询。";
                StartPolling();
                return;
            }
            catch { /* Preserve all remaining sessions when local request recovery is unavailable. */ }
        }
        _generation++; _reads?.Cancel(); _reads = null; _readBusy = false;
        _polling?.Cancel(); _availabilityPolling?.Cancel(); _restoreProgress?.Cancel(); _backupMonitoring?.Cancel();
        _connection = null; IsSignedIn = false; IsBusy = false; MarkUnknown(); NotifyPending();
        Message = "当前节点的管理会话已失效。其他节点会话和原请求继续保留，请重新连接后查询。";
    }

    private Task RefreshPeerStatusAsync(bool force = false)
    {
        if (!IsSignedIn || _disposed) return Task.CompletedTask;
        if (_peerStatusRefreshing) return _peerStatusRefresh ?? Task.CompletedTask;
        if (!force && _time.GetUtcNow() - _lastPeerStatusRefresh < TimeSpan.FromSeconds(10)) return Task.CompletedTask;
        _lastPeerStatusRefresh = _time.GetUtcNow();
        _peerStatusRefreshing = true;
        var version = _managementSessionVersion;
        var token = _lifetime.Token;
        return _peerStatusRefresh = RefreshConfiguredNodeStatusesAsync(version, token);
    }
    private async Task RefreshConfiguredNodeStatusesAsync(long version, CancellationToken token)
    {
        var jobs = ConfiguredManagementTargets.Select(async target =>
        {
            IManagementConnection? connection = null;
            var sequence = BeginNodeStatusObservation(target);
            try
            {
                var node = NodeSession(target);
                connection = node.Connection;
                if (connection is null && _businessSession is { } session)
                    connection = await StartBusinessAuthentication(target, session).WaitAsync(token);
                if (connection is null || token.IsCancellationRequested || version != _managementSessionVersion) return;
                // Explicit refresh owns the current node's status read; peers remain independent.
                if (ReferenceEquals(connection, _connection) && (_readBusy || IsBusy)) return;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var reply = await connection.StatusAsync(deadline.Token);
                if (deadline.IsCancellationRequested || version != _managementSessionVersion || _disposed) return;
                if (reply.IsUnauthorized || reply.Envelope.Error is not null || reply.Envelope.Result is not { } value ||
                    reply.Envelope.ServerId != target.ServerId || reply.Envelope.NodeId != target.NodeId)
                { MarkNodeStatusUnavailable(target, sequence); if (reply.IsUnauthorized) MarkNodeSessionUnavailable(connection, true); return; }
                if (ReferenceEquals(connection, _connection)) ApplyStatus(value, reply.Envelope.EpochVerified, reply.Envelope.ServerEpoch, sequence);
                else RememberNodeStatus(target, value, sequence);
            }
            catch
            {
                if (!token.IsCancellationRequested && version == _managementSessionVersion)
                    MarkNodeStatusUnavailable(target, sequence);
            }
        });
        try { await Task.WhenAll(jobs); }
        finally { if (version == _managementSessionVersion) _peerStatusRefreshing = false; }
    }

    private IManagementConnection[] ReleasePeerConnections(bool dispose)
    {
        _managementSessionVersion++;
        _routingCancellation?.Cancel();
        _routingCancellation = null; _routingConnection = false;
        _peerAuthentication?.Cancel(); _peerAuthentication?.Dispose(); _peerAuthentication = null;
        _peerStatusRefresh = null; _peerStatusRefreshing = false; _lastPeerStatusRefresh = DateTimeOffset.MinValue;
        _pendingReconciliation = null;
        var connections = _nodeSessions.Values.Select(value => { lock (value.Gate) return value.Connection; }).Append(_connection)
            .Where(value => value is not null).Cast<IManagementConnection>().Distinct<IManagementConnection>(ReferenceEqualityComparer.Instance).ToArray();
        _nodeSessions.Clear(); _knownPendingRequests.Clear();
        _localNodeHealth.Clear();
        foreach (var key in _nodeStatuses.Keys)
            if (_nodeStatuses.TryGetValue(key, out var old)) _nodeStatuses[key] = old with { Sequence = 0, Unavailable = true };
        SetPeerLoginTarget(null);
        if (dispose) foreach (var connection in connections) connection.Dispose();
        return connections;
    }
}
