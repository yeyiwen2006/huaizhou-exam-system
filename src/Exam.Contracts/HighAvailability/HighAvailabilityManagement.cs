using Exam.Contracts.ServiceManagement;

namespace Exam.Contracts.HighAvailability;

// These observations are produced by the management host, never by GUI checkboxes.
public sealed record HighAvailabilityStatus(string ServerId, string NodeId,
    ServerDeploymentState? State, DateTimeOffset? VerifiedAt, bool SynchronousProtectionVerified,
    IReadOnlyList<StartupCheckResult> Checks)
{
    // The database mode is retained for compatibility with existing journals.
    // Only current independent acknowledgements can advertise two durable copies.
    public ProtectionMode? EffectiveProtectionMode => State is null ? null :
        SynchronousProtectionVerified ? ProtectionMode.SynchronousDualNode : ProtectionMode.EmergencySingleNode;
}

public sealed record HighAvailabilityMaterialProof(string ServerId, string NodeId, long Epoch,
    bool KeysReady, bool CertificateReady, DateTimeOffset ObservedAt);

public sealed record HighAvailabilityRequest(string RequestId, string ServerId, string NodeId,
    string ActorId, long ExpectedEpoch, long ExpectedVersion, string TargetNodeId, string Reason,
    IsolationEvidence? Isolation = null, bool ConfirmDestructiveRebuild = false);

// Protected journal checkpoint, recorded before promotion, configuration or data-directory effects.
// No connection strings, certificate material or operator tokens belong here.
public sealed record FailoverExecutionPlan(string RequestId, string ContentHash,
    ManagementOperationKind Kind, ServerDeploymentState Before, ServerDeploymentState After,
    string SystemIdentifier, string DatabaseName, int Timeline, string? ResourcesDigest);
