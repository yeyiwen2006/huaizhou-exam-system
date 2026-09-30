using Exam.Application.Scoring;
using Exam.Contracts.Submissions;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Server.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Exam.IntegrationTests.Scoring;

public sealed partial class PostgresGradingPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GradingProcessor_StateChangesDuringBatch_StopWritesAndNewEpochRecovers(bool cancel)
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-a");
        using var stop = new CancellationTokenSource();
        var paused = new PausedGradingInputs(new PostgresGradingInputs(fixture.DataSource));
        await using var services = ProductionGradingServices(inputs: paused);
        var processor = services.GetRequiredService<GradingProcessor>();
        var running = processor.RunOnceAsync(stop.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // 领取已提交之后允许主备状态变化；后续保存及结项必须重新在各自写事务内校验。
            await AdvanceServerEpochAsync();
            if (cancel)
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            else
            {
                paused.Resume.TrySetResult();
                Assert.Equal(1, (await running).Failed);
            }
            Assert.Null(await ReadCompletedAtAsync());
            Assert.Empty(await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3));

            // 保留原任务租约；旧进程不再领取，新进程到期接续，原答案及回执不可改变。
            Assert.Equal(0, (await processor.RunOnceAsync()).Claimed);
            await using var restarted = ProductionGradingServices(epoch: 2, now: BoundaryNow + BoundaryLease);
            var resumed = restarted.GetRequiredService<GradingProcessor>();
            Assert.Equal(1, (await resumed.RunOnceAsync()).Graded);
            var original = await new PostgresGradingInputs(fixture.DataSource).LoadSubmissionAsync(SubmissionId);
            Assert.Equal("submission-receipt-1", original!.Receipt.ReceiptId);
            Assert.Equal("A", Assert.Single(original.FrozenAnswers["q-001"].OptionIds));
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            finally { await DeleteServerStateAsync(); }
        }
    }

    [Fact]
    public async Task RecalculationProcessor_RechecksEpochAfterReadingPublishedDirectory()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-a");
        try
        {
            var before = await OutboxCountAsync();
            await using var services = ProductionGradingServices();
            var published = new ChangingPublicationDirectory(
                new PostgresGradingPublicationDirectory(fixture.DataSource), AdvanceServerEpochAsync);
            var processor = new RecalculationProcessor(published,
                services.GetRequiredService<RecalculationScheduler>(),
                Gate("node-a", false), NullLogger<RecalculationProcessor>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ReconcileOnceAsync());
            Assert.Equal(before, await OutboxCountAsync());
            await using var restarted = ProductionGradingServices(epoch: 2);
            Assert.Equal(1, await restarted.GetRequiredService<RecalculationProcessor>().ReconcileOnceAsync());
        }
        finally { await DeleteServerStateAsync(); }
    }

    [Fact]
    public async Task ProductionRegistration_EveryGradingWriteRejectsStaleEpoch()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-a");
        await using var services = ProductionGradingServices();
        var tasks = services.GetRequiredService<IGradingTaskSource>();
        try
        {
            var claimed = Assert.Single(await tasks.ClaimAsync(1, BoundaryNow, BoundaryLease));
            await AdvanceServerEpochAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.CompleteAsync(claimed).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.FailAsync(claimed, "合成失败").AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tasks.ClaimAsync(1, BoundaryNow + BoundaryLease, BoundaryLease).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IGradingResultStore>()
                .SaveAsync(Result(3, "3", BoundaryNow)).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IRecalculationQueue>()
                .EnsureQueuedAsync(ExamId, SubmissionId, 3).AsTask());
            Assert.Null(await ReadCompletedAtAsync());
            Assert.Null(await ReadLastErrorAsync());
            Assert.Equal(1, await OutboxCountAsync());
            Assert.Empty(await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3));
        }
        finally { await DeleteServerStateAsync(); }
    }

    [Fact]
    public async Task ProductionClaim_TerminatedWriteConnectionRollsBackStateLockAndClaimTogether()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-a");
        await using var services = ProductionGradingServices();
        await using var blocker = await fixture.DataSource.OpenConnectionAsync();
        await using (var install = new NpgsqlCommand("""
            CREATE FUNCTION exam.pause_grading_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(583083);
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER pause_grading_claim BEFORE UPDATE ON exam.grading_outbox
                FOR EACH ROW EXECUTE FUNCTION exam.pause_grading_claim();
            SELECT pg_advisory_lock(583083);
            """, blocker))
            await install.ExecuteNonQueryAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var claiming = services.GetRequiredService<IGradingTaskSource>()
            .ClaimAsync(1, BoundaryNow, BoundaryLease, stop.Token).AsTask();
        try
        {
            // 等到实际写语句已执行到触发器，而非用 sleep 推断它取得了状态锁。
            var pid = await WaitForBlockedClaimAsync(stop.Token);
            await using (var update = fixture.DataSource.CreateCommand("""
                SET lock_timeout = '100ms';
                UPDATE exam.server_state SET server_epoch = 2 WHERE server_id = 'pg-grading-server';
                """))
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            }
            await using (var terminate = fixture.DataSource.CreateCommand("SELECT pg_terminate_backend($1);"))
            {
                terminate.Parameters.AddWithValue(pid);
                Assert.True((bool)(await terminate.ExecuteScalarAsync())!);
            }
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => claiming);
            await AdvanceServerEpochAsync();
            await using (var state = fixture.DataSource.CreateCommand(
                             "SELECT attempt_count = 0 AND claimed_at IS NULL FROM exam.grading_outbox;"))
                Assert.True((bool)(await state.ExecuteScalarAsync())!);

            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(583083);", blocker))
                await release.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IGradingTaskSource>()
                .ClaimAsync(1, BoundaryNow, BoundaryLease).AsTask());
            await using var restarted = ProductionGradingServices(epoch: 2);
            var recovered = Assert.Single(await restarted.GetRequiredService<IGradingTaskSource>()
                .ClaimAsync(1, BoundaryNow, BoundaryLease));
            Assert.Equal(1, recovered.AttemptCount);
        }
        finally
        {
            stop.Cancel();
            try { await claiming; } catch (Exception error) when (error is NpgsqlException or OperationCanceledException) { }
            await using var cleanup = fixture.DataSource.CreateCommand("""
                DROP TRIGGER IF EXISTS pause_grading_claim ON exam.grading_outbox;
                DROP FUNCTION IF EXISTS exam.pause_grading_claim();
                """);
            await cleanup.ExecuteNonQueryAsync();
            await DeleteServerStateAsync();
        }
    }

    [Fact]
    public async Task GradingWorker_StartsConsumesPersistedTaskAndStopsWithoutLosingResult()
    {
        await SeedAsync();
        var time = new ObservedDelayTimeProvider();
        using var worker = new GradingWorker(GradingProcessor(Gate("node-a", true)), WorkerOptions,
            NullLogger<GradingWorker>.Instance, time);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(WorkerOptions.BusyDelay, await time.Delay.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3));
            Assert.NotNull(await ReadCompletedAtAsync());
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task GradingWorker_BacksOffFailedBatchAndKeepsTaskRetryable()
    {
        await SeedAsync();
        await using (var damage = fixture.DataSource.CreateCommand("DELETE FROM exam.version_events;"))
            await damage.ExecuteNonQueryAsync();
        var time = new ObservedDelayTimeProvider();
        using var worker = new GradingWorker(GradingProcessor(Gate("node-a", true)), WorkerOptions,
            NullLogger<GradingWorker>.Instance, time);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(WorkerOptions.ErrorDelay, await time.Delay.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Null(await ReadCompletedAtAsync());
            Assert.NotNull(await ReadLastErrorAsync());
            Assert.Single(await new PostgresGradingTaskSource(fixture.DataSource)
                .ClaimAsync(1, BoundaryNow, BoundaryLease));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private static GradingBackgroundOptions WorkerOptions => new(8, BoundaryLease,
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3));

    private ServiceProvider ProductionGradingServices(long epoch = 1, IGradingInputs? inputs = null,
        DateTimeOffset? now = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Exam"] = fixture.ConnectionString,
            ["ExamSecurity:OperatorToken"] = new string('x', 32),
            ["ExamSecurity:OperatorId"] = "synthetic-operator",
            ["Database:MaximumPoolSize"] = "16",
            ["Database:Capacity:PostgresMaxConnections"] = "100",
            ["Database:Capacity:PostgresReservedConnections"] = "3",
            ["Database:Capacity:MaximumServerProcesses"] = "1",
            ["Database:Capacity:OtherConnections"] = "0",
            ["Database:Capacity:OperationsReserveConnections"] = "10",
            ["HighAvailability:RequiredNodeIds:0"] = "node-a",
            ["HighAvailability:RequiredNodeIds:1"] = "node-b",
            ["Grading:LeaseSeconds"] = ((int)BoundaryLease.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now ?? BoundaryNow));
        var source = services.AddPostgresExamRuntime(configuration,
            new ServerIdentityOptions { ServerId = ServerId, NodeId = "node-a", ServerEpoch = epoch },
            allowUninitializedFailoverState: false);
        // 测试容器负责释放本次注册创建的连接池，避免将会话留给其他用例。
        services.AddSingleton<NpgsqlDataSource>(_ => source);
        if (inputs is not null) services.AddSingleton(inputs);
        return services.BuildServiceProvider();
    }

    private async Task<int> WaitForBlockedClaimAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var command = fixture.DataSource.CreateCommand("""
                SELECT pid FROM pg_locks
                 WHERE locktype = 'advisory' AND classid = 0 AND objid = 583083 AND NOT granted
                   AND database = (SELECT oid FROM pg_database WHERE datname = current_database());
                """);
            if (await command.ExecuteScalarAsync(cancellationToken) is int pid) return pid;
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private async Task AdvanceServerEpochAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            "UPDATE exam.server_state SET server_epoch = 2 WHERE server_id = 'pg-grading-server';");
        await command.ExecuteNonQueryAsync();
    }

    private sealed class PausedGradingInputs(IGradingInputs inner) : IGradingInputs
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<FinalSubmission?> LoadSubmissionAsync(string submissionId,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await inner.LoadSubmissionAsync(submissionId, cancellationToken);
        }

        public ValueTask<PublishedGradingRules?> LoadRulesAsync(string examId,
            CancellationToken cancellationToken = default) => inner.LoadRulesAsync(examId, cancellationToken);
    }

    private sealed class ChangingPublicationDirectory(IPublishedGradingDirectory inner, Func<Task> change)
        : IPublishedGradingDirectory
    {
        public async ValueTask<IReadOnlyList<PublishedGradingVersion>> ListPublishedAsync(
            CancellationToken cancellationToken = default)
        {
            var versions = await inner.ListPublishedAsync(cancellationToken);
            await change();
            return versions;
        }
    }

    private sealed class ObservedDelayTimeProvider : TimeProvider
    {
        public TaskCompletionSource<TimeSpan> Delay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            Delay.TrySetResult(dueTime);
            return timer;
        }
    }
}
