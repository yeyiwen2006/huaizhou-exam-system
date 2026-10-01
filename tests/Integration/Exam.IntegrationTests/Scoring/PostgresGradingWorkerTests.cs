using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;

namespace Exam.IntegrationTests.Scoring;

/// <summary>
/// D05 生产后台消费接线的 PostgreSQL 集成用例：主节点写门、持久任务消费与重算对账。
///
/// 复用同类的夹具与种子（已发布计分版本 3、答卷初始计分版本 1、outbox 中一条目标版本 1 的任务），
/// 因此这些用例验证的正是生产后台在真实数据库上的行为，而不是替身。
/// </summary>
public sealed partial class PostgresGradingPersistenceTests
{
    private const string ServerId = "pg-grading-server";

    [Fact]
    public async Task PublicationDirectory_ListsExamsWithCurrentPublishedVersion()
    {
        await SeedAsync();

        var published = await new PostgresGradingPublicationDirectory(fixture.DataSource).ListPublishedAsync();

        var exam = Assert.Single(published);
        Assert.Equal(ExamId, exam.ExamId);
        Assert.Equal(3, exam.GradingVersion);
    }

    [Fact]
    public async Task RecalculationProcessor_OnActiveNode_EnqueuesStaleResultOnce()
    {
        await SeedAsync();
        var processor = RecalculationProcessor(Gate("node-a", allowUninitialized: true));
        var before = await OutboxCountAsync();

        Assert.Equal(1, await processor.ReconcileOnceAsync());
        Assert.Equal(before + 1, await OutboxCountAsync());

        // 成绩尚未因这一轮入队而变化，重复对账不得再产生任务。
        Assert.Equal(0, await processor.ReconcileOnceAsync());
        Assert.Equal(before + 1, await OutboxCountAsync());
    }

    [Fact]
    public async Task RecalculationProcessor_OnNonActiveNode_WritesNothing()
    {
        await SeedAsync();
        var processor = RecalculationProcessor(Gate("node-a", allowUninitialized: false));
        var before = await OutboxCountAsync();

        Assert.Equal(0, await processor.ReconcileOnceAsync());
        Assert.Equal(before, await OutboxCountAsync());
    }

