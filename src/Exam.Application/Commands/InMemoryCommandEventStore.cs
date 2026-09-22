using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Domain.Commands;

namespace Exam.Application.Commands;

/// <summary>
/// Thread-safe executable reference adapter. It is intentionally not a replacement for the
/// PostgreSQL store required in production.
/// </summary>
public sealed class InMemoryCommandEventStore : ICommandEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PersistentCommandEvent> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _createRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string EventId, CommandAcknowledgement Acknowledgement)> _ackRequests =
        new(StringComparer.Ordinal);

    public ValueTask<PersistentCommandEvent?> FindByCreateRequestAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_createRequests.TryGetValue(requestId, out var eventId))
            {
                return ValueTask.FromResult<PersistentCommandEvent?>(_events[eventId]);
            }

            return ValueTask.FromResult<PersistentCommandEvent?>(null);
        }
    }

    public ValueTask<CommandAcknowledgement?> FindAcknowledgementByRequestAsync(
        string requestId,
        string eventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _ackRequests.TryGetValue(requestId, out var stored)
                    && string.Equals(stored.EventId, eventId, StringComparison.Ordinal)
                    ? stored.Acknowledgement
                    : null);
        }
    }

    public ValueTask<CommandCreatePersistenceResult> CreateOrGetAsync(
        string requestId,
        PersistentCommandEvent commandEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_createRequests.TryGetValue(requestId, out var replayEventId))
            {
                return ValueTask.FromResult(new CommandCreatePersistenceResult(
                    CommandCreatePersistenceStatus.RequestReplay,
                    _events[replayEventId]));
            }

            if (_events.TryGetValue(commandEvent.EventId, out var conflicting))
            {
                return ValueTask.FromResult(new CommandCreatePersistenceResult(
                    CommandCreatePersistenceStatus.EventIdConflict,
                    conflicting));
            }

            _events.Add(commandEvent.EventId, commandEvent);
            _createRequests.Add(requestId, commandEvent.EventId);
            return ValueTask.FromResult(new CommandCreatePersistenceResult(
                CommandCreatePersistenceStatus.Created,
                commandEvent));
        }
    }

    public ValueTask<PersistentCommandEvent?> FindAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _events.TryGetValue(eventId, out var commandEvent);
            return ValueTask.FromResult(commandEvent);
        }
    }

    public ValueTask<IReadOnlyList<PersistentCommandEvent>> ClaimDueForDeviceAsync(
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        DateTimeOffset dueAtOrBefore,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var due = _events.Values
                .Where(commandEvent =>
                    commandEvent.NextDeliveryAt <= dueAtOrBefore &&
                    string.Equals(commandEvent.ParticipationId, participationId, StringComparison.Ordinal) &&
                    commandEvent.EntryVersion == entryVersion &&
                    string.Equals(commandEvent.TargetDeviceId, deviceId, StringComparison.Ordinal) &&
                    commandEvent.TargetAuthorizationVersion == authorizationVersion &&
                    commandEvent.Acknowledgement?.ProcessingResult is not
                        (CommandProcessingResult.Applied or CommandProcessingResult.RejectedAsStale))
                .OrderBy(commandEvent => commandEvent.CreatedAt)
                .ThenBy(commandEvent => commandEvent.EventId, StringComparer.Ordinal)
                .ToArray();
            var result = new PersistentCommandEvent[due.Length];
            for (var index = 0; index < due.Length; index++)
            {
                var leased = due[index] with { NextDeliveryAt = dueAtOrBefore.AddSeconds(30) };
                _events[leased.EventId] = leased;
                result[index] = leased;
            }

            return ValueTask.FromResult<IReadOnlyList<PersistentCommandEvent>>(result);
        }
    }

    public ValueTask<PersistentCommandEvent?> RecordDeliveryAttemptAsync(
        string eventId,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextDeliveryAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var commandEvent) ||
                commandEvent.Acknowledgement?.ProcessingResult is
                    CommandProcessingResult.Applied or CommandProcessingResult.RejectedAsStale)
            {
                return ValueTask.FromResult<PersistentCommandEvent?>(null);
            }

            var updated = commandEvent with
            {
                DeliveryAttempts = commandEvent.DeliveryAttempts + 1,
                NextDeliveryAt = nextDeliveryAt
            };
            _events[eventId] = updated;
            return ValueTask.FromResult<PersistentCommandEvent?>(updated);
        }
    }

    public ValueTask<CommandAckPersistenceResult> AcknowledgeAsync(
        string requestId,
        string eventId,
        CommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_ackRequests.TryGetValue(requestId, out var replay))
            {
                if (!string.Equals(replay.EventId, eventId, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(new CommandAckPersistenceResult(
                        CommandAckPersistenceStatus.RequestIdConflict,
                        null));
                }

                return ValueTask.FromResult(new CommandAckPersistenceResult(
                    CommandAckPersistenceStatus.RequestReplay,
                    replay.Acknowledgement));
            }

            if (!_events.TryGetValue(eventId, out var commandEvent))
            {
                return ValueTask.FromResult(new CommandAckPersistenceResult(
                    CommandAckPersistenceStatus.EventNotFound,
                    null));
            }

            if (commandEvent.Acknowledgement?.ProcessingResult is
                CommandProcessingResult.Applied or CommandProcessingResult.RejectedAsStale)
            {
                _ackRequests.Add(requestId, (eventId, commandEvent.Acknowledgement));
                return ValueTask.FromResult(new CommandAckPersistenceResult(
                    CommandAckPersistenceStatus.RequestReplay,
                    commandEvent.Acknowledgement));
            }

            _ackRequests.Add(requestId, (eventId, acknowledgement));
            _events[eventId] = commandEvent with { Acknowledgement = acknowledgement };
            return ValueTask.FromResult(new CommandAckPersistenceResult(
                CommandAckPersistenceStatus.Acknowledged,
                acknowledgement));
        }
    }
}
