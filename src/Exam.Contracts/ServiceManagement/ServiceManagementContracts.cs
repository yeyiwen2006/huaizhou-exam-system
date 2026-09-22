namespace Exam.Contracts.ServiceManagement;

public enum BusinessServiceStatus
{
    Stopped,
    Starting,
    Recovering,
    Running,
    Stopping,
    Faulted
}

public sealed record ServiceConfiguration(
    string ServerId,
    string NodeId,
    string BindAddress,
    int HttpsPort,
    string DataDirectory,
    bool HighAvailabilityEnabled,
    long Version);

public sealed record StartupCheckResult(
    string Name,
    bool Passed,
    string Message,
    bool BlocksWrites);

public sealed record ServiceStatusSnapshot(
    BusinessServiceStatus Status,
    ServiceConfiguration Configuration,
    IReadOnlyList<StartupCheckResult> Checks,
    DateTimeOffset UpdatedAt,
    string? LastError);

public sealed record BackupRequest(string RequestId, string DestinationDirectory, string ActorId);

public sealed record BackupResult(
    string BackupId,
    string DestinationDirectory,
    DateTimeOffset CompletedAt,
    bool IncludesDatabase,
    bool IncludesProtectedConfiguration,
    bool IncludesVersionedResources);

public sealed record RestoreRequest(string RequestId, string BackupId, string SourceDirectory, string ActorId);

public sealed record RestoreResult(string BackupId, DateTimeOffset CompletedAt, IReadOnlyList<StartupCheckResult> Checks);
