using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Submissions;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed partial class PostgresSubmissionStoreTests(PostgresDatabaseFixture fixture)
{
    private static int _clientSequence = 500;

    [Fact]
    public async Task FinalSubmission_CommitsReceiptTerminalStateIdempotencyAndOutboxTogether()
    {
        var context = await SeedParticipationAsync(currentPaperVersion: 3, gradingVersion: 7);
        var store = new PostgresSubmissionStore(fixture.DataSource);
        var answers = Answer("A");
        var request = context.Submission(Id("request"), Id("submission"), answers) with
        {
            PaperVersion = 1
        };

        var nonRoundTripTimestamp = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero).AddTicks(7);
        var saved = await store.SaveSubmissionAsync(
            request,
            nonRoundTripTimestamp,
            Id("receipt"));
        answers["q-1"] = AnswerValue.Selected("D");
        var replay = await store.SaveSubmissionAsync(
            request with { FrozenAnswers = Answer("C") },
            DateTimeOffset.UtcNow.AddSeconds(1),
            Id("ignored-receipt"));
        var otherIdentity = await SeedParticipationAsync(examId: context.ExamId);
        var crossIdentity = await store.SaveSubmissionAsync(
            otherIdentity.Submission(request.RequestId, Id("cross-submission"), Answer("D")),
            DateTimeOffset.UtcNow.AddSeconds(1),
            Id("cross-receipt"));
        var lateProgress = await store.SaveProgressAsync(
            context.Progress(Id("progress"), 99, "B"),
            DateTimeOffset.UtcNow.AddSeconds(2));
        var queried = await store.FindSubmissionAsync(context.ExamId, context.CandidateId);

        Assert.Equal(SubmissionPersistenceStatus.Saved, saved.Status);
        Assert.NotNull(queried);
        Assert.Equal(saved.Submission!.Receipt, queried!.Receipt);
        Assert.Equal(0, saved.Submission.Receipt.ReceivedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(SubmissionPersistenceStatus.RequestReplay, replay.Status);
        Assert.Equal(saved.Submission!.Receipt, replay.Submission!.Receipt);
        Assert.Equal("A", Assert.Single(replay.Submission.FrozenAnswers["q-1"].OptionIds));
        Assert.Equal(SubmissionPersistenceStatus.AuthorizationRevoked, crossIdentity.Status);
        Assert.Null(crossIdentity.Submission);
        Assert.Equal(ProgressPersistenceStatus.AlreadySubmitted, lateProgress.Status);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT p.business_state,
                   (SELECT count(*) FROM exam.final_submissions WHERE participation_id = @participation_id),
                   (SELECT count(*) FROM exam.submission_idempotency WHERE request_id = @request_id),
                   (SELECT count(*) FROM exam.grading_outbox WHERE submission_id = @submission_id)
              FROM exam.participations p
             WHERE p.participation_id = @participation_id;
            """, connection);
        command.Parameters.AddWithValue("participation_id", context.ParticipationId);
        command.Parameters.AddWithValue("request_id", request.RequestId);
        command.Parameters.AddWithValue("submission_id", request.SubmissionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("submitted", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task OutboxFailure_RollsBackSubmissionReceiptTerminalStateAndIdempotency()
    {
        var context = await SeedParticipationAsync();
        var store = new PostgresSubmissionStore(fixture.DataSource);
        var request = context.Submission(
            Id("rollback-request"),
            $"rollback-{Guid.NewGuid():N}",
            Answer("A"));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var install = new NpgsqlCommand("""
                         CREATE OR REPLACE FUNCTION exam.reject_test_grading_outbox()
                         RETURNS trigger
                         LANGUAGE plpgsql
                         AS $$
                         BEGIN
                             IF NEW.submission_id LIKE 'rollback-%' THEN
                                 RAISE EXCEPTION 'synthetic outbox failure';
                             END IF;
                             RETURN NEW;
                         END;
                         $$;

                         DROP TRIGGER IF EXISTS test_reject_grading_outbox ON exam.grading_outbox;
                         CREATE TRIGGER test_reject_grading_outbox
                             BEFORE INSERT ON exam.grading_outbox
                             FOR EACH ROW EXECUTE FUNCTION exam.reject_test_grading_outbox();
                         """, connection))
        {
            await install.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                store.SaveSubmissionAsync(
                        request,
                        DateTimeOffset.UtcNow,
                        Id("receipt"))
                    .AsTask());
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DROP TRIGGER IF EXISTS test_reject_grading_outbox ON exam.grading_outbox;
                DROP FUNCTION IF EXISTS exam.reject_test_grading_outbox();
                """, connection);
            await cleanup.ExecuteNonQueryAsync();
        }

        await using var verify = new NpgsqlCommand("""
            SELECT p.business_state,
                   (SELECT count(*) FROM exam.final_submissions WHERE submission_id = @submission_id),
                   (SELECT count(*) FROM exam.submission_idempotency WHERE request_id = @request_id),
                   (SELECT count(*) FROM exam.grading_outbox WHERE submission_id = @submission_id)
              FROM exam.participations p
             WHERE p.participation_id = @participation_id;
            """, connection);
        verify.Parameters.AddWithValue("participation_id", context.ParticipationId);
        verify.Parameters.AddWithValue("submission_id", request.SubmissionId);
        verify.Parameters.AddWithValue("request_id", request.RequestId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("answering", reader.GetString(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
    }

    [Fact]
    public async Task ConcurrentSubmissions_KeepOneExamCandidateAnswerAndOneGradingTask()
    {
        var context = await SeedParticipationAsync();
        var stores = Enumerable.Range(0, 12)
            .Select(_ => new PostgresSubmissionStore(fixture.DataSource))
            .ToArray();
        var attempts = stores.Select((store, index) =>
            store.SaveSubmissionAsync(
                    context.Submission(
                        Id($"request-{index}"),
                        Id($"submission-{index}"),
                        Answer(index.ToString())),
                    DateTimeOffset.UtcNow.AddMilliseconds(index),
                    Id($"receipt-{index}"))
                .AsTask())
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Status == SubmissionPersistenceStatus.Saved);
        Assert.Equal(
            results.Length - 1,
            results.Count(result => result.Status == SubmissionPersistenceStatus.ExistingSubmission));
        Assert.Single(results.Select(result => result.Submission!.SubmissionId).Distinct(StringComparer.Ordinal));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM exam.final_submissions
                  WHERE exam_id = @exam_id AND candidate_id = @candidate_id),
                (SELECT count(*) FROM exam.grading_outbox outbox
                  JOIN exam.final_submissions submission USING (submission_id)
                 WHERE submission.exam_id = @exam_id AND submission.candidate_id = @candidate_id);
            """, connection);
        command.Parameters.AddWithValue("exam_id", context.ExamId);
        command.Parameters.AddWithValue("candidate_id", context.CandidateId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task RequestIdempotency_IsBoundToIdentityAndProgressKeepsHighestVersion()
    {
        var first = await SeedParticipationAsync();
        var second = await SeedParticipationAsync(examId: first.ExamId);
        var store = new PostgresSubmissionStore(fixture.DataSource);
        var requestId = Id("shared-progress-request");
        var saved = await store.SaveProgressAsync(
            first.Progress(requestId, 4, "A"),
            DateTimeOffset.UtcNow);
        var crossIdentity = await store.SaveProgressAsync(
            second.Progress(requestId, 100, "D"),
            DateTimeOffset.UtcNow.AddSeconds(1));
        var old = await store.SaveProgressAsync(
            first.Progress(Id("old-progress"), 3, "C"),
            DateTimeOffset.UtcNow.AddSeconds(2));
        var current = await store.FindProgressAsync(first.ParticipationId);

        Assert.Equal(ProgressPersistenceStatus.Saved, saved.Status);
        Assert.Equal(ProgressPersistenceStatus.AuthorizationRevoked, crossIdentity.Status);
        Assert.Null(crossIdentity.Progress);
        Assert.Null(await store.FindProgressAsync(second.ParticipationId));
        Assert.Equal(ProgressPersistenceStatus.VersionConflict, old.Status);
        Assert.Equal(4, current!.AnswerVersion);
        Assert.Equal("A", Assert.Single(current.Answers["q-1"].OptionIds));
    }

    [Fact]
    public async Task CompletionAndGradingStores_AreDatabaseIdempotent()
    {
        var context = await SeedParticipationAsync();
        var submissionStore = new PostgresSubmissionStore(fixture.DataSource);
        var request = context.Submission(Id("request"), Id("submission"), Answer("A"));
        var saved = await submissionStore.SaveSubmissionAsync(
            request,
            DateTimeOffset.UtcNow,
            Id("receipt"));
        var grading = new PostgresGradingTaskQueue(fixture.DataSource);
        var completions = new PostgresCollectionCompletionStore(
            fixture.DataSource,
            () => Id("collection"));

        var duplicateGrading = await grading.EnqueueOnceAsync(
            request.SubmissionId,
            saved.Submission!.Receipt.GradingVersion);
        var zero = await completions.TryRecordAsync(Id("empty-exam"), 1, 0, DateTimeOffset.UtcNow);
        var first = await completions.TryRecordAsync(context.ExamId, 1, 1, DateTimeOffset.UtcNow);
        var duplicate = await completions.TryRecordAsync(context.ExamId, 1, 1, DateTimeOffset.UtcNow);

        Assert.False(duplicateGrading);
        Assert.Null(zero);
        Assert.NotNull(first);
        Assert.Null(duplicate);
    }

    private async Task<ParticipationContext> SeedParticipationAsync(
        long currentPaperVersion = 1,
        long gradingVersion = 1,
        string? examId = null)
    {
        examId ??= Id("exam");
        var candidateId = Id("candidate");
        var participationId = Id("participation");
        var deviceId = Id("device");
        var sequence = Interlocked.Increment(ref _clientSequence);
        var clientNumber = $"20260922{sequence:D3}";
        var admission = $"{sequence:D6}";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
                         INSERT INTO exam.activation_devices
                             (device_id, client_number, activation_date, activation_status,
                              room_group_id, protected_credential, credential_version,
                              created_at, last_seen_at, version)
                         VALUES
                             (@device_id, @client_number, DATE '2026-09-22', 'active',
                              NULL, 'test-only-protected-verifier', 1,
                              clock_timestamp(), NULL, 1);

                         INSERT INTO exam.candidate_roster_entries
                             (exam_id, candidate_id, candidate_name, class_text, admission_no)
                         VALUES
                             (@exam_id, @candidate_id, '合成考生', '01', @admission_no);
                         """, connection))
        {
            seed.Parameters.AddWithValue("device_id", deviceId);
            seed.Parameters.AddWithValue("client_number", clientNumber);
            seed.Parameters.AddWithValue("exam_id", examId);
            seed.Parameters.AddWithValue("candidate_id", candidateId);
            seed.Parameters.AddWithValue("admission_no", admission);
            await seed.ExecuteNonQueryAsync();
        }

        var store = new PostgresSubmissionStore(fixture.DataSource);
        await store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
            examId,
            candidateId,
            participationId,
            1,
            deviceId,
            1,
            ParticipationBusinessState.Answering,
            currentPaperVersion,
            gradingVersion));
        return new ParticipationContext(examId, candidateId, participationId, deviceId);
    }

    private static Dictionary<string, AnswerValue> Answer(string option) =>
        new() { ["q-1"] = AnswerValue.Selected(option) };

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed record ParticipationContext(
        string ExamId,
        string CandidateId,
        string ParticipationId,
        string DeviceId)
    {
        public SyncProgressRequest Progress(string requestId, long answerVersion, string option) =>
            new(
                requestId,
                ExamId,
                CandidateId,
                ParticipationId,
                1,
                DeviceId,
                1,
                answerVersion,
                1,
                Answer(option),
                "q-1");

        public SubmitFinalAnswersRequest Submission(
            string requestId,
            string submissionId,
            IReadOnlyDictionary<string, AnswerValue> answers) =>
            new(
                requestId,
                submissionId,
                ExamId,
                CandidateId,
                ParticipationId,
                1,
                DeviceId,
                1,
                1,
                answers);
    }
}
