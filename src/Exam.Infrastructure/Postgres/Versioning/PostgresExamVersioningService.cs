using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Versioning;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Versioning;

/// <summary>
/// PostgreSQL-backed versioning reference service. The database stores an append-only command stream and
/// a query projection. Mutations replay the existing domain aggregate while holding the stream row
/// lock, so the domain remains the single source of schedule, structure and terminal-state rules.
/// </summary>
public sealed partial class PostgresExamVersioningService : IExamVersioningService
{
    private const string CreateOperation = "exam.create";
    private const string UpdateScheduleOperation = "schedule.update";
    private const string ScheduleTriggerOperation = "schedule.trigger";
    private const string PublishPaperOperation = "paper.publish";
    private const string PublishGradingOperation = "grading.publish";
    private const string RegisterParticipantOperation = "participant.register";
    private const string ChangeParticipantOperation = "participant.change-state";
    private const string ReportPaperTransferOperation = "paper.report-transfer";
    private const string ConfirmPaperReviewOperation = "paper.confirm-review";

    private readonly NpgsqlDataSource _dataSource;
    private readonly JsonSerializerOptions _json;

    public PostgresExamVersioningService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _json = JsonDefaults.Create();
    }

    public VersioningResult<ExamVersionSnapshot> CreateExam(CreateVersionedExamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return InvalidRequest<ExamVersionSnapshot>();
        }

        var replay = new InMemoryExamVersioningService();
        var validated = replay.CreateExam(request);
        if (!validated.Succeeded)
        {
            return validated;
        }

        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        AcquireRequestLock(connection, transaction, request.RequestId);
        if (FindIdempotent<ExamVersionSnapshot, CreateVersionedExamRequest>(
                connection,
                transaction,
                request.RequestId,
                CreateOperation,
                request) is { } replayed)
        {
            transaction.Commit();
            return replayed;
        }

        var recordedAt = DateTimeOffset.UtcNow;
        try
        {
            using var insertStream = new NpgsqlCommand(
                """
                INSERT INTO exam.version_streams (exam_id, stream_revision, created_at, updated_at)
                VALUES (@exam_id, 1, @recorded_at, @recorded_at);
                """,
                connection,
                transaction);
            insertStream.Parameters.AddWithValue("exam_id", request.ExamId);
            insertStream.Parameters.AddWithValue("recorded_at", recordedAt);
            insertStream.ExecuteNonQuery();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            transaction.Rollback();
            return VersioningResult<ExamVersionSnapshot>.Failure(
                VersioningErrorCode.ExamAlreadyExists,
                $"Exam '{request.ExamId}' already exists.");
        }

        AppendEvent(connection, transaction, request.ExamId, 1, CreateOperation, request, recordedAt);
        UpsertProjection(connection, transaction, 1, validated.Value!, recordedAt);
        UpsertEntryAccess(connection, transaction, validated.Value!);
        SaveIdempotent(connection, transaction, request.RequestId, CreateOperation, request, validated);
        transaction.Commit();
        return validated;
    }

    public VersioningResult<ExamVersionSnapshot> GetExam(string examId)
    {
        if (string.IsNullOrWhiteSpace(examId))
        {
            return NotFound<ExamVersionSnapshot>(examId);
        }

        ExamVersionSnapshot snapshot;
        // Return the connection before the canonical roster query borrows from the same bounded pool.
        using (var connection = _dataSource.OpenConnection())
        {
            using var command = new NpgsqlCommand(
                "SELECT snapshot::text FROM exam.version_state WHERE exam_id = @exam_id;",
                connection);
            command.Parameters.AddWithValue("exam_id", examId);
            var value = command.ExecuteScalar();
            if (value is not string json)
            {
                return NotFound<ExamVersionSnapshot>(examId);
            }
            snapshot = JsonSerializer.Deserialize<ExamVersionSnapshot>(json, _json)
                ?? throw new InvalidDataException($"The persisted version snapshot for exam '{examId}' is unreadable.");
        }
        return VersioningResult<ExamVersionSnapshot>.Success(OverlayCanonicalParticipants(snapshot));
    }

    /// <summary>Read the published question structure without loading the roster or borrowing nested connections.</summary>
    public async ValueTask<PaperStructure?> GetPaperStructureAsync(
        string examId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(examId)) return null;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT (snapshot #> '{paper,structure}')::text FROM exam.version_state WHERE exam_id = @exam_id;",
            connection);
        command.Parameters.AddWithValue("exam_id", examId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json) return null;
        var structure = JsonSerializer.Deserialize<PaperStructure>(json, _json);
        if (structure is not null && structure.Questions is null)
            throw new InvalidDataException("已发布试卷的题目结构无法读取。");
        return structure;
    }

    private ExamVersionSnapshot OverlayCanonicalParticipants(ExamVersionSnapshot snapshot)
    {
        var participants = new Dictionary<string, ParticipantVersionStatus>(
            snapshot.Participants,
            StringComparer.Ordinal);
        var canonicalIds = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            """
            SELECT participation_id, business_state, current_paper_version
              FROM exam.participations
             WHERE exam_id = @exam_id
             ORDER BY participation_id;
            """,
            connection);
        command.Parameters.AddWithValue("exam_id", snapshot.ExamId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var participantId = reader.GetString(0);
            var state = ParseParticipationState(reader.GetString(1));
            if (state == ParticipationBusinessState.NotParticipating)
            {
                participants.Remove(participantId);
                continue;
            }

            canonicalIds.Add(participantId);
            if (participants.TryGetValue(participantId, out var existing))
            {
                participants[participantId] = existing with { ExamState = state };
            }
            else
            {
                var installed = snapshot.Versions.PaperVersion == 0
                    ? 0
                    : Math.Min(reader.GetInt64(2), snapshot.Versions.PaperVersion);
                participants[participantId] = new ParticipantVersionStatus(
                    participantId,
                    state,
                    snapshot.Versions.PaperVersion,
                    installed,
                    installed == snapshot.Versions.PaperVersion && snapshot.Versions.PaperVersion > 0
                        ? PaperUpdateState.Updated
                        : PaperUpdateState.NotReceived,
                    null);
            }
        }

        foreach (var stale in participants.Keys.Where(id => !canonicalIds.Contains(id)).ToArray())
        {
            participants.Remove(stale);
        }

        return snapshot with
        {
            Participants = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ParticipantVersionStatus>(
                participants)
        };
    }

    public VersioningResult<ExamVersionSnapshot> UpdateSchedule(UpdateScheduleRequest request) =>
        Mutate(
            request.ExamId,
            UpdateScheduleOperation,
            request,
            service => service.UpdateSchedule(request),
            snapshot => snapshot);

    public VersioningResult<ScheduleTriggerResult> ExecuteScheduleTrigger(ExecuteScheduleTriggerRequest request) =>
        Mutate(
            request.ExamId,
            ScheduleTriggerOperation,
            request,
            service => service.ExecuteScheduleTrigger(request),
            result => result.Snapshot);

    public VersioningResult<ExamVersionSnapshot> PublishPaper(PublishPaperRequest request) =>
        Mutate(
            request.ExamId,
            PublishPaperOperation,
            request,
            service => service.PublishPaper(request),
            snapshot => snapshot);

    public VersioningResult<ExamVersionSnapshot> PublishGradingRules(PublishGradingRulesRequest request) =>
        Mutate(
            request.ExamId,
            PublishGradingOperation,
            request,
            service => service.PublishGradingRules(request),
            snapshot => snapshot);

    public VersioningResult<ExamVersionSnapshot> RegisterParticipant(RegisterVersionedParticipantRequest request) =>
        Mutate(
            request.ExamId,
            RegisterParticipantOperation,
            request,
            service => service.RegisterParticipant(request),
            snapshot => snapshot);

    public VersioningResult<ExamVersionSnapshot> ChangeParticipantState(ChangeParticipantStateRequest request) =>
        Mutate(
            request.ExamId,
            ChangeParticipantOperation,
            request,
            service => service.ChangeParticipantState(request),
            snapshot => snapshot);

    public VersioningResult<ExamVersionSnapshot> ReportPaperTransfer(ReportPaperTransferRequest request) =>
        Mutate(
            request.ExamId,
            ReportPaperTransferOperation,
            request,
            service => service.ReportPaperTransfer(request),
            snapshot => snapshot);

    public VersioningResult<ExamVersionSnapshot> ConfirmPaperReview(ConfirmPaperReviewRequest request) =>
        Mutate(
            request.ExamId,
            ConfirmPaperReviewOperation,
            request,
            service => service.ConfirmPaperReview(request),
            snapshot => snapshot);

    private VersioningResult<T> Mutate<T, TRequest>(
        string examId,
        string operation,
        TRequest request,
        Func<IExamVersioningService, VersioningResult<T>> execute,
        Func<T, ExamVersionSnapshot> snapshotFromResult)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = RequestIdOf(request);
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return InvalidRequest<T>();
        }

        using var connection = _dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        AcquireRequestLock(connection, transaction, requestId);
        if (FindIdempotent<T, TRequest>(connection, transaction, requestId, operation, request) is { } replayed)
        {
            transaction.Commit();
            return replayed;
        }

        var revision = LockStream(connection, transaction, examId);
        if (revision is null)
        {
            transaction.Rollback();
            return NotFound<T>(examId);
        }

        LockEntryAccess(connection, transaction, examId);
        var service = Replay(connection, transaction, examId, revision.Value);
        if (SynchronizeAndValidateCanonicalParticipants(
                connection,
                transaction,
                service,
                examId,
                request) is { } canonicalError)
        {
            transaction.Rollback();
            return VersioningResult<T>.Failure(canonicalError.Code, canonicalError.Message);
        }

        var beforeResult = service.GetExam(examId);
        if (!beforeResult.Succeeded || beforeResult.Value is null)
        {
            throw new InvalidDataException($"The version stream for exam '{examId}' did not rebuild its aggregate.");
        }

        var result = execute(service);
        if (!result.Succeeded || result.Value is null)
        {
            transaction.Rollback();
            return result;
        }

        var after = snapshotFromResult(result.Value);
        if (SnapshotsEqual(beforeResult.Value, after))
        {
            SaveIdempotent(connection, transaction, requestId, operation, request, result);
            transaction.Commit();
            return result;
        }

        var nextRevision = checked(revision.Value + 1);
        var recordedAt = DateTimeOffset.UtcNow;
        if (ApplyRuntimeEffects(
                connection,
                transaction,
                operation,
                requestId,
                request,
                beforeResult.Value,
                after,
                recordedAt) is { } runtimeError)
        {
            transaction.Rollback();
            return VersioningResult<T>.Failure(runtimeError.Code, runtimeError.Message);
        }

        AppendEvent(connection, transaction, examId, nextRevision, operation, request, recordedAt);
        UpdateStreamHead(connection, transaction, examId, revision.Value, nextRevision, recordedAt);
        UpsertProjection(connection, transaction, nextRevision, after, recordedAt);
        SaveIdempotent(connection, transaction, requestId, operation, request, result);
        transaction.Commit();
        return result;
    }

    private InMemoryExamVersioningService Replay(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string examId,
        long expectedRevision)
    {
        var service = new InMemoryExamVersioningService();
        using var command = new NpgsqlCommand(
            """
            SELECT stream_revision, operation_type, payload::text
            FROM exam.version_events
            WHERE exam_id = @exam_id
            ORDER BY stream_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", examId);

        long lastRevision = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var revision = reader.GetInt64(0);
            if (revision != lastRevision + 1)
            {
                throw new InvalidDataException($"Version stream '{examId}' has a revision gap at {revision}.");
            }

            ReplayEvent(service, reader.GetString(1), reader.GetString(2));
            lastRevision = revision;
        }

        if (lastRevision != expectedRevision)
        {
            throw new InvalidDataException(
                $"Version stream '{examId}' head is {expectedRevision}, but its last event is {lastRevision}.");
        }

        return service;
    }

    internal void ReplayEvent(InMemoryExamVersioningService service, string operation, string payload)
    {
        switch (operation)
        {
            case CreateOperation:
                RequireReplay(service.CreateExam(Deserialize<CreateVersionedExamRequest>(payload)), operation);
                break;
            case UpdateScheduleOperation:
                RequireReplay(service.UpdateSchedule(Deserialize<UpdateScheduleRequest>(payload)), operation);
                break;
            case ScheduleTriggerOperation:
                RequireReplay(service.ExecuteScheduleTrigger(Deserialize<ExecuteScheduleTriggerRequest>(payload)), operation);
                break;
            case PublishPaperOperation:
                RequireReplay(service.PublishPaper(Deserialize<PublishPaperRequest>(payload)), operation);
                break;
            case PublishGradingOperation:
                RequireReplay(service.PublishGradingRules(Deserialize<PublishGradingRulesRequest>(payload)), operation);
                break;
            case RegisterParticipantOperation:
                RequireReplay(
                    service.RegisterParticipant(Deserialize<RegisterVersionedParticipantRequest>(payload)),
                    operation);
                break;
            case ChangeParticipantOperation:
                RequireReplay(service.ChangeParticipantState(Deserialize<ChangeParticipantStateRequest>(payload)), operation);
                break;
            case ReportPaperTransferOperation:
                RequireReplay(service.ReportPaperTransfer(Deserialize<ReportPaperTransferRequest>(payload)), operation);
                break;
            case ConfirmPaperReviewOperation:
                RequireReplay(service.ConfirmPaperReview(Deserialize<ConfirmPaperReviewRequest>(payload)), operation);
                break;
            default:
                throw new InvalidDataException($"Unsupported version event operation '{operation}'.");
        }
    }

    private static long? LockStream(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string examId)
    {
        using var command = new NpgsqlCommand(
            "SELECT stream_revision FROM exam.version_streams WHERE exam_id = @exam_id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        var value = command.ExecuteScalar();
        return value is long revision ? revision : null;
    }

    private static void LockEntryAccess(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string examId)
    {
        using var command = new NpgsqlCommand(
            "SELECT exam_id FROM exam.exam_entry_access WHERE exam_id = @exam_id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        command.ExecuteScalar();
    }

    private static void AcquireRequestLock(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId)
    {
        using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", $"version-request:{requestId}");
        command.ExecuteNonQuery();
    }

    private static RuntimeEffectFailure? SynchronizeAndValidateCanonicalParticipants<TRequest>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InMemoryExamVersioningService service,
        string examId,
        TRequest request)
        where TRequest : notnull
    {
        using var command = new NpgsqlCommand(
            """
            SELECT participation_id, business_state, entry_version,
                   authorized_device_id, authorization_version
              FROM exam.participations
             WHERE exam_id = @exam_id
             ORDER BY participation_id
             FOR SHARE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        var canonical = new Dictionary<string, CanonicalParticipant>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var participant = new CanonicalParticipant(
                    reader.GetString(0),
                    ParseParticipationState(reader.GetString(1)),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt64(4));
                canonical.Add(participant.ParticipationId, participant);
            }
        }

        foreach (var participant in canonical.Values)
        {
            var synchronized = service.SynchronizePersistedParticipant(
                examId,
                participant.ParticipationId,
                participant.State);
            if (!synchronized.Succeeded)
            {
                return new(synchronized.ErrorCode, synchronized.Error ?? "Cannot synchronize canonical participation.");
            }
        }

        string? participantId = request switch
        {
            RegisterVersionedParticipantRequest value => value.ParticipantId,
            ChangeParticipantStateRequest value => value.ParticipantId,
            ReportPaperTransferRequest value => value.ParticipantId,
            ConfirmPaperReviewRequest value => value.ParticipantId,
            _ => null
        };
        if (participantId is null)
        {
            return null;
        }

        if (!canonical.TryGetValue(participantId, out var target) ||
            target.State == ParticipationBusinessState.NotParticipating)
        {
            return new(VersioningErrorCode.ParticipantNotFound, "The participant is not present in canonical participation state.");
        }

        if (request is RegisterVersionedParticipantRequest registration &&
            registration.InitialState != target.State)
        {
            return new(VersioningErrorCode.InvalidStateTransition, "Participant registration must match canonical state.");
        }

        if (request is ChangeParticipantStateRequest change && change.State != target.State)
        {
            return new(VersioningErrorCode.InvalidStateTransition, "版本服务不能覆盖参加考试与收卷事务确定的参加考试状态。");
        }

        if (request is ReportPaperTransferRequest transfer && !MatchesAuthorization(target, transfer))
        {
            return new(VersioningErrorCode.InvalidStateTransition, "Device authorization is stale or belongs to another participant.");
        }

        if (request is ConfirmPaperReviewRequest review && !MatchesAuthorization(target, review))
        {
            return new(VersioningErrorCode.InvalidStateTransition, "Device authorization is stale or belongs to another participant.");
        }

        return null;
    }

    private static bool MatchesAuthorization(CanonicalParticipant participant, ReportPaperTransferRequest request) =>
        !string.IsNullOrWhiteSpace(request.DeviceId) &&
        participant.EntryVersion == request.EntryVersion &&
        string.Equals(participant.AuthorizedDeviceId, request.DeviceId, StringComparison.Ordinal) &&
        participant.AuthorizationVersion == request.AuthorizationVersion;

    private static bool MatchesAuthorization(CanonicalParticipant participant, ConfirmPaperReviewRequest request) =>
        !string.IsNullOrWhiteSpace(request.DeviceId) &&
        participant.EntryVersion == request.EntryVersion &&
        string.Equals(participant.AuthorizedDeviceId, request.DeviceId, StringComparison.Ordinal) &&
        participant.AuthorizationVersion == request.AuthorizationVersion;

    private static RuntimeEffectFailure? ApplyRuntimeEffects<TRequest>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string operation,
        string requestId,
        TRequest request,
        ExamVersionSnapshot before,
        ExamVersionSnapshot after,
        DateTimeOffset recordedAt)
        where TRequest : notnull
    {
        UpsertEntryAccess(connection, transaction, after);
        if (operation == PublishPaperOperation && request is PublishPaperRequest paper)
        {
            // Once an exam uses archived publication, legacy JSON/direct-paper routes cannot
            // bypass its source comparison. The durable event survives loss of every archive row.
            using var history = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM exam.paper_materials WHERE exam_id=$1)
                    OR EXISTS (SELECT 1 FROM exam.version_events WHERE exam_id=$1
                        AND operation_type='paper.publish' AND payload->>'materialsSha256' IS NOT NULL)
                """, connection, transaction);
            history.Parameters.AddWithValue(paper.ExamId);
            if (paper.MaterialsSha256 is not null || (bool)history.ExecuteScalar()!)
            {
                if (paper.MaterialsSha256 is not { Length: 64 })
                    return new(VersioningErrorCode.MaterialsArchiveInvalid,
                        "本场考试必须通过带源材料归档的整体上传入口修订，不能绕过实际变更核对。");
                using var archived = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM exam.paper_materials WHERE exam_id=$1 AND paper_version=$2
                        AND lower(archive_sha256)=lower($3))
                    """, connection, transaction);
                archived.Parameters.AddWithValue(paper.ExamId);
                archived.Parameters.AddWithValue(after.Versions.PaperVersion);
                archived.Parameters.AddWithValue(paper.MaterialsSha256);
                if (!(bool)archived.ExecuteScalar()!)
                    return new(VersioningErrorCode.MaterialsArchiveInvalid, "待发布版本缺少经过核对的源材料归档。");
            }
            using var resource = new NpgsqlCommand(
                """
                SELECT octet_length(encrypted_content)
                  FROM exam.versioned_resources
                 WHERE resource_id = @resource_id
                   AND exam_id = @exam_id
                   AND resource_kind = 'paper-package'
                   AND resource_version = @resource_version;
                """,
                connection,
                transaction);
            resource.Parameters.AddWithValue("resource_id", paper.ArtifactId);
            resource.Parameters.AddWithValue("exam_id", paper.ExamId);
            resource.Parameters.AddWithValue("resource_version", after.Versions.PaperVersion);
            var length = resource.ExecuteScalar();
            if (length is not int packageLength)
            {
                return new(
                    VersioningErrorCode.PublicationNotReady,
                    "The encrypted paper resource must be durably stored before its version is published.");
            }

            using var update = new NpgsqlCommand(
                """
                UPDATE exam.exam_entry_access
                   SET package_ready = true,
                       encrypted_package_uri = @package_uri,
                       package_length = @package_length,
                       paper_version = @paper_version,
                       unlock_reference = @unlock_reference
                 WHERE exam_id = @exam_id;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("package_uri", $"/api/v1/resources/{Uri.EscapeDataString(paper.ArtifactId)}");
            update.Parameters.AddWithValue("package_length", (long)packageLength);
            update.Parameters.AddWithValue("paper_version", after.Versions.PaperVersion);
            update.Parameters.AddWithValue("unlock_reference", $"resource-envelope:{paper.ArtifactId}");
            update.Parameters.AddWithValue("exam_id", paper.ExamId);
            update.ExecuteNonQuery();

            using var participationVersion = new NpgsqlCommand(
                """
                UPDATE exam.participations
                   SET current_paper_version = @paper_version
                 WHERE exam_id = @exam_id;
                """,
                connection,
                transaction);
            participationVersion.Parameters.AddWithValue("paper_version", after.Versions.PaperVersion);
            participationVersion.Parameters.AddWithValue("exam_id", paper.ExamId);
            participationVersion.ExecuteNonQuery();
        }

        if (operation == PublishGradingOperation)
        {
            using var grading = new NpgsqlCommand(
                """
                UPDATE exam.participations
                   SET current_grading_version = @grading_version
                 WHERE exam_id = @exam_id;
                """,
                connection,
                transaction);
            grading.Parameters.AddWithValue("grading_version", after.Versions.GradingVersion);
            grading.Parameters.AddWithValue("exam_id", after.ExamId);
            grading.ExecuteNonQuery();
        }

        var eventTypes = new List<CommandEventType>();
        if (operation == UpdateScheduleOperation)
        {
            eventTypes.Add(CommandEventType.ScheduleChanged);
        }

        if (operation == PublishPaperOperation)
        {
            eventTypes.Add(CommandEventType.PaperUpdated);
        }

        if (before.LifecycleState != after.LifecycleState)
        {
            eventTypes.Add(after.LifecycleState == ExamLifecycle.Running
                ? CommandEventType.Start
                : CommandEventType.End);
        }

        if (eventTypes.Contains(CommandEventType.Start))
        {
            using var readiness = new NpgsqlCommand(
                """
                SELECT package_ready AND instructions_ready AND unlock_reference IS NOT NULL
                  FROM exam.exam_entry_access
                 WHERE exam_id = @exam_id
                 FOR UPDATE;
                """,
                connection,
                transaction);
            readiness.Parameters.AddWithValue("exam_id", after.ExamId);
            if ((bool)(readiness.ExecuteScalar() ?? false) is false)
            {
                return new(
                    VersioningErrorCode.PublicationNotReady,
                    "Paper package, instructions, and protected unlock material must be ready before the exam starts.");
            }

            using var transition = new NpgsqlCommand(
                """
                UPDATE exam.participations
                   SET business_state = 'answering', version = version + 1
                 WHERE exam_id = @exam_id AND business_state = 'waiting';
                """,
                connection,
                transaction);
            transition.Parameters.AddWithValue("exam_id", after.ExamId);
            transition.ExecuteNonQuery();
        }

        foreach (var eventType in eventTypes)
        {
            InsertReliableEvents(
                connection,
                transaction,
                requestId,
                after,
                eventType,
                recordedAt);
        }

        return null;
    }

    private static void UpsertEntryAccess(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExamVersionSnapshot snapshot)
    {
        using var command = new NpgsqlCommand(
            """
            INSERT INTO exam.exam_entry_access
                (exam_id, scheduled_start, scheduled_end, lifecycle,
                 package_ready, instructions_ready, encrypted_package_uri,
                 package_length, paper_version, instructions_version, unlock_reference)
            VALUES
                (@exam_id, @scheduled_start, @scheduled_end, @lifecycle,
                 false, false, '', 0, 1, 1, NULL)
            ON CONFLICT (exam_id) DO UPDATE SET
                scheduled_start = EXCLUDED.scheduled_start,
                scheduled_end = EXCLUDED.scheduled_end,
                lifecycle = EXCLUDED.lifecycle;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", snapshot.ExamId);
        command.Parameters.AddWithValue("scheduled_start", snapshot.Schedule.StartsAt.ToUniversalTime());
        command.Parameters.AddWithValue("scheduled_end", snapshot.Schedule.EndsAt.ToUniversalTime());
        command.Parameters.AddWithValue("lifecycle", snapshot.LifecycleState.ToString().ToLowerInvariant());
        command.ExecuteNonQuery();
    }

    private static void InsertReliableEvents(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        ExamVersionSnapshot snapshot,
        CommandEventType eventType,
        DateTimeOffset recordedAt)
    {
        var stateVersion = eventType == CommandEventType.PaperUpdated
            ? snapshot.Versions.PaperVersion
            : snapshot.Versions.ScheduleVersion;
        var resourceVersion = eventType == CommandEventType.PaperUpdated
            ? snapshot.Versions.PaperVersion
            : (long?)null;
        using var command = new NpgsqlCommand(
            """
            INSERT INTO exam.command_events
                (event_id, create_request_id, exam_id, participation_id, entry_version,
                 event_type, state_version, target_device_id, target_authorization_version,
                 resource_version, protected_unlock_reference, metadata, created_at,
                 next_delivery_at, delivery_attempts)
            SELECT
                @event_prefix || participation_id,
                @request_prefix || participation_id,
                exam_id,
                participation_id,
                entry_version,
                @event_type,
                @state_version,
                authorized_device_id,
                authorization_version,
                @resource_version,
                CASE WHEN @event_type = 'Start' THEN access.unlock_reference ELSE NULL END,
                CAST(@metadata AS jsonb),
                @created_at,
                @created_at,
                0
              FROM exam.participations AS participation
              JOIN exam.exam_entry_access AS access USING (exam_id)
             WHERE participation.exam_id = @exam_id
               AND participation.business_state IN ('waiting', 'answering');
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_prefix", $"version:{requestId}:{eventType}:");
        command.Parameters.AddWithValue("request_prefix", $"version:{requestId}:{eventType}:");
        command.Parameters.AddWithValue("event_type", eventType.ToString());
        command.Parameters.AddWithValue("state_version", stateVersion);
        command.Parameters.AddWithValue(
            "resource_version",
            NpgsqlDbType.Bigint,
            resourceVersion ?? (object)DBNull.Value);
        // 修订定位：PaperUpdated 指令携带本次发布的变更题目标识清单，
        // 学生端据此列出修订题号并支持一键跳转；其他指令保持原有元数据。
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scheduleVersion"] = snapshot.Versions.ScheduleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["paperVersion"] = snapshot.Versions.PaperVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (eventType == CommandEventType.ScheduleChanged)
        {
            // 与时间计划和可靠事件一起提交；重发只读取这份不可变事件，不读取后来修改的计划。
            metadata["scheduledStart"] = snapshot.Schedule.StartsAt.ToOffset(TimeSpan.FromHours(8))
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            metadata["scheduledEnd"] = snapshot.Schedule.EndsAt.ToOffset(TimeSpan.FromHours(8))
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (eventType == CommandEventType.PaperUpdated && snapshot.Paper is { } paper)
        {
            metadata["changedQuestionIds"] = JsonSerializer.Serialize(
                paper.ChangedQuestionIds);
        }

        command.Parameters.AddWithValue(
            "metadata",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(metadata));
        command.Parameters.AddWithValue("created_at", recordedAt);
        command.Parameters.AddWithValue("exam_id", snapshot.ExamId);
        command.ExecuteNonQuery();
    }

    private static ParticipationBusinessState ParseParticipationState(string value) => value switch
    {
        "not_participating" => ParticipationBusinessState.NotParticipating,
        "waiting" => ParticipationBusinessState.Waiting,
        "answering" => ParticipationBusinessState.Answering,
        "submitted" => ParticipationBusinessState.Submitted,
        _ => throw new DataException($"Unknown participation state '{value}'.")
    };

    private VersioningResult<T>? FindIdempotent<T, TRequest>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        string operation,
        TRequest request)
        where TRequest : notnull
    {
        using var command = new NpgsqlCommand(
            """
            SELECT operation_type, payload_hash, response_json::text
              FROM exam.version_idempotency
             WHERE request_id = @request_id
             FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var matchingOperation = string.Equals(reader.GetString(0), operation, StringComparison.Ordinal);
        var storedHash = reader.GetString(1);
        var responseJson = reader.GetString(2);
        reader.Close();
        if (!matchingOperation ||
            !string.Equals(storedHash, HashPayload(request), StringComparison.Ordinal) &&
            !MatchesLegacyGradingPayload(request, storedHash, responseJson) &&
            !MatchesLegacySchedulePayload(connection, transaction, request, storedHash))
        {
            return VersioningResult<T>.Failure(
                VersioningErrorCode.IdempotencyConflict,
                "requestId has already been used for a different versioning operation or payload.");
        }

        return JsonSerializer.Deserialize<VersioningResult<T>>(responseJson, _json)
            ?? throw new InvalidDataException("The stored versioning idempotency response is unreadable.");
    }

    private void SaveIdempotent<T, TRequest>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        string operation,
        TRequest request,
        VersioningResult<T> result)
        where TRequest : notnull
    {
        using var command = new NpgsqlCommand(
            """
            INSERT INTO exam.version_idempotency
                (request_id, operation_type, payload_hash, response_json)
            VALUES
                (@request_id, @operation_type, @payload_hash, CAST(@response_json AS jsonb));
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("operation_type", operation);
        command.Parameters.AddWithValue("payload_hash", HashPayload(request));
        command.Parameters.AddWithValue("response_json", JsonSerializer.Serialize(result, _json));
        command.ExecuteNonQuery();
    }

    private string HashPayload<TRequest>(TRequest request) where TRequest : notnull =>
        // The HTTP endpoint supplies Now on every arrival. It is audit time, not caller intent.
        // Keep every caller-controlled field in the identity, including the exact answer/rule bytes.
        request switch
        {
            PublishGradingRulesRequest grading => HashExactPayload(grading with { Now = DateTimeOffset.UnixEpoch }),
            UpdateScheduleRequest schedule => HashExactPayload(schedule with { Now = DateTimeOffset.UnixEpoch }),
            _ => HashExactPayload(request)
        };

    private string HashExactPayload<TRequest>(TRequest request) where TRequest : notnull =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, _json))));

    private bool MatchesLegacyGradingPayload<TRequest>(TRequest request, string storedHash, string responseJson)
    {
        if (request is not PublishGradingRulesRequest grading) return false;
        // Old rows included Now. Reconstruct ONLY that server-owned value from the durable receipt;
        // do not rewrite historical receipts, events or accept another payload under the same ID.
        var receipt = JsonSerializer.Deserialize<VersioningResult<ExamVersionSnapshot>>(responseJson, _json);
        return receipt is { Succeeded: true, Value.Grading: not null } &&
            receipt.Value.ExamId == grading.ExamId &&
            string.Equals(storedHash, HashExactPayload(grading with { Now = receipt.Value.Grading.PublishedAt }),
                StringComparison.Ordinal);
    }

    private bool MatchesLegacySchedulePayload<TRequest>(NpgsqlConnection connection,
        NpgsqlTransaction transaction, TRequest request, string storedHash)
    {
        if (request is not UpdateScheduleRequest schedule) return false;
        // Legacy hashes included the server arrival time. Recover only that value from the
        // immutable event; every caller-owned field and the original hash must still agree.
        using var command = new NpgsqlCommand("""
            SELECT payload::text FROM exam.version_events
             WHERE exam_id=$1 AND operation_type=$2 AND payload->>'requestId'=$3;
            """, connection, transaction);
        command.Parameters.AddWithValue(schedule.ExamId);
        command.Parameters.AddWithValue(UpdateScheduleOperation);
        command.Parameters.AddWithValue(schedule.RequestId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        var original = JsonSerializer.Deserialize<UpdateScheduleRequest>(reader.GetString(0), _json);
        if (reader.Read() || original is null) return false;
        return string.Equals(storedHash, HashExactPayload(schedule with { Now = original.Now }), StringComparison.Ordinal);
    }

    private static string RequestIdOf<TRequest>(TRequest request) =>
        request switch
        {
            UpdateScheduleRequest value => value.RequestId,
            ExecuteScheduleTriggerRequest value => value.RequestId,
            PublishPaperRequest value => value.RequestId,
            PublishGradingRulesRequest value => value.RequestId,
            RegisterVersionedParticipantRequest value => value.RequestId,
            ChangeParticipantStateRequest value => value.RequestId,
            ReportPaperTransferRequest value => value.RequestId,
            ConfirmPaperReviewRequest value => value.RequestId,
            _ => string.Empty
        };

    private static VersioningResult<T> InvalidRequest<T>() =>
        VersioningResult<T>.Failure(VersioningErrorCode.InvalidRequest, "requestId is required.");

    private void AppendEvent<TRequest>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string examId,
        long revision,
        string operation,
        TRequest request,
        DateTimeOffset recordedAt)
    {
        using var command = new NpgsqlCommand(
            """
            INSERT INTO exam.version_events
                (exam_id, stream_revision, operation_type, payload, recorded_at)
            VALUES
                (@exam_id, @stream_revision, @operation_type, @payload, @recorded_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        command.Parameters.AddWithValue("stream_revision", revision);
        command.Parameters.AddWithValue("operation_type", operation);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(request, _json));
        command.Parameters.AddWithValue("recorded_at", recordedAt);
        command.ExecuteNonQuery();
    }

    private void UpsertProjection(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long streamRevision,
        ExamVersionSnapshot snapshot,
        DateTimeOffset recordedAt)
    {
        using var command = new NpgsqlCommand(
            """
            INSERT INTO exam.version_state
                (exam_id, stream_revision, schedule_version, paper_version, grading_version,
                 starts_at, ends_at, lifecycle, actual_started_at, actual_ended_at, snapshot, updated_at)
            VALUES
                (@exam_id, @stream_revision, @schedule_version, @paper_version, @grading_version,
                 @starts_at, @ends_at, @lifecycle, @actual_started_at, @actual_ended_at, @snapshot, @updated_at)
            ON CONFLICT (exam_id) DO UPDATE SET
                stream_revision = EXCLUDED.stream_revision,
                schedule_version = EXCLUDED.schedule_version,
                paper_version = EXCLUDED.paper_version,
                grading_version = EXCLUDED.grading_version,
                starts_at = EXCLUDED.starts_at,
                ends_at = EXCLUDED.ends_at,
                lifecycle = EXCLUDED.lifecycle,
                actual_started_at = EXCLUDED.actual_started_at,
                actual_ended_at = EXCLUDED.actual_ended_at,
                snapshot = EXCLUDED.snapshot,
                updated_at = EXCLUDED.updated_at
            WHERE version_state.stream_revision < EXCLUDED.stream_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("exam_id", snapshot.ExamId);
        command.Parameters.AddWithValue("stream_revision", streamRevision);
        command.Parameters.AddWithValue("schedule_version", snapshot.Versions.ScheduleVersion);
        command.Parameters.AddWithValue("paper_version", snapshot.Versions.PaperVersion);
        command.Parameters.AddWithValue("grading_version", snapshot.Versions.GradingVersion);
        command.Parameters.AddWithValue("starts_at", snapshot.Schedule.StartsAt.ToUniversalTime());
        command.Parameters.AddWithValue("ends_at", snapshot.Schedule.EndsAt.ToUniversalTime());
        command.Parameters.AddWithValue("lifecycle", snapshot.LifecycleState.ToString());
        command.Parameters.AddWithValue(
            "actual_started_at",
            NpgsqlDbType.TimestampTz,
            snapshot.ActualStartedAt?.ToUniversalTime() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "actual_ended_at",
            NpgsqlDbType.TimestampTz,
            snapshot.ActualEndedAt?.ToUniversalTime() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "snapshot",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(snapshot, _json));
        command.Parameters.AddWithValue("updated_at", recordedAt);
        command.ExecuteNonQuery();
    }

    private static void UpdateStreamHead(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string examId,
        long previousRevision,
        long nextRevision,
        DateTimeOffset recordedAt)
    {
        using var command = new NpgsqlCommand(
            """
            UPDATE exam.version_streams
            SET stream_revision = @next_revision, updated_at = @updated_at
            WHERE exam_id = @exam_id AND stream_revision = @previous_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("next_revision", nextRevision);
        command.Parameters.AddWithValue("updated_at", recordedAt);
        command.Parameters.AddWithValue("exam_id", examId);
        command.Parameters.AddWithValue("previous_revision", previousRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new DBConcurrencyException($"Version stream '{examId}' changed while its mutation was being saved.");
        }
    }

    private T Deserialize<T>(string json) where T : notnull =>
        JsonSerializer.Deserialize<T>(json, _json)
        ?? throw new InvalidDataException($"Cannot deserialize persisted version event as {typeof(T).Name}.");

    private static void RequireReplay<T>(VersioningResult<T> result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidDataException(
                $"Persisted version event '{operation}' no longer replays: {result.ErrorCode} {result.Error}");
        }
    }

    private bool SnapshotsEqual(ExamVersionSnapshot left, ExamVersionSnapshot right) =>
        string.Equals(
            JsonSerializer.Serialize(left, _json),
            JsonSerializer.Serialize(right, _json),
            StringComparison.Ordinal);

    private static VersioningResult<T> NotFound<T>(string? examId) =>
        VersioningResult<T>.Failure(
            VersioningErrorCode.ExamNotFound,
            $"Exam '{examId}' was not found.");

    private sealed record RuntimeEffectFailure(VersioningErrorCode Code, string Message);

    private sealed record CanonicalParticipant(
        string ParticipationId,
        ParticipationBusinessState State,
        long EntryVersion,
        string AuthorizedDeviceId,
        long AuthorizationVersion);
}
