using Exam.Application.Submissions;
using Exam.Contracts.Submissions;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Fact]
    public async Task Collection_UsesAuthoritativeRosterAndIgnoresForgedSubset()
    {
        var first = await SeedParticipationAsync();
        var missing = await SeedParticipationAsync(examId: first.ExamId);
        await SaveFinalAsync(first);
        var store = new PostgresCollectionCompletionStore(fixture.DataSource);
        var service = new CollectionCompletionService(
            new PostgresSubmissionStore(fixture.DataSource), store, authoritativeStore: store);

        Assert.Null(await service.EvaluateAsync(first.ExamId, 999, [first.ParticipationId]));
        Assert.Empty(await store.ReadEventsAsync(first.ExamId, 0));
        // No connection-status predicate exists: a disconnected actual participant still owes a final.
        await SaveFinalAsync(missing);
        await store.ProcessPendingAsync();
        var completed = Assert.Single(await store.ReadEventsAsync(first.ExamId, 0));
        Assert.Equal(2, completed.ParticipantCount);
        Assert.Equal(2, completed.RosterVersion);
    }

    [Fact]
    public async Task Collection_LastTwoFinalsConcurrent_RecoveryEmitsOnceAndReplayIsStable()
    {
        var first = await SeedParticipationAsync();
        var second = await SeedParticipationAsync(examId: first.ExamId);
        await Task.WhenAll(SaveFinalAsync(first), SaveFinalAsync(second));
        // Recreate the store after answer persistence: no in-memory enqueue is required to recover.
        var recovered = new PostgresCollectionCompletionStore(fixture.DataSource);
        await Task.WhenAll(recovered.ProcessPendingAsync().AsTask(), recovered.ProcessPendingAsync().AsTask());
        var notification = Assert.Single(await recovered.ReadEventsAsync(first.ExamId, 0));
        Assert.Null(await recovered.EvaluateAsync(first.ExamId));
        Assert.Equal(notification, Assert.Single(await recovered.ReadEventsAsync(first.ExamId, 0)));
        Assert.Empty(await recovered.ReadEventsAsync(first.ExamId, notification.RosterVersion));
        Assert.Equal(2, notification.ParticipantCount);
    }

    [Fact]
    public async Task Collection_WaitingWithdrawalRechecksAndZeroRosterNeverNotifies()
    {
        var first = await SeedParticipationAsync();
        var waiting = await SeedParticipationAsync(examId: first.ExamId);
        await SetStateAsync(waiting.ParticipationId, "waiting");
        await SaveFinalAsync(first);
        var store = new PostgresCollectionCompletionStore(fixture.DataSource);
        await store.ProcessPendingAsync();
        Assert.Empty(await store.ReadEventsAsync(first.ExamId, 0));
        await SetStateAsync(waiting.ParticipationId, "not_participating");
        await store.ProcessPendingAsync();
        var notification = Assert.Single(await store.ReadEventsAsync(first.ExamId, 0));
        Assert.Equal(3, notification.RosterVersion);
        Assert.Equal(1, notification.ParticipantCount);

        var zero = await SeedParticipationAsync();
        await SetStateAsync(zero.ParticipationId, "waiting");
        await SetStateAsync(zero.ParticipationId, "not_participating");
        await store.ProcessPendingAsync();
        Assert.Empty(await store.ReadEventsAsync(zero.ExamId, 0));
    }

    [Fact]
    public async Task Collection_NewActualMemberGetsNewVersionAndCannotReuseEarlierCompletion()
    {
        var first = await SeedParticipationAsync();
        await SaveFinalAsync(first);
        var store = new PostgresCollectionCompletionStore(fixture.DataSource);
        await store.ProcessPendingAsync();
        var earlier = Assert.Single(await store.ReadEventsAsync(first.ExamId, 0));
        var second = await SeedParticipationAsync(examId: first.ExamId);
        await store.ProcessPendingAsync();
        Assert.Empty(await store.ReadEventsAsync(first.ExamId, earlier.RosterVersion));
        await SaveFinalAsync(second);
        await store.ProcessPendingAsync();
        var next = Assert.Single(await store.ReadEventsAsync(first.ExamId, earlier.RosterVersion));
        Assert.Equal(earlier.RosterVersion + 1, next.RosterVersion);
        Assert.NotEqual(earlier.EventId, next.EventId);
        Assert.Equal(2, next.ParticipantCount);
    }

    [Fact]
    public async Task Collection_OutboxWriteFailureRetainsPendingCheckForRestart()
    {
        var first = await SeedParticipationAsync();
        await SaveFinalAsync(first);
        var store = new PostgresCollectionCompletionStore(fixture.DataSource);
        var completed = await store.EvaluateAsync(first.ExamId);
        Assert.NotNull(completed);
        var other = await SeedParticipationAsync();
        await SaveFinalAsync(other);
        // Force a unique event-id violation after the durable final exists.
        var failing = new PostgresCollectionCompletionStore(fixture.DataSource, () => completed.EventId);
        await Assert.ThrowsAsync<PostgresException>(() => failing.EvaluateAsync(other.ExamId).AsTask());
        Assert.Empty(await store.ReadEventsAsync(other.ExamId, 0));
        await new PostgresCollectionCompletionStore(fixture.DataSource).ProcessPendingAsync();
        Assert.Single(await store.ReadEventsAsync(other.ExamId, 0));
    }

    [Fact]
    public async Task Collection_BackgroundWorkerRecoversPendingWithoutManualEvaluate()
    {
        var first = await SeedParticipationAsync();
        await SaveFinalAsync(first);
        var store = new PostgresCollectionCompletionStore(fixture.DataSource);
        using var worker = new CollectionCompletionWorker(store,
            new ActiveNodeGuard(fixture.DataSource, new ServerIdentityOptions
            {
                ServerId = Id("worker-server"), NodeId = "node-a", ServerEpoch = 1
            }, true), NullLogger<CollectionCompletionWorker>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(deadline.Token);
        try
        {
            IReadOnlyList<CollectionCompletedNotification> events;
            do
            {
                await Task.Delay(25, deadline.Token);
                events = await store.ReadEventsAsync(first.ExamId, 0, cancellationToken: deadline.Token);
            } while (events.Count == 0);
            Assert.Single(events);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Collection_CommitDuringWorkerDrainCannotLosePendingWakeup()
    {
        var first = await SeedParticipationAsync();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var claim = new NpgsqlCommand(
            "SELECT exam_id FROM exam.collection_pending_checks WHERE exam_id = @id FOR UPDATE;", connection, transaction))
        {
            claim.Parameters.AddWithValue("id", first.ExamId);
            Assert.Equal(first.ExamId, await claim.ExecuteScalarAsync());
        }
        var submitting = SaveFinalAsync(first);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Wait for the actual PostgreSQL lock wait, not an arbitrary sleep, before simulating
        // the worker removing its claimed row. ON CONFLICT must retry insertion after deletion.
        while (true)
        {
            await using var blocked = fixture.DataSource.CreateCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                      AND query LIKE '%INSERT INTO exam.final_submissions%');
                """);
            if ((bool)(await blocked.ExecuteScalarAsync(deadline.Token))!) break;
            await Task.Delay(10, deadline.Token);
        }
        await using (var drain = new NpgsqlCommand(
            "DELETE FROM exam.collection_pending_checks WHERE exam_id = @id;", connection, transaction))
        {
            drain.Parameters.AddWithValue("id", first.ExamId);
            await drain.ExecuteNonQueryAsync(deadline.Token);
        }
        await transaction.CommitAsync(deadline.Token);
        await submitting.WaitAsync(deadline.Token);
        var recovered = new PostgresCollectionCompletionStore(fixture.DataSource);
        await recovered.ProcessPendingAsync(cancellationToken: deadline.Token);
        Assert.Single(await recovered.ReadEventsAsync(first.ExamId, 0, cancellationToken: deadline.Token));
    }

    private async Task SaveFinalAsync(ParticipationContext context)
    {
        var saved = await new PostgresSubmissionStore(fixture.DataSource).SaveSubmissionAsync(
            context.Submission(Id("collection-request"), Id("collection-final"), Answer("A")),
            DateTimeOffset.UtcNow, Id("collection-receipt"));
        Assert.Equal(Exam.Domain.Submissions.SubmissionPersistenceStatus.Saved, saved.Status);
    }

    private async Task SetStateAsync(string participationId, string state)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "UPDATE exam.participations SET business_state = @state WHERE participation_id = @id;");
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("id", participationId);
        await command.ExecuteNonQueryAsync();
    }
}
