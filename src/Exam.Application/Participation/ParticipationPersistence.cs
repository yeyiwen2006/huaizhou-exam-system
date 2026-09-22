using Exam.Domain.Participation;

namespace Exam.Application.Participation;

public interface IParticipationPersistence
{
    ValueTask<T> ExecuteTransactionAsync<T>(
        Func<IParticipationTransaction, T> operation,
        CancellationToken cancellationToken = default);
}

public interface IParticipationTransaction
{
    StudentSession? FindSession(string sessionId);

    EntryPreparation? FindPreparation(string preparationId);

    ParticipationRecord? FindParticipation(string participationId);

    ParticipationRecord? FindParticipation(string examId, string candidateId);

    IReadOnlyList<ParticipationRecord> ListParticipations(string examId);

    DeviceConflict? FindConflict(string participationId);

    ParticipationIdempotencyRecord? FindIdempotencyRecord(string requestId);

    void SaveSession(StudentSession session);

    void SavePreparation(EntryPreparation preparation);

    void SaveParticipation(ParticipationRecord participation);

    void SaveConflict(DeviceConflict conflict);

    void RemoveConflict(string participationId);

    void SaveDecisionAudit(DeviceDecisionAudit audit);

    void SaveIdempotencyRecord(ParticipationIdempotencyRecord record);
}

/// <summary>
/// Transactional, thread-safe reference persistence. Production storage must enforce
/// the same unique (examId, candidateId) participation key and atomic authorization
/// updates in PostgreSQL.
/// </summary>
public sealed class InMemoryParticipationPersistence : IParticipationPersistence
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State _state = new();

    public async ValueTask<T> ExecuteTransactionAsync<T>(
        Func<IParticipationTransaction, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var working = _state.Clone();
            var result = operation(new Transaction(working));
            _state = working;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class State
    {
        public Dictionary<string, StudentSession> Sessions { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, EntryPreparation> Preparations { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, ParticipationRecord> Participations { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ParticipationKeys { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, DeviceConflict> Conflicts { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, DeviceDecisionAudit> Audits { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, ParticipationIdempotencyRecord> Idempotency { get; init; } = new(StringComparer.Ordinal);

        public State Clone() => new()
        {
            Sessions = new Dictionary<string, StudentSession>(Sessions, StringComparer.Ordinal),
            Preparations = new Dictionary<string, EntryPreparation>(Preparations, StringComparer.Ordinal),
            Participations = new Dictionary<string, ParticipationRecord>(Participations, StringComparer.Ordinal),
            ParticipationKeys = new Dictionary<string, string>(ParticipationKeys, StringComparer.Ordinal),
            Conflicts = new Dictionary<string, DeviceConflict>(Conflicts, StringComparer.Ordinal),
            Audits = new Dictionary<string, DeviceDecisionAudit>(Audits, StringComparer.Ordinal),
            Idempotency = new Dictionary<string, ParticipationIdempotencyRecord>(Idempotency, StringComparer.Ordinal)
        };
    }

    private sealed class Transaction(State state) : IParticipationTransaction
    {
        public StudentSession? FindSession(string sessionId) => state.Sessions.GetValueOrDefault(sessionId);

        public EntryPreparation? FindPreparation(string preparationId) =>
            state.Preparations.GetValueOrDefault(preparationId);

        public ParticipationRecord? FindParticipation(string participationId) =>
            state.Participations.GetValueOrDefault(participationId);

        public ParticipationRecord? FindParticipation(string examId, string candidateId)
        {
            var key = CreateParticipationKey(examId, candidateId);
            return state.ParticipationKeys.TryGetValue(key, out var id)
                ? state.Participations.GetValueOrDefault(id)
                : null;
        }

        public IReadOnlyList<ParticipationRecord> ListParticipations(string examId) =>
            state.Participations.Values
                .Where(participation => StringComparer.Ordinal.Equals(participation.ExamId, examId))
                .OrderBy(participation => participation.CandidateId, StringComparer.Ordinal)
                .ToArray();

        public DeviceConflict? FindConflict(string participationId) =>
            state.Conflicts.GetValueOrDefault(participationId);

        public ParticipationIdempotencyRecord? FindIdempotencyRecord(string requestId) =>
            state.Idempotency.GetValueOrDefault(requestId);

        public void SaveSession(StudentSession session) => state.Sessions[session.SessionId] = session;

        public void SavePreparation(EntryPreparation preparation) =>
            state.Preparations[preparation.PreparationId] = preparation;

        public void SaveParticipation(ParticipationRecord participation)
        {
            var key = CreateParticipationKey(participation.ExamId, participation.CandidateId);
            if (state.ParticipationKeys.TryGetValue(key, out var existingId) && existingId != participation.ParticipationId)
            {
                throw new InvalidOperationException("同场同考生只能保留一条参试记录。");
            }

            state.ParticipationKeys[key] = participation.ParticipationId;
            state.Participations[participation.ParticipationId] = participation;
        }

        public void SaveConflict(DeviceConflict conflict) => state.Conflicts[conflict.ParticipationId] = conflict;

        public void RemoveConflict(string participationId) => state.Conflicts.Remove(participationId);

        public void SaveDecisionAudit(DeviceDecisionAudit audit) => state.Audits[audit.DecisionId] = audit;

        public void SaveIdempotencyRecord(ParticipationIdempotencyRecord record) =>
            state.Idempotency[record.RequestId] = record;

        private static string CreateParticipationKey(string examId, string candidateId) =>
            $"{examId.Length}:{examId}{candidateId}";
    }
}
