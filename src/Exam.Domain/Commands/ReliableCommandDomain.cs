using Exam.Contracts.Commands;
using Exam.Contracts.Common;

namespace Exam.Domain.Commands;

public sealed record PersistentCommandEvent(
    string EventId,
    string ExamId,
    string? ParticipationId,
    long EntryVersion,
    CommandEventType EventType,
    long StateVersion,
    string? TargetDeviceId,
    long? TargetAuthorizationVersion,
    long? ResourceVersion,
    string? ProtectedUnlockReference,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextDeliveryAt,
    int DeliveryAttempts,
    CommandAcknowledgement? Acknowledgement);

public enum CommandCreatePersistenceStatus
{
    Created,
    RequestReplay,
    EventIdConflict
}

public sealed record CommandCreatePersistenceResult(
    CommandCreatePersistenceStatus Status,
    PersistentCommandEvent Event);

public enum CommandAckPersistenceStatus
{
    Acknowledged,
    RequestReplay,
    RequestIdConflict,
    EventNotFound
}

public sealed record CommandAckPersistenceResult(
    CommandAckPersistenceStatus Status,
    CommandAcknowledgement? Acknowledgement);

/// <summary>
/// Persistence boundary for reliable events. A production adapter must persist event creation,
/// delivery state and acknowledgement atomically before returning from each operation.
/// </summary>
public interface ICommandEventStore
{
    ValueTask<PersistentCommandEvent?> FindByCreateRequestAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    ValueTask<CommandAcknowledgement?> FindAcknowledgementByRequestAsync(
        string requestId,
        string eventId,
        CancellationToken cancellationToken = default);

    ValueTask<CommandCreatePersistenceResult> CreateOrGetAsync(
        string requestId,
        PersistentCommandEvent commandEvent,
        CancellationToken cancellationToken = default);

    ValueTask<PersistentCommandEvent?> FindAsync(
        string eventId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PersistentCommandEvent>> ListUnacknowledgedAsync(
        DateTimeOffset dueAtOrBefore,
        CancellationToken cancellationToken = default);

    ValueTask<PersistentCommandEvent?> RecordDeliveryAttemptAsync(
        string eventId,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextDeliveryAt,
        CancellationToken cancellationToken = default);

    ValueTask<CommandAckPersistenceResult> AcknowledgeAsync(
        string requestId,
        string eventId,
        CommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);
}

public static class ReliableCommandPolicy
{
    public static TimeSpan RetryDelay(int completedAttempts)
    {
        var exponent = Math.Clamp(completedAttempts - 1, 0, 3);
        return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, exponent), 10));
    }

    public static string? Validate(CreateCommandEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.EventId) ||
            string.IsNullOrWhiteSpace(request.ExamId) ||
            request.EntryVersion < 0 ||
            request.StateVersion < 0)
        {
            return "指令标识、考试标识和版本必须有效。";
        }

        var targeted = request.EventType != CommandEventType.CollectionCompleted;
        if (targeted &&
            (string.IsNullOrWhiteSpace(request.ParticipationId) ||
             string.IsNullOrWhiteSpace(request.TargetDeviceId) ||
             request.TargetAuthorizationVersion is null))
        {
            return "面向学生端的指令必须指定参试记录、设备和授权版本。";
        }

        if (request.EventType == CommandEventType.Start &&
            string.IsNullOrWhiteSpace(request.ProtectedUnlockReference))
        {
            return "开始指令必须引用受保护的解锁材料。";
        }

        if (request.EventType != CommandEventType.Start &&
            request.ProtectedUnlockReference is not null)
        {
            return "只有开始指令可以携带受保护的解锁材料引用。";
        }

        return null;
    }
}
