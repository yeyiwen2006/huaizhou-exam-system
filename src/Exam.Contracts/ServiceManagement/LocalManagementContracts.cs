using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using System.Text.Json.Serialization;

namespace Exam.Contracts.ServiceManagement;

public static class ManagementHeaders
{
    public const string ServerId = "X-Exam-Server-Id";
    public const string NodeId = "X-Exam-Node-Id";
}

public sealed record ManagementEnvelope<T>(
    string ServerId, string NodeId, long? ServerEpoch, bool EpochVerified,
    DateTimeOffset? EpochVerifiedAt, string CorrelationId, T? Result, ApiError? Error);

public sealed record ManagementServiceRequest(string RequestId, string ServerId, string NodeId);
public sealed record ManagementConfigurationRequest(
    string RequestId, string ServerId, string NodeId, long ExpectedVersion, ServiceConfiguration Configuration);

public enum ManagementOperationKind { Start, Stop, Configure, Checks, Backup, Restore, Takeover, Emergency, Rebuild, Reprotect }
public enum ManagementOperationPhase { Accepted, Completed, Failed, NeedsVerification, Staged }
public enum BusinessReadiness { Unknown, NotReady, Ready }
public enum RecoveryActivationPhase { SelectionPending, Selected, StartingBusiness, BusinessReady, RollbackPending, RolledBack }

// A process being Running is not evidence of business readiness.
public sealed record ManagementStatus(
    BusinessServiceStatus ProcessStatus, BusinessReadiness Readiness,
    ServiceConfiguration? Configuration, IReadOnlyList<StartupCheckResult> Checks);

public sealed record ManagementRestoreOutcome(
    string BackupId, bool IsolationStaged, bool RecoveryVerified, bool BusinessReady);

/// <summary>Bounded, non-sensitive projection of the protected local operation journal.</summary>
public sealed record ManagementAuditEntry(string RequestId, ManagementOperationKind Kind,
    ManagementOperationPhase Phase, DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt, string? ErrorCode);

public sealed record ProtectedRuntimeLogEntry(DateTimeOffset Timestamp, string Source, string Level,
    string Category, int EventId);

public sealed record ManagementOperation(
    string RequestId, string ServerId, string NodeId, string ActorId,
    ManagementOperationKind Kind, string ContentHash, ManagementOperationPhase Phase,
    DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt,
    ManagementStatus? Status = null, ServiceConfiguration? Configuration = null,
    IReadOnlyList<StartupCheckResult>? Checks = null, BackupResult? Backup = null,
    ManagementRestoreOutcome? Restore = null, ApiError? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecoveryWorkingDirectory = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecoveryActivationPhase? RecoveryActivation = null,
    HighAvailabilityStatus? HighAvailability = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FailoverExecutionPlan? FailoverPlan = null);
