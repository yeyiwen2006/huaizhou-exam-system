using Exam.Contracts.Common;

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

public enum ManagementOperationKind { Start, Stop, Configure, Checks, Backup, Restore }
public enum ManagementOperationPhase { Accepted, Completed, Failed, NeedsVerification, Staged }
public enum BusinessReadiness { Unknown, NotReady, Ready }

// A process being Running is not evidence of business readiness.
public sealed record ManagementStatus(
    BusinessServiceStatus ProcessStatus, BusinessReadiness Readiness,
    ServiceConfiguration? Configuration, IReadOnlyList<StartupCheckResult> Checks);

public sealed record ManagementRestoreOutcome(
    string BackupId, bool IsolationStaged, bool RecoveryVerified, bool BusinessReady);

public sealed record ManagementOperation(
    string RequestId, string ServerId, string NodeId, string ActorId,
    ManagementOperationKind Kind, string ContentHash, ManagementOperationPhase Phase,
    DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt,
    ManagementStatus? Status = null, ServiceConfiguration? Configuration = null,
    IReadOnlyList<StartupCheckResult>? Checks = null, BackupResult? Backup = null,
    ManagementRestoreOutcome? Restore = null, ApiError? Error = null);
