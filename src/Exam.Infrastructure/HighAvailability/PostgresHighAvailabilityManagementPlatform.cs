using System.Data;
using System.Security.AccessControl;
using System.Net.Http.Headers;
using System.Text.Json;
using Exam.Application.HighAvailability;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.HighAvailability;

public sealed record FailoverNodeDeployment(string NodeId, string ConnectionString, string ResourcesDirectory,
    string CertificateThumbprint, string ConfirmationDirectory, string BusinessHttpsUrl)
{
    public string? ReplicationConnectionString { get; init; }
}

public sealed record HighAvailabilityManagementOptions(string ServerId, string LocalNodeId,
    IReadOnlyDictionary<string, FailoverNodeDeployment> Nodes, string SelectionPath, string RebuildRoot,
    string PostgresBinDirectory, string PostgresDataDirectory, string PostgresServiceName,
    string BusinessServiceName, string PeerToken, string PostgresConfigurationPath = "",
    string WalArchiveDirectory = "", string WalArchiveScriptsDirectory = "")
{
    public int WalArchiveTimeoutSeconds { get; init; } = 60;
    public string PostgresHbaPath { get; init; } = string.Empty;
}

/// <summary>Actual PostgreSQL state, independent confirmation heads and configured local service controls.</summary>
public sealed partial class PostgresHighAvailabilityManagementPlatform : IHighAvailabilityManagementPlatform, IDisposable
{
    private readonly HighAvailabilityManagementOptions _options;
    private readonly HttpClient _http;
    private readonly IStartupReadinessProbe _localMaterials;
    private readonly IExternalProcessRunner _processes;
    private readonly IManagedBusinessHost _business, _postgres;
    private readonly TimeProvider _time;
    private readonly Action _verifyDeployment;
    private readonly Func<FailoverDeploymentSelection?> _readSelection;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _stateSources = new(StringComparer.Ordinal);
    private readonly PostgresNodeHealthProbe _healthProbe;
    private readonly ResourceDirectoryDigestCache _localResourceDigest;
    private readonly Action<string, Exception>? _recordProbeFailure;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _probeFailureTimes = new(StringComparer.Ordinal);
    private FailoverNodeDeployment Local => _options.Nodes[_options.LocalNodeId];
    private FailoverNodeDeployment Other => _options.Nodes.Values.Single(n => n.NodeId != _options.LocalNodeId);