    [Fact]
    public async Task RecalculationProcessor_OnStandbyNode_WritesNothingWhileAnotherNodeIsActive()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-b");
        try
        {
            var before = await OutboxCountAsync();
            var processor = RecalculationProcessor(Gate("node-a", allowUninitialized: false));

            Assert.Equal(0, await processor.ReconcileOnceAsync());
            Assert.Equal(before, await OutboxCountAsync());
        }
        finally
        {
            await DeleteServerStateAsync();
        }
    }

    [Fact]
    public async Task GradingProcessor_OnActiveNode_GradesPendingTaskWithLatestPublishedRules()
    {
        await SeedAsync();
        var processor = GradingProcessor(Gate("node-a", allowUninitialized: true));

        Assert.Equal(1, (await processor.RunOnceAsync()).Graded);

        // 任务的目标版本是 1，但计分采用当前已发布版本 3，成绩按实际版本落库。
        var results = await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3);
        Assert.Single(results);
    }

    [Fact]
    public async Task GradingProcessor_OnStandbyNode_LeavesTaskForTheActiveNode()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-b");
        try
        {
            var processor = GradingProcessor(Gate("node-a", allowUninitialized: false));

            Assert.Equal(0, (await processor.RunOnceAsync()).Claimed);
            Assert.Empty(await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3));

            // 备库节点既没有消费也没有删除任务：活动节点仍能领到它。
            var claimed = await new PostgresGradingTaskSource(fixture.DataSource)
                .ClaimAsync(10, BoundaryNow, TimeSpan.FromMinutes(5));
            Assert.Single(claimed);
        }
        finally
        {
            await DeleteServerStateAsync();
        }
    }

    [Fact]
    public async Task RecalculationProcessor_OneFailingExam_DoesNotBlockTheOthers()
    {
        await SeedAsync();
        const string poison = "pg-poison-exam";
        var processor = new RecalculationProcessor(
            new FixedPublishedDirectory([new(poison, 1), new(ExamId, 3)]),
            new RecalculationScheduler(
                new PoisonScope(fixture.DataSource, poison),
                new PostgresRecalculationQueue(fixture.DataSource)),
            Gate("node-a", allowUninitialized: true),
            NullLogger<RecalculationProcessor>.Instance);

        // 第一场对账必然失败：不得中断本轮，后一场仍须完成入队。
        var enqueued = await processor.ReconcileOnceAsync();

        Assert.Equal(1, enqueued);
    }

    /// <summary>固定列出两场考试，用来确定性地构造「一场坏、一场好」的对账场景。</summary>
    private sealed class FixedPublishedDirectory(IReadOnlyList<PublishedGradingVersion> published)
        : IPublishedGradingDirectory
    {
        public ValueTask<IReadOnlyList<PublishedGradingVersion>> ListPublishedAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(published);
    }

    /// <summary>指定考试读取范围时抛错，其余委托真实实现。</summary>
    private sealed class PoisonScope(Npgsql.NpgsqlDataSource dataSource, string poisonExamId) : IGradingScope
    {
        private readonly PostgresGradingScope _inner = new(dataSource);

        public ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(
            string examId, CancellationToken cancellationToken = default) =>
            string.Equals(examId, poisonExamId, StringComparison.Ordinal)
                ? throw new InvalidDataException("合成的坏数据：该考试范围记录损坏。")
                : _inner.ListStatesAsync(examId, cancellationToken);
    }

    private ActiveNodeGuard Gate(string nodeId, bool allowUninitialized, long epoch = 1) =>
        new(fixture.DataSource,
            new ServerIdentityOptions { ServerId = ServerId, NodeId = nodeId, ServerEpoch = epoch },
            allowUninitialized);

    private GradingProcessor GradingProcessor(ActiveNodeGuard gate) =>
        new(new GradingCoordinator(
                new PostgresGradingTaskSource(fixture.DataSource),
                new PostgresGradingResultStore(fixture.DataSource),
                new PostgresGradingInputs(fixture.DataSource),
                timeProvider: new FixedTimeProvider(BoundaryNow)),
            gate,
            NullLogger<GradingProcessor>.Instance);

    private RecalculationProcessor RecalculationProcessor(ActiveNodeGuard gate) =>
        new(new PostgresGradingPublicationDirectory(fixture.DataSource),
            new RecalculationScheduler(
                new PostgresGradingScope(fixture.DataSource),
                new PostgresRecalculationQueue(fixture.DataSource)),
            gate,
            NullLogger<RecalculationProcessor>.Instance);

    private async Task<int> OutboxCountAsync()
    {
        await using var command = fixture.DataSource.CreateCommand("SELECT count(*) FROM exam.grading_outbox;");
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task SetActiveNodeAsync(string activeNodeId)
    {
        // server_state 与 server_nodes 互为外键（后者 DEFERRABLE INITIALLY DEFERRED），
        // 必须放在同一条命令里插入，让延迟校验在批次结束时统一成立。
        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.server_state
                (server_id, active_node_id, server_epoch, protection_mode, state_version, updated_at)
            VALUES (@server_id, @active_node_id, 1, 'SynchronousDualNode', 1,
                    TIMESTAMPTZ '2026-09-25 03:00:00+00')
            ON CONFLICT (server_id) DO UPDATE
                SET active_node_id = EXCLUDED.active_node_id,
                    state_version = exam.server_state.state_version + 1,
                    updated_at = EXCLUDED.updated_at;

            INSERT INTO exam.server_nodes
                (server_id, node_id, node_role, reachable, database_in_recovery,
                 replay_lag_bytes, key_material_ready, certificate_ready, observed_at)
            VALUES (@server_id, @active_node_id, 'Primary', true, false, 0, true, true,
                    TIMESTAMPTZ '2026-09-25 03:00:00+00')
            ON CONFLICT (server_id, node_id) DO NOTHING;
            """);
        command.Parameters.AddWithValue("server_id", NpgsqlDbType.Text, ServerId);
        command.Parameters.AddWithValue("active_node_id", NpgsqlDbType.Text, activeNodeId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>server_state 不在夹具的清理范围内，写入后必须删除，避免影响同类其它用例。</summary>
    private async Task DeleteServerStateAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            "DELETE FROM exam.server_state WHERE server_id = @server_id;");
        command.Parameters.AddWithValue("server_id", NpgsqlDbType.Text, ServerId);
        await command.ExecuteNonQueryAsync();
    }
}
