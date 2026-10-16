using System.Text.Json.Serialization;

namespace Exam.Contracts.ServiceManagement;

/// <summary>Read-only maintenance evidence. It contains no paths, credentials, roster or exam contents.</summary>
public sealed record BackupMaintenanceStatus
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public BackupMaintenancePhase BackupPhase { get; init; }
    public BackupMaintenancePhase CleanupPhase { get; init; }
    public DateTimeOffset? LastSuccessfulBackupAt { get; init; }
    public DateTimeOffset? NextBackupAttemptAt { get; init; }
    public long? AvailableBytes { get; init; }
    public long? BackupBytes { get; init; }
    public long? ArchiveBytes { get; init; }
    public long? WalBytes { get; init; }
    public long? ReplicationRetainedBytes { get; init; }
    public long MinimumAvailableBytes { get; init; }
    public int RetentionDays { get; init; } = 7;
    public int IncompleteBackups { get; init; }
    public bool StandbyConnected { get; init; }
    public int RemovedBackups { get; init; }
    public int RemovedWal { get; init; }
    public long ReclaimedBytes { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    [JsonIgnore] public bool HasWarning => Warnings.Count > 0;
}

public enum BackupMaintenancePhase { Disabled, Waiting, Running, Completed, Deferred, Failed }