    public PostgresHighAvailabilityManagementPlatform(HighAvailabilityManagementOptions options, HttpClient http,
        IStartupReadinessProbe? localMaterials = null, IExternalProcessRunner? processes = null,
        IManagedBusinessHost? business = null, IManagedBusinessHost? postgres = null, TimeProvider? time = null,
        Action? verifyDeployment = null, Action<string, Exception>? recordProbeFailure = null,
        Func<FailoverDeploymentSelection?>? readSelection = null)
    {
        if (options.Nodes.Count is < 1 or > 2 || !options.Nodes.ContainsKey(options.LocalNodeId) ||
            options.Nodes.Any(p => p.Key != p.Value.NodeId || string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 200 ||
                p.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))))
            throw new ArgumentException("正式服务器管理须配置一个节点或两个不同节点，并包含本机节点。");
        static (string Host, int Port) Endpoint(FailoverNodeDeployment node)
        {
            var connection = new NpgsqlConnectionStringBuilder(node.ConnectionString);
            var host = (connection.Host ?? throw new ArgumentException("节点数据库监听地址未配置。")).Trim().ToLowerInvariant();
            return (host is "localhost" or "127.0.0.1" or "::1" ? "loopback" : host, connection.Port);
        }
        if (options.Nodes.Values.Select(Endpoint).Distinct().Count() != options.Nodes.Count)
            throw new ArgumentException("两个节点不能引用同一数据库监听端点。");
        _options = options; _http = http; _time = time ?? TimeProvider.System;
        _readSelection = readSelection ?? (() => new FileFailoverDeploymentSelection(options.SelectionPath).Read());
        _localResourceDigest = new(Local.ResourcesDirectory);
        _recordProbeFailure = recordProbeFailure;
        _healthProbe = new(options.Nodes.ToDictionary(pair => pair.Key,
            pair => new PostgresNodeConnection(pair.Key, pair.Value.ConnectionString)), _time);
        _processes = processes ?? new ExternalProcessRunner();
        _business = business ?? new WindowsServiceBusinessHost(options.BusinessServiceName);
        _postgres = postgres ?? new WindowsServiceBusinessHost(options.PostgresServiceName, timeout: TimeSpan.FromMinutes(2));
        _verifyDeployment = verifyDeployment ?? (() =>
        {
            if (new ScmWindowsServiceControl().GetBinaryPath(options.PostgresServiceName) != BuildPostgresServiceCommand(options))
                throw new IOException("SCM 数据库服务的实际启动命令与受控数据目录不一致，禁止重建。");
        });
        _localMaterials = localMaterials ?? new CompositeStartupReadinessProbe([
            new NodeCertificateStartupReadinessProbe(Local.CertificateThumbprint),
            new ConfirmationMaterialReadinessProbe(StateSource(Local),
                new CertificateStorePackageKeyUnwrapper(Local.NodeId,
                    new Exam.Infrastructure.Security.ServerCertificateSource(Local.CertificateThumbprint))),
            new PostgresWalArchiveReadinessProbe(Local.ConnectionString, options)]);
    }

    private async Task<(ServerDeploymentState State, bool Recovery, string System, string Database, int Timeline)?> ReadAsync(
        FailoverNodeDeployment node, CancellationToken token)
    {
        try
        {
            var source = StateSource(node);
            var state = await new PostgresFailoverStateStore(source).LoadAsync(_options.ServerId, token);
            if (state is null || state.Nodes.Count != _options.Nodes.Count || !state.Nodes.Keys.Order().SequenceEqual(_options.Nodes.Keys.Order()) ||
                state.ServerEpoch <= 0 || state.Version <= 0 || !Enum.IsDefined(state.ProtectionMode) ||
                !state.Nodes.TryGetValue(state.ActiveNodeId, out var active) || active.Role != DeploymentNodeRole.Primary)
                return null;
            await using var command = source.CreateCommand("""
                SELECT pg_is_in_recovery(),s.system_identifier::text,current_database(),p.timeline_id
                FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() p
                """);
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) return null;
            return (state, row.GetBoolean(0), row.GetString(1), row.GetString(2), row.GetInt32(3));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var now = _time.GetUtcNow();
            if (_recordProbeFailure is not null && (!_probeFailureTimes.TryGetValue(node.NodeId, out var prior) || now - prior >= TimeSpan.FromSeconds(30)))
            {
                _probeFailureTimes[node.NodeId] = now;
                try { _recordProbeFailure(node.NodeId, error); } catch { }
            }
            return null;
        }
    }

    private NpgsqlDataSource StateSource(FailoverNodeDeployment node) => _stateSources.GetOrAdd(node.NodeId,
        _ => new Lazy<NpgsqlDataSource>(() => ManagementProbeConnection.Create(node.ConnectionString, 5))).Value;

    public void Dispose()
    {
        _healthProbe.Dispose();
        _localResourceDigest.Dispose();
        foreach (var source in _stateSources.Values) if (source.IsValueCreated) source.Value.Dispose();
        _stateSources.Clear();
    }

    private string ConfirmationDirectory(FailoverNodeDeployment node, long epoch)
    {
        if (node.NodeId == Local.NodeId)
        {
            var selected = _readSelection();
            if (selected is not null && (selected.ServerId != _options.ServerId || selected.NodeId != node.NodeId))
                throw new IOException("确认目录选择与本机身份不一致。");
            if (selected?.Epoch == epoch)
                return PathBoundary.ResolveWithinRoot(node.ConfirmationDirectory, selected.ConfirmationDirectory,
                    "确认目录超出节点根目录。");
            // Reconciliation still verifies the previous epoch after the new selection is
            // durable. Subsequent promotions must not fall back to the initial head.
            var historical = PathBoundary.ResolveWithinRoot(node.ConfirmationDirectory,
                Path.Combine(node.ConfirmationDirectory, "epochs", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), node.NodeId),
                "历史确认目录超出节点根目录。");
            if (Directory.Exists(historical)) return historical;
        }
        return node.ConfirmationDirectory;
    }
    private FileConfirmationStore Confirmation(FailoverNodeDeployment node, long epoch) => new(ConfirmationDirectory(node, epoch));
    private ServiceConfiguration LocalConfiguration(long version = 1) => new(_options.ServerId, Local.NodeId,
        "127.0.0.1", 1, Local.ResourcesDirectory, true, version);

    private async Task<(bool Keys, bool Certificate, HighAvailabilityMaterialProof? Proof, IReadOnlyList<StartupCheckResult> Checks)> MaterialsAsync(FailoverNodeDeployment node, CancellationToken token)
    {
        if (node.NodeId == Local.NodeId)
        {
            var checks = await _localMaterials.RunAsync(LocalConfiguration(), token);
            var certificate = checks.Any(c => c.Name == "node-certificate-private-key" && c.Passed);
            return (checks.Count > 1 && checks.All(c => c.Passed || !c.BlocksWrites), certificate, null, checks);
        }
        try
        {
            var endpoint = PeerAddress(node, "replication/v1/readiness");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = PeerRequest(HttpMethod.Get, endpoint, node.NodeId);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 16384) return (false, false, null, []);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var memory = new MemoryStream(); var bytes = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
            { if (memory.Length + read > 16384) return (false, false, null, []); memory.Write(bytes, 0, read); }
            var proof = JsonSerializer.Deserialize<HighAvailabilityMaterialProof>(memory.ToArray(), JsonDefaults.Create());
            if (proof is null || proof.ServerId != _options.ServerId || proof.NodeId != node.NodeId)
                return (false, false, null, []);
            return (proof.KeysReady, proof.CertificateReady, proof, []);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return (false, false, null, []); }
    }

    public async Task<HighAvailabilityStatus> ObserveLocalAsync(CancellationToken token)
    {
        var local = await ReadAsync(Local, token);
        if (local is null)
            return new(_options.ServerId, Local.NodeId, null, null, false,
                [new("failover-state", false, "本机数据库主备状态不可读取。", true)]);
        return await ObserveLocalStateAsync(local.Value.State, local.Value.Recovery, local.Value.System, token);
    }

    private async Task<HighAvailabilityStatus> ObserveLocalStateAsync(ServerDeploymentState state, bool recovery,
        string system, CancellationToken token)
    {
        await RecoverLocalResourcesAsync(state, recovery, system, token);
        var materials = await MaterialsAsync(Local, token);
        var probe = _healthProbe;
        var health = await probe.ObserveAsync(Local.NodeId, state.Nodes[Local.NodeId].Role,
            materials.Keys, materials.Certificate, token);
        var nodes = state.Nodes.ToDictionary(p => p.Key, p => p.Value with
            { Reachable = false, ReplayLagBytes = long.MaxValue, KeyMaterialReady = false,
                CertificateReady = false, ObservedAt = default });
        nodes[Local.NodeId] = health;
        return new(_options.ServerId, Local.NodeId, state with { Nodes = nodes }, _time.GetUtcNow(), false,
            [new("failover-state", true, "已读取本机数据库角色与部署身份。", true), .. materials.Checks]);
    }

    public async Task<HighAvailabilityStatus> ObserveAsync(CancellationToken token)
    {
        var local = await ReadAsync(Local, token);
        var retired = new NodeRetirementStore(Local.ConfirmationDirectory).Read();
        if (local is { Recovery: false } && (retired is null || local.Value.State.ServerEpoch > retired.ActiveEpoch) &&
            IsSelectedIsolatedPrimary(local.Value.State))
        {
            // Isolation is an explicit durable takeover result, not an inference from a
            // timeout. Confirm this local authority without waiting for the isolated node's
            // business listener. Its independent health/authentication remains unverified.
            var observation = await ObserveLocalStateAsync(local.Value.State, false, local.Value.System, token);
            return observation with { Checks = [.. observation.Checks,
                new("synchronous-protection", false, "原主机已隔离；当前主机可靠保存，重建备机后再核对双节点保护。", false)] };
        }
        var otherNode = _options.Nodes.Values.SingleOrDefault(n => n.NodeId != Local.NodeId);
        var otherRead = otherNode is null ? null : ReadAsync(otherNode, token);
        var probe = _healthProbe;
        // Independent peer timeouts must not accumulate behind the local observation.
        var materialsTasks = _options.Nodes.Values.ToDictionary(n => n.NodeId, n => MaterialsAsync(n, token));
        var peerTasks = _options.Nodes.Values.Where(n => n.NodeId != Local.NodeId).ToDictionary(n => n.NodeId,
            n => probe.ObserveAsync(n.NodeId, DeploymentNodeRole.Isolated, false, false, token));
        await Task.WhenAll(otherRead is null ? Task.CompletedTask : otherRead,
            Task.WhenAll(materialsTasks.Values), Task.WhenAll(peerTasks.Values));
        var other = otherRead is null ? null : await otherRead;
        if (local is { } localState)
            await RecoverLocalResourcesAsync(localState.State, localState.Recovery, localState.System, token);
        var primaryCandidates = new[] { (Node: Local.NodeId, Observation: local), (Node: otherNode?.NodeId ?? "", Observation: other) }
            .Where(v => v.Observation is { Recovery: false } && v.Observation.Value.State.ActiveNodeId == v.Node &&
                v.Observation.Value.State.ServerEpoch >= (retired?.ActiveEpoch ?? 1) &&
                (v.Node != Local.NodeId || retired is null || v.Observation.Value.State.ServerEpoch > retired.ActiveEpoch))
            .Select(v => v.Observation)
            .OrderByDescending(v => v!.Value.State.ServerEpoch).ThenByDescending(v => v!.Value.State.Version).ToArray();
        var authoritative = primaryCandidates.FirstOrDefault() ?? (local is { Recovery: true } ? local : null);
        if (primaryCandidates.Length == 2 && primaryCandidates[0]!.Value.State.ServerEpoch == primaryCandidates[1]!.Value.State.ServerEpoch &&
            primaryCandidates[0]!.Value.State.ActiveNodeId != primaryCandidates[1]!.Value.State.ActiveNodeId)
            authoritative = null; // Conflicting primary identities at one epoch are never resolved by a GUI guess.
        if (authoritative is null)
            return new(_options.ServerId, Local.NodeId, null, null, false,
                [new("failover-state", false, retired is not null
                    ? "本机旧库已隔离，当前主机数据库连接暂不可用，尚未取得当前主备状态。"
                    : "实际数据库主备状态不可读取；代次与保护模式未核实。", true)]);
        var state = authoritative.Value.State;
        var nodes = new Dictionary<string, NodeState>();
        var archiveChecks = new List<StartupCheckResult>();
        foreach (var node in _options.Nodes.Values)
        {
            var materials = await materialsTasks[node.NodeId];
            archiveChecks.AddRange(materials.Checks.Where(c => c.Name == "wal-archiving"));
            var materialsVerified = node.NodeId == Local.NodeId || materials.Proof is { } proof &&
                proof.Epoch == state.ServerEpoch && _time.GetUtcNow() - proof.ObservedAt <= HighAvailabilityStatus.ObservationLifetime &&
                proof.ObservedAt <= _time.GetUtcNow().AddSeconds(5);
            var role = state.Nodes[node.NodeId].Role;
            // Observe local database health after the peer probes, keeping its timestamp current
            // when an offline peer consumes its connection timeout.
            var health = node.NodeId == Local.NodeId
                ? await probe.ObserveAsync(node.NodeId, role, materialsVerified && materials.Keys,
                    materialsVerified && materials.Certificate, token)
                : await peerTasks[node.NodeId];
            nodes[node.NodeId] = health with { Role = role,
                DatabaseInRecovery = health.Reachable ? health.DatabaseInRecovery : role != DeploymentNodeRole.Primary,
                KeyMaterialReady = materialsVerified && materials.Keys, CertificateReady = materialsVerified && materials.Certificate };
        }
        var verified = true;
        if (authoritative.Value.Recovery)
        {
            // The standby's independently persisted acknowledgement remains available after a whole-host failure.
            // This proves its recoverable boundary; manual isolation still precedes promotion.
            try { RequireTakeoverHead(state); }
            catch (Exception) { verified = false; }
        }
        var protection = false;
        if (verified && _options.Nodes.Count == 2)
        {
            try { await VerifyDualProtectionAsync(state, nodes, token); protection = true; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        var checks = new List<StartupCheckResult> {
            new("failover-state", verified, verified ? "主备状态与实际数据库身份已读取。" : "本机副本的持久确认尚未核实，不能接管。", true),
            new("synchronous-protection", protection, protection ? "双侧持久确认与复制材料已核实。" :
                "单节点运行：业务继续以本机可靠落盘为准，后台持续尝试补齐复制。", false) };
        checks.AddRange(archiveChecks);
        return new(_options.ServerId, Local.NodeId, state with { Nodes = nodes }, verified ? _time.GetUtcNow() : null, protection, checks);
    }

    private bool IsSelectedIsolatedPrimary(ServerDeploymentState state)
    {
        if (state.ActiveNodeId != Local.NodeId || state.ProtectionMode != ProtectionMode.EmergencySingleNode ||
            state.Nodes.Count != 2 || state.Nodes.Values.Any(node => node.NodeId != Local.NodeId && node.Role != DeploymentNodeRole.Isolated))
            return false;
        var selected = _readSelection();
        return selected is not null && selected.ServerId == state.ServerId && selected.NodeId == Local.NodeId &&
            selected.ActiveNodeId == Local.NodeId && selected.OtherNodeId == Other.NodeId && selected.Epoch == state.ServerEpoch &&
            selected.StateVersion == state.Version && selected.Mode == state.ProtectionMode &&
            state.Audit.Any(entry => entry.Action == $"F02:Takeover:{selected.RequestId}" && entry.NodeId == Local.NodeId &&
                entry.ServerEpoch == state.ServerEpoch);
    }

    private ConfirmationHead RequireTakeoverHead(ServerDeploymentState state)
    {
        var head = Confirmation(Local, state.ServerEpoch).Read();
        if (head is not { Confirmed: true } || head.Record.ServerId != state.ServerId ||
            head.Record.Epoch != state.ServerEpoch || head.Record.StateVersion > state.Version ||
            head.Record.PrimaryNodeId != state.ActiveNodeId || head.Record.StandbyNodeId != Local.NodeId ||
            head.Record.Mode != ProtectionMode.SynchronousDualNode)
            throw new IOException("本机缺少同身份、同代次的独立持久副本确认。");
        return head;
    }

    private async Task RecoverLocalResourcesAsync(ServerDeploymentState state, bool recovery, string system, CancellationToken token)
    {
        if (!recovery || !File.Exists(Local.ResourcesDirectory.TrimEnd(Path.DirectorySeparatorChar) + ".replica-swap.json")) return;
        using var guard = await Confirmation(Local, state.ServerEpoch).LockAsync(token);
        await ReplicaResourceTransfer.RecoverAsync(Local.ResourcesDirectory,
            $"{state.ServerId}/{Local.NodeId}/{state.ActiveNodeId}/{state.ServerEpoch}/{system}", async ct =>
            {
                var current = await ReadAsync(Local, ct);
                if (current is not { Recovery: true } || current.Value.System != system ||
                    current.Value.State.ServerEpoch != state.ServerEpoch || current.Value.State.ActiveNodeId != state.ActiveNodeId)
                    throw new IOException("资源交换恢复期间本机角色或权威身份变化。");
            }, token);
    }

    private async Task<ConfirmationHead?> ReadHeadAsync(FailoverNodeDeployment node, long epoch, CancellationToken token)
    {
        if (node.NodeId == Local.NodeId) return Confirmation(node, epoch).Read();
        return await new HttpConfirmationPeer(_http, PeerAddress(node, ""), _options.ServerId,
            node.NodeId, () => _options.PeerToken).ReadAsync(token);
    }

    private async Task<ConfirmationHead> RequireMatchingHeadsAsync(ServerDeploymentState state, string primaryId, string standbyId, CancellationToken token)
    {
        var primary = await ReadHeadAsync(_options.Nodes[primaryId], state.ServerEpoch, token);
        var standby = await ReadHeadAsync(_options.Nodes[standbyId], state.ServerEpoch, token);
        if (primary is not { Confirmed: true } || standby?.Covers(primary.Record) != true || primary.Record.ServerId != state.ServerId ||
            primary.Record.Epoch != state.ServerEpoch || primary.Record.StateVersion != state.Version ||
            primary.Record.PrimaryNodeId != primaryId || primary.Record.StandbyNodeId != standbyId ||
            primary.Record.Mode != ProtectionMode.SynchronousDualNode)
            throw new IOException("双侧最新持久确认尚未一致，当前按单节点运行。");
        return primary;
    }

    private async Task VerifyDualProtectionAsync(ServerDeploymentState state, IReadOnlyDictionary<string, NodeState> nodes, CancellationToken token)
    {
        var primary = _options.Nodes[state.ActiveNodeId]; var standby = _options.Nodes.Values.Single(n => n.NodeId != primary.NodeId);
        if (nodes.Values.Any(n => !n.Reachable || !n.KeyMaterialReady || !n.CertificateReady) ||
            nodes[primary.NodeId].DatabaseInRecovery || !nodes[standby.NodeId].DatabaseInRecovery)
            throw new IOException("实际节点复制或材料核对未通过。");
        var head = await RequireMatchingHeadsAsync(state, primary.NodeId, standby.NodeId, token);
        if (_localResourceDigest.Read() != head.Record.ResourcesDigest)
            throw new IOException("双节点资源未覆盖最新确认。");
        var source = StateSource(standby);
        await PostgresStandbyConfirmation.VerifyPositionAsync(source, head.Record, standby.NodeId, token,
            standby.ConnectionString, _options.PostgresBinDirectory);
        var active = StateSource(primary);
        await using var sync = active.CreateCommand("SELECT count(*) FROM pg_stat_replication WHERE application_name=$1 AND state='streaming'");
        sync.Parameters.AddWithValue(standby.NodeId);
        if ((long)(await sync.ExecuteScalarAsync(token) ?? 0L) != 1) throw new IOException("流复制未按配置节点生效。");
    }

    public async Task<FailoverExecutionPlan> PrepareAsync(ManagementOperation operation, HighAvailabilityRequest request,
        ServerDeploymentState before, ServerDeploymentState after, CancellationToken token)
    {
        var target = await ReadAsync(Local, token);
        if (operation.Kind != ManagementOperationKind.Rebuild && target is null)
            throw new IOException("本机数据库身份未核实。");
        var reference = operation.Kind == ManagementOperationKind.Rebuild ? await ReadAsync(_options.Nodes[before.ActiveNodeId], token) : target;
        if (reference is null) throw new IOException("操作数据源的实际身份未核实。");
        if (operation.Kind == ManagementOperationKind.Takeover)
        {
            if (!reference.Value.Recovery || !SameState(reference.Value.State, before)) throw new IOException("本机不是预期的备用库快照。");
            var head = RequireTakeoverHead(before);
            await using var source = ManagementProbeConnection.Create(Local.ConnectionString, 5);
            await PostgresStandbyConfirmation.VerifyPositionAsync(source, head.Record, Local.NodeId, token,
                Local.ConnectionString, _options.PostgresBinDirectory);
            if (FileConfirmationStore.ResourcesDigest(Local.ResourcesDirectory) != head.Record.ResourcesDigest)
                throw new IOException("本机资源未覆盖最新已确认边界。");
        }
        else if (operation.Kind == ManagementOperationKind.Reprotect)
        {
            if (reference.Value.Recovery || !before.Nodes.TryGetValue(request.TargetNodeId, out var standby) ||
                !standby.Reachable || !standby.DatabaseInRecovery || !standby.KeyMaterialReady || !standby.CertificateReady)
                throw new IOException("重建备库尚未追平或材料未核实。");
            var standbyIdentity = await ReadAsync(_options.Nodes[request.TargetNodeId], token);
            if (standbyIdentity is null || !standbyIdentity.Value.Recovery || standbyIdentity.Value.System != reference.Value.System ||
                standbyIdentity.Value.Database != reference.Value.Database || !SameState(standbyIdentity.Value.State, before) ||
                (await ReadHeadAsync(Other, before.ServerEpoch, token))?.Record.ResourcesDigest != FileConfirmationStore.ResourcesDigest(Local.ResourcesDirectory))
                throw new IOException("重建备库集群、状态或资源与当前主库不一致。");
        }
        else if (operation.Kind == ManagementOperationKind.Emergency && (reference.Value.Recovery || !SameState(reference.Value.State, before)))
            throw new IOException("本机不是当前活动主库。");
        else if (operation.Kind == ManagementOperationKind.Rebuild)
        {
            if (reference.Value.Recovery || !SameState(reference.Value.State, before)) throw new IOException("重建源不是当前活动主库。");
            ValidateRebuildPaths();
        }
        return new(operation.RequestId, operation.ContentHash, operation.Kind, before, after,
            reference.Value.System, reference.Value.Database, reference.Value.Timeline,
            operation.Kind == ManagementOperationKind.Rebuild
                ? await ReadSourceResourcesAsync(_options.Nodes[before.ActiveNodeId], before, reference.Value.System, token)
                : FileConfirmationStore.ResourcesDigest(Local.ResourcesDirectory));
    }

    public async Task ExecuteAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        if (plan.Kind == ManagementOperationKind.Takeover)
        {
            await _business.StopAsync(token);
            await PromoteConfirmedStandbyAsync(plan, token);
            await PublishAsync(plan, token); await SetSynchronousAsync(false, plan, token); await SelectAsync(plan, token);
        }
        else if (plan.Kind == ManagementOperationKind.Emergency)
        {
            // The running business host holds its confirmation directory for its lifetime.
            // Stop it before selecting a different protected directory, as for takeover.
            await _business.StopAsync(token);
            await PublishAsync(plan, token); await SetSynchronousAsync(false, plan, token); await SelectAsync(plan, token);
        }
        else if (plan.Kind == ManagementOperationKind.Rebuild)
        {
            var wasRunning = _business.Status == BusinessServiceStatus.Running;
            try { await _business.StopAsync(token); await PublishAsync(plan, token); await RebuildLocalAsync(plan, token); }
            catch (Exception failure)
            {
                // Only an actual read-only database belonging to this plan may resume its listener.
                // A writable old primary or a stopped/replaced database must remain stopped.
                if (wasRunning && _postgres.Status == BusinessServiceStatus.Running)
                {
                    try
                    {
                        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var actual = await ReadAsync(Local, recovery.Token);
                        if (actual is { Recovery: true } && actual.Value.System == plan.SystemIdentifier &&
                            actual.Value.Database == plan.DatabaseName && actual.Value.State.ActiveNodeId != Local.NodeId)
                            await _business.StartAsync(recovery.Token);
                    }
                    catch (Exception restartFailure)
                    { throw new AggregateException("重建失败，旧备机确认服务恢复也未完成。", failure, restartFailure); }
                }
                throw;
            }
        }
        else if (plan.Kind == ManagementOperationKind.Reprotect)
        { await PublishAsync(plan, token); await SetSynchronousAsync(true, plan, token); await ConfirmSynchronousAsync(plan, token); await SelectAsync(plan, token); }
        else throw new ArgumentException("主备计划类型无效。");
    }

    public async Task<bool> ReconcileAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        if (plan.Kind == ManagementOperationKind.Rebuild && HasRebuildCompletion(plan))
        {
            // A later authorized transition does not undo a completed historical rebuild.
            // Close only its journal; never reselect or restart its now obsolete role.
            foreach (var node in _options.Nodes.Values)
            {
                var current = await ReadAsync(node, token);
                if (current is { Recovery: false } && current.Value.State.ActiveNodeId == node.NodeId &&
                    current.Value.System == plan.SystemIdentifier && current.Value.Database == plan.DatabaseName &&
                    current.Value.State.Version > plan.After.Version && FailoverStateHistory.Covers(current.Value.State, plan.After))
                    return true;
            }
        }
        var active = _options.Nodes[plan.After.ActiveNodeId]; var actual = await ReadAsync(active, token);
        if (actual is null || actual.Value.Recovery || actual.Value.System != plan.SystemIdentifier || actual.Value.Database != plan.DatabaseName)
            return false;
        if (!SameState(actual.Value.State, plan.Before) && !SameState(actual.Value.State, plan.After)) return false;
        if (plan.Kind == ManagementOperationKind.Takeover)
        {
            if (plan.After.ActiveNodeId != Local.NodeId || actual.Value.Timeline <= plan.Timeline) return false;
            // A larger timeline is not proof that promotion retained the latest confirmed data.
            // Recheck the original, independent heads and actual lineage before publishing or selecting.
            await VerifyPromotedStandbyAsync(plan, actual.Value.Timeline, token);
        }
        if (plan.Kind == ManagementOperationKind.Takeover && SameState(actual.Value.State, plan.Before))
            await PublishAsync(plan, token); // Complete only the publication of this already-proven promotion.
        else if (!SameState(actual.Value.State, plan.After)) return false;
        if (plan.Kind is ManagementOperationKind.Takeover or ManagementOperationKind.Emergency)
        {
            await SetSynchronousAsync(false, plan, token); await SelectAsync(plan, token);
            if (plan.Kind == ManagementOperationKind.Takeover)
                await new ReadyCheckedBusinessHost(_business, _http, PeerAddress(Local, "health/ready"),
                    _options.ServerId, Local.NodeId, plan.After.ServerEpoch).StartAsync(token);
            return true;
        }
        if (plan.Kind == ManagementOperationKind.Reprotect)
        {
            await SetSynchronousAsync(true, plan, token); await ConfirmSynchronousAsync(plan, token); await SelectAsync(plan, token);
            var status = await ObserveAsync(token); return status.SynchronousProtectionVerified && status.State is { } state && SameState(state, plan.After);
        }
        if (plan.Kind == ManagementOperationKind.Rebuild)
        {
            if (ReadRebuildProgress(plan) is { Phase: "Installing" } progress)
                await InstallPreparedRebuildAsync(progress, token);
            if (!await VerifyRebuiltLocalAsync(plan, token)) return false;
            await SelectAsync(plan, token);
            await _business.StartAsync(token); // Finish the durable plan's non-active confirmation listener only.
            await RetireSupersededSlotsAsync(plan, token);
            return _business.Status == BusinessServiceStatus.Running;
        }
        return false;
    }

    private async Task VerifyPromotedStandbyAsync(FailoverExecutionPlan plan, int timeline, CancellationToken token)
    {
        var standby = Confirmation(Local, plan.Before.ServerEpoch);
        using var standbyGuard = standby.Lock();
        var head = RequireTakeoverHead(plan.Before);
        if (head.Record.SystemIdentifier != plan.SystemIdentifier || head.Record.DatabaseName != plan.DatabaseName ||
            head.Record.ResourcesDigest != plan.ResourcesDigest ||
            FileConfirmationStore.ResourcesDigest(Local.ResourcesDirectory) != head.Record.ResourcesDigest)
            throw new IOException("接管恢复的最新确认身份或本机资源与持久意图不一致。");

        await using var source = ManagementProbeConnection.Create(Local.ConnectionString, 10);
        await using (var command = source.CreateCommand("""
            SELECT pg_is_in_recovery(),s.system_identifier::text,current_database(),p.timeline_id,
                   pg_current_wal_flush_lsn()::text
            FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() p
            """))
        await using (var row = await command.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token) || row.GetBoolean(0) || row.GetString(1) != plan.SystemIdentifier ||
                row.GetString(2) != plan.DatabaseName || row.GetInt32(3) != timeline ||
                FileConfirmationStore.Lsn(row.GetString(4)) < FileConfirmationStore.Lsn(head.Record.FlushLsn))
                throw new IOException("提升后的实际数据库未覆盖最新持久确认边界。");
        }
        _ = await VerifiedTimelineContinuation.ReadAsync(source, head.Record, timeline,
            Local.ConnectionString, _options.PostgresBinDirectory, token);
        if (head.Record.Timeline != plan.Timeline)
            _ = await VerifiedTimelineContinuation.ReadAsync(source, head.Record with { Timeline = plan.Timeline }, timeline,
                Local.ConnectionString, _options.PostgresBinDirectory, token);
    }

    private async Task PublishAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        await using var source = ManagementProbeConnection.Create(_options.Nodes[plan.After.ActiveNodeId].ConnectionString, 10);
        var store = new PostgresFailoverStateStore(source); var actual = await store.LoadAsync(_options.ServerId, token);
        if (actual is not null && SameState(actual, plan.After)) return;
        if (actual is null || !SameState(actual, plan.Before)) throw new DBConcurrencyException("主备状态已变化，禁止执行原计划。");
        await store.SaveAsync(plan.After, token);
    }
    private static bool SameState(ServerDeploymentState actual, ServerDeploymentState expected) =>
        actual.ServerId == expected.ServerId && actual.ActiveNodeId == expected.ActiveNodeId && actual.ServerEpoch == expected.ServerEpoch &&
        actual.Version == expected.Version && actual.ProtectionMode == expected.ProtectionMode &&
        expected.Nodes.All(n => actual.Nodes.TryGetValue(n.Key, out var node) && node.Role == n.Value.Role) &&
        expected.Audit.All(a => actual.Audit.Any(b => b.Action == a.Action && b.ActorId == a.ActorId && b.Reason == a.Reason && b.ServerEpoch == a.ServerEpoch));

    private async Task PromoteConfirmedStandbyAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        using var replicaLock = Confirmation(Local, plan.Before.ServerEpoch).Lock();
        var head = RequireTakeoverHead(plan.Before);
        await using var source = ManagementProbeConnection.Create(Local.ConnectionString, 70);
        await PostgresStandbyConfirmation.VerifyPositionAsync(source, head.Record, Local.NodeId, token,
            Local.ConnectionString, _options.PostgresBinDirectory);
        await using var connection = await source.OpenConnectionAsync(token);
        await using var check = new NpgsqlCommand("SELECT pg_is_in_recovery() AND NOT pg_is_wal_replay_paused() AND pg_last_wal_receive_lsn() IS NOT NULL AND pg_last_wal_replay_lsn() >= pg_last_wal_receive_lsn()", connection);
        if ((bool)(await check.ExecuteScalarAsync(token) ?? false) is false) throw new IOException("提升前备库尚未完成实际重放。");
        await using var promote = new NpgsqlCommand("SELECT pg_promote(true,60)", connection) { CommandTimeout = 65 };
        if ((bool)(await promote.ExecuteScalarAsync(token) ?? false) is false) throw new IOException("备用库未在限定时间完成提升。");
    }

    private async Task SetSynchronousAsync(bool enabled, FailoverExecutionPlan plan, CancellationToken token)
    {
        var standbyId = _options.Nodes.Keys.Single(n => n != plan.After.ActiveNodeId);
        if (standbyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            throw new IOException("同步复制应用名须使用部署提供的 ASCII 节点标识。");
        var setting = "";
        await using var source = ManagementProbeConnection.Create(_options.Nodes[plan.After.ActiveNodeId].ConnectionString, 10);
        await using var format = source.CreateCommand("SELECT format('ALTER SYSTEM SET synchronous_standby_names = %L', $1)");
        format.Parameters.AddWithValue(setting);
        var statement = (string)(await format.ExecuteScalarAsync(token) ?? throw new IOException("复制配置语句未生成。"));
        await using var update = source.CreateCommand(statement); await update.ExecuteNonQueryAsync(token);
        await using var reload = source.CreateCommand("SELECT pg_reload_conf()");
        if ((bool)(await reload.ExecuteScalarAsync(token) ?? false) is false) throw new IOException("复制配置尚未重载。");
        var deadline = _time.GetUtcNow().AddSeconds(5);
        while (true)
        {
            await using var verify = source.CreateCommand("SHOW synchronous_standby_names");
            if ((string?)await verify.ExecuteScalarAsync(token) == setting) break;
            if (_time.GetUtcNow() >= deadline) throw new IOException("复制配置实际生效状态未核实。");
            await Task.Delay(50, token);
        }
    }

    private async Task SelectAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        // Reprotect never changes the live producer's directory in the same epoch.
        var directory = plan.Kind == ManagementOperationKind.Reprotect
            ? ConfirmationDirectory(Local, plan.After.ServerEpoch)
            : Path.Combine(Local.ConfirmationDirectory, "epochs", plan.After.ServerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), Local.NodeId);
        PathBoundary.ResolveWithinRoot(Local.ConfirmationDirectory, directory, "代次确认目录超出允许根目录。");
        await new FileFailoverDeploymentSelection(_options.SelectionPath).SaveAsync(new(_options.ServerId, Local.NodeId,
            plan.After.ActiveNodeId, Other.NodeId, plan.After.ServerEpoch, plan.After.Version, plan.After.ProtectionMode,
            plan.RequestId, directory), token);
    }
    private async Task ConfirmSynchronousAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        var primary = _options.Nodes[plan.After.ActiveNodeId]; var standby = _options.Nodes.Values.Single(n => n.NodeId != primary.NodeId);
        Directory.CreateDirectory(ConfirmationDirectory(primary, plan.After.ServerEpoch));
        var peer = new HttpConfirmationPeer(_http, PeerAddress(standby, ""), _options.ServerId, standby.NodeId, () => _options.PeerToken);
        await using var source = ManagementProbeConnection.Create(primary.ConnectionString, 10);
        using var confirmation = new PostgresBusinessConfirmation(source, Confirmation(primary, plan.After.ServerEpoch), _options.ServerId,
            primary.NodeId, standby.NodeId, plan.After.ServerEpoch, primary.ResourcesDirectory, peer,
            primary.ConnectionString, _options.PostgresBinDirectory);
        await confirmation.ConfirmReplicationAsync(token);
        var head = Confirmation(primary, plan.After.ServerEpoch).Read();
        var independent = await peer.ReadAsync(token);
        if (head is not { Confirmed: true } || independent?.Covers(head.Record) != true) throw new IOException("恢复保护尚无双侧持久确认。");
    }
    private Uri PeerAddress(FailoverNodeDeployment node, string path)
    {
        if (!Uri.TryCreate(node.BusinessHttpsUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new IOException("节点业务确认地址必须是受保护的 HTTPS 根地址。");
        return new(uri, path);
    }
    private HttpRequestMessage PeerRequest(HttpMethod method, Uri uri, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(_options.PeerToken) || _options.PeerToken.Length < 32) throw new IOException("节点确认凭据未配置。");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.PeerToken);
        request.Headers.Add("X-Exam-Server-Id", _options.ServerId); request.Headers.Add("X-Exam-Node-Id", nodeId); return request;
    }
    private sealed class NoRebuild : IPostgresNodeRebuilder
    { public Task PrepareRebuildAsync(string nodeId, string sourceNodeId, CancellationToken token) => throw new InvalidOperationException("提升路径不允许触发重建。"); }

    // The local rebuild implementation keeps old data quarantined and never merges it back into the new primary.
    private void ValidateRebuildPaths()
    {
        foreach (var path in new[] { _options.RebuildRoot, _options.PostgresDataDirectory, Local.ResourcesDirectory })
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) == Path.GetPathRoot(path)) throw new IOException("重建目标须为部署提供的非根绝对目录。");
            PathBoundary.RejectExistingReparsePoints(path, path);
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var paths = new[] { _options.RebuildRoot, _options.PostgresDataDirectory, Local.ResourcesDirectory }.Select(Path.GetFullPath).ToArray();
        for (var i = 0; i < paths.Length; i++) for (var j = 0; j < paths.Length; j++)
            if (i != j && (paths[i].Equals(paths[j], comparison) || paths[i].StartsWith(paths[j] + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("重建暂存、数据库与资源目录须互相独立。");
        _verifyDeployment();
    }

    public static string BuildPostgresServiceCommand(HighAvailabilityManagementOptions options)
    {
        if (!Path.IsPathFullyQualified(options.PostgresBinDirectory) || !Path.IsPathFullyQualified(options.PostgresDataDirectory) ||
            !Path.IsPathFullyQualified(options.PostgresConfigurationPath)) throw new IOException("数据库服务须配置绝对工具、数据和独立配置路径。");
        foreach (var path in new[] { options.PostgresBinDirectory, options.PostgresDataDirectory, options.PostgresConfigurationPath })
        { if (path.IndexOfAny(['\"', '\r', '\n']) >= 0) throw new IOException("服务部署路径含无效字符。"); PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(path)!, path); }
        var settings = new NpgsqlConnectionStringBuilder(options.Nodes[options.LocalNodeId].ConnectionString);
        if (settings.Host is not ("127.0.0.1" or "::1" or "localhost") || settings.Port is < 1 or > 65535)
            throw new IOException("受控数据库服务必须与固定本机数据库端口一致。");
        _ = new WindowsServiceBusinessHost(options.PostgresServiceName);
        var config = Path.GetFullPath(options.PostgresConfigurationPath); var data = Path.GetFullPath(options.PostgresDataDirectory);
        var relative = Path.GetRelativePath(data, config);
        if (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative))
            throw new IOException("受控配置必须位于被替换数据库目录之外。");
        var arguments = string.Join(' ', BuildPostgresRuntimeArguments(options).Select(PostgresWalArchiving.QuoteArgument));
        return $"{PostgresWalArchiving.QuoteArgument(Path.Combine(Path.GetFullPath(options.PostgresBinDirectory), "pg_ctl.exe"))} runservice -N {PostgresWalArchiving.QuoteArgument(options.PostgresServiceName)} " +
            $"-D {PostgresWalArchiving.QuoteArgument(data)} -w -t 60 -o {PostgresWalArchiving.QuoteArgument(arguments)}";
    }

    // Shared by the SCM command and native regression tests: no alternate test-only settings.
    public static IReadOnlyList<string> BuildPostgresRuntimeArguments(HighAvailabilityManagementOptions options)
    {
        if (options.WalArchiveTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "WAL 归档切换周期必须为正数秒。");
        var archiveCommand = PostgresWalArchiving.BuildCommand(options);
        var settings = new NpgsqlConnectionStringBuilder(options.Nodes[options.LocalNodeId].ConnectionString);
        List<string> arguments = ["-c", "config_file=" + Path.GetFullPath(options.PostgresConfigurationPath).Replace('\\', '/'),
            "-c", "data_directory=" + Path.GetFullPath(options.PostgresDataDirectory).Replace('\\', '/')];
        if (!string.IsNullOrWhiteSpace(options.PostgresHbaPath))
        {
            if (!Path.IsPathFullyQualified(options.PostgresHbaPath) || options.PostgresHbaPath.IndexOfAny(['"', '\r', '\n']) >= 0)
                throw new IOException("数据库认证配置须使用部署提供的绝对路径。");
            var hba = Path.GetFullPath(options.PostgresHbaPath);
            PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(hba)!, hba);
            arguments.AddRange(["-c", "hba_file=" + hba.Replace('\\', '/')]);
        }
        arguments.AddRange(["-c", $"port={settings.Port}", "-c", "archive_mode=on", "-c", "archive_library=",
            "-c", "archive_command=" + archiveCommand, "-c", $"archive_timeout={options.WalArchiveTimeoutSeconds}s",
            "-c", "fsync=on", "-c", "full_page_writes=on",
            "-c", "shared_preload_libraries=", "-c", "session_preload_libraries=", "-c", "local_preload_libraries=",
            "-c", "restore_command=exit 1", "-c", "archive_cleanup_command=", "-c", "recovery_end_command="]);
        return arguments;
    }

    private string RebuildSlot(FailoverExecutionPlan plan) => PathBoundary.ResolveWithinRoot(_options.RebuildRoot,
        Path.Combine(_options.RebuildRoot, plan.ContentHash), "重建工作槽超出允许根目录。");
    private async Task RebuildLocalAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        ValidateRebuildPaths(); var slot = RebuildSlot(plan);
        if (Directory.Exists(slot)) throw new IOException("原重建工作槽已存在，须查询原请求，禁止重新生成或覆盖。");
        Directory.CreateDirectory(slot);
        var progress = new RebuildProgress(plan, "Preparing",
            DirectoryIdentity(_options.PostgresDataDirectory), DirectoryIdentity(Local.ResourcesDirectory), null, null);
        await SaveRebuildProgressAsync(progress, token);
        var source = _options.Nodes[plan.After.ActiveNodeId];
        var connections = _options.Nodes.ToDictionary(p => p.Key, p => new PostgresNodeConnection(p.Key, p.Value.ConnectionString)
            { ReplicationConnectionString = p.Value.ReplicationConnectionString });
        var replicationSlot = await CreateOwnedRebuildSlotAsync(plan, token);
        await new StagedPostgresNodeRebuilder(new(_options.PostgresBinDirectory, slot, connections, TimeSpan.FromMinutes(60))
            { PrecreatedReplicationSlot = replicationSlot }, _processes)
            .PrepareRebuildAsync(Local.NodeId, source.NodeId, token);
        var database = Path.Combine(slot, Local.NodeId);
        var verification = await _processes.RunAsync(Path.Combine(_options.PostgresBinDirectory, OperatingSystem.IsWindows() ? "pg_verifybackup.exe" : "pg_verifybackup"),
            [database], new Dictionary<string, string>(), TimeSpan.FromMinutes(10), token);
        if (verification.ExitCode != 0) throw new IOException("重建基础备份未通过实际文件校验。");
        RejectLinkedTree(database);
        if (File.Exists(Path.Combine(database, "tablespace_map")) && new FileInfo(Path.Combine(database, "tablespace_map")).Length > 0)
            throw new IOException("重建备份含外部表空间，须按独立受控部署处理，禁止自动启用。");
        var resources = Path.Combine(slot, "resources");
        Directory.CreateDirectory(resources);
        if (plan.ResourcesDigest is null) throw new IOException("重建计划缺少资源版本。");
        using (var request = ResourceSourceRequest(source, "snapshot", plan.After, plan.SystemIdentifier))
        {
            request.Headers.Add("X-Exam-Resources-Digest", plan.ResourcesDigest);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/zip" ||
                response.Content.Headers.ContentLength > ReplicaResourceTransfer.MaximumBytes ||
                !response.Headers.TryGetValues("X-Exam-Resources-Digest", out var versions) ||
                versions.SingleOrDefault() != plan.ResourcesDigest) throw new IOException("活动节点资源快照响应无效。");
            await using var body = await response.Content.ReadAsStreamAsync(token);
            await ReplicaResourceTransfer.ReceiveAsync(body, resources, plan.ResourcesDigest, async ct =>
            {
                if (await ReadSourceResourcesAsync(source, plan.After, plan.SystemIdentifier, ct) != plan.ResourcesDigest)
                    throw new IOException("资源源节点或版本发生变化，禁止启用重建结果。");
            }, token, plan.ContentHash);
        }
        progress = progress with { Phase = "Prepared", DatabaseIdentity = DirectoryIdentity(database), ResourcesIdentity = DirectoryIdentity(resources) };
        await SaveRebuildProgressAsync(progress, token);
        await InstallPreparedRebuildAsync(progress, token);
    }
    private HttpRequestMessage ResourceSourceRequest(FailoverNodeDeployment node, string kind,
        ServerDeploymentState state, string systemIdentifier)
    {
        var request = PeerRequest(HttpMethod.Get, PeerAddress(node, "replication/v1/resources/" + kind), node.NodeId);
        request.Headers.Add("X-Exam-Primary-Node-Id", state.ActiveNodeId);
        request.Headers.Add("X-Exam-Epoch", state.ServerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Exam-System-Identifier", systemIdentifier);
        return request;
    }

    private async Task<string> ReadSourceResourcesAsync(FailoverNodeDeployment node, ServerDeploymentState state,
        string systemIdentifier, CancellationToken token)
    {
        using var request = ResourceSourceRequest(node, "source", state, systemIdentifier);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var proof = await ReplicaResourceTransfer.ReadStateAsync(body, token);
        if (proof.ServerId != state.ServerId || proof.NodeId != state.ActiveNodeId || proof.NodeId != node.NodeId ||
            proof.InRecovery || proof.ActiveNodeId != proof.NodeId || proof.Epoch != state.ServerEpoch ||
            proof.SystemIdentifier != systemIdentifier || proof.ResourcesDigest.Length != 64 ||
            proof.ResourcesDigest.Any(c => !Uri.IsHexDigit(c))) throw new IOException("活动节点资源身份无效。");
        return proof.ResourcesDigest;
    }

    private static void RejectLinkedTree(string root)
    {
        PathBoundary.RejectExistingReparsePoints(root, root);
        var directories = new Queue<string>(); directories.Enqueue(root);
        while (directories.TryDequeue(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            { PathBoundary.RejectExistingReparsePoints(root, path); if (Directory.Exists(path)) directories.Enqueue(path); }
    }
    internal static void PreserveRebuildAccess(string staged, string target)
    {
        if (OperatingSystem.IsWindows() && Directory.Exists(target))
        {
            // A staged backup inherits the maintenance directory's permissions. Preserve
            // the installed data/resource ACL before moving it, including the database
            // service's inheritable write permission and existing resource readers.
            var installed = new DirectoryInfo(target).GetAccessControl(AccessControlSections.Access);
            var access = new DirectorySecurity();
            access.SetSecurityDescriptorBinaryForm(installed.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            new DirectoryInfo(staged).SetAccessControl(access);
        }
    }
    private async Task WriteRebuildMarkerAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        var marker = Path.Combine(RebuildSlot(plan), "rebuild-complete.json");
        var previous = DurableJsonFile.Read<FailoverExecutionPlan>(marker);
        if (previous is not null && !JsonSerializer.SerializeToUtf8Bytes(previous, JsonDefaults.Create()).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan, JsonDefaults.Create())))
            throw new IOException("重建完成记录已绑定其他计划。");
        await DurableJsonFile.WriteAsync(marker, plan, token);
    }
    private async Task<bool> VerifyRebuiltLocalAsync(FailoverExecutionPlan plan, CancellationToken token, bool requireMarker = true)
    {
        if (requireMarker)
        {
            if (!HasRebuildCompletion(plan)) return false;
        }
        var actual = await ReadAsync(Local, token);
        return actual is { Recovery: true } && actual.Value.System == plan.SystemIdentifier && actual.Value.Database == plan.DatabaseName &&
            FailoverStateHistory.Covers(actual.Value.State, plan.After) &&
            (requireMarker && actual.Value.State.Version > plan.After.Version ||
                _localResourceDigest.Read() == plan.ResourcesDigest);
    }
}
