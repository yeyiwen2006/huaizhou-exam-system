namespace Exam.Contracts.HighAvailability;

public enum ProtectionMode
{
    SynchronousDualNode,
    EmergencySingleNode,
    Recovering
}

public enum DeploymentNodeRole
{
    Primary,
    Standby,
    Isolated,
    Rebuilding
}

public sealed record NodeState(
    string NodeId,
    DeploymentNodeRole Role,
    bool Reachable,
    bool DatabaseInRecovery,
    long ReplayLagBytes,
    bool KeyMaterialReady,
    bool CertificateReady,
    DateTimeOffset ObservedAt)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReplicationStreaming { get; init; }
}

public sealed record ServerDeploymentState(
    string ServerId,
    string ActiveNodeId,
    long ServerEpoch,
    ProtectionMode ProtectionMode,
    IReadOnlyDictionary<string, NodeState> Nodes,
    long Version,
    IReadOnlyList<FailoverAuditEntry> Audit);

public sealed record IsolationEvidence(
    string IsolatedNodeId,
    bool HostPoweredOff,
    bool BusinessNetworkIsolated,
    string ConfirmedBy,
    string Reason)
{
    public bool IsSufficient => HostPoweredOff || BusinessNetworkIsolated;
}

public sealed record TakeoverRequest(
    string RequestId,
    string TargetNodeId,
    long ExpectedEpoch,
    IsolationEvidence Isolation);

public sealed record EmergencyModeRequest(string RequestId, string ActorId, string Reason, long ExpectedEpoch);

public sealed record RebuildNodeRequest(string RequestId, string NodeId, string ActorId, long ExpectedEpoch);

public sealed record CompleteRebuildRequest(string RequestId, string NodeId, string ActorId, long ExpectedEpoch);

public sealed record FailoverAuditEntry(
    string Action,
    string ActorId,
    string Reason,
    string NodeId,
    long ServerEpoch,
    DateTimeOffset OccurredAt);
