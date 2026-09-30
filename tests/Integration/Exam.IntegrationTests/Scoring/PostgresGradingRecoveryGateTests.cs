using System.Diagnostics;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Server.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Exam.IntegrationTests.Scoring;

public sealed partial class PostgresGradingPersistenceTests
{
    [Fact]
    public async Task ProductionHostedGrading_WaitsForRecoveryThenConsumesAndRecalculatesWithoutChangingSubmission()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-a");
        try
        {
            var original = await ReadCanonicalSubmissionRowAsync();
            var paused = new PausedGradingInputs(new PostgresGradingInputs(fixture.DataSource));
            await using var services = ProductionGradingServices(inputs: paused);
            var workers = GatedGradingWorkers(services);
            var guard = services.GetRequiredService<BusinessRecoveryGuard>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                foreach (var worker in workers) await worker.StartAsync(timeout.Token);
                await AssertRecoveryBlocksGradingAsync(services, workers, timeout.Token);

                guard.Observe([new("synthetic-recovery", false, "合成恢复核对失败", true)], BoundaryNow);
                await AssertRecoveryBlocksGradingAsync(services, workers, timeout.Token);
                Assert.False(paused.Entered.Task.IsCompleted);
                Assert.Equal(original, await ReadCanonicalSubmissionRowAsync());

                guard.Observe([new("synthetic-recovery", true, "合成恢复核对通过", true)], BoundaryNow);
                // 在实际领取后暂停读取答卷，确保重算线程有机会独立生成版本 3 的持久任务。
                // 如果只等计分结果，旧任务也能使用版本 3 完成，会漏掉重算线程没有接线的错误。
                await paused.Entered.Task.WaitAsync(timeout.Token);
                await WaitForGradingDatabaseConditionAsync("""
                    SELECT EXISTS (
                        SELECT 1 FROM exam.grading_outbox
                         WHERE submission_id = $1 AND grading_version = 3);
                    """, timeout.Token);
                paused.Resume.TrySetResult();
                await WaitForGradingDatabaseConditionAsync("""
                    SELECT count(*) = 2 AND bool_and(completed_at IS NOT NULL AND last_error IS NULL)
                      FROM exam.grading_outbox WHERE submission_id = $1;
                    """, timeout.Token);

                var result = Assert.Single(await services.GetRequiredService<IGradingResultStore>()
                    .ListAsync(ExamId, 3, timeout.Token));
                Assert.Equal("3", result.Grade.ExactTotalScore);
                var state = Assert.Single(await services.GetRequiredService<IGradingScope>()
                    .ListStatesAsync(ExamId, timeout.Token));
                Assert.Equal(3, state.GradedVersion);
                Assert.False(state.GradingFailed);
                // 比较完整持久答卷行，包含原答案、提交标识、接收时间、回执及其原始版本。
                Assert.Equal(original, await ReadCanonicalSubmissionRowAsync());
            }
            finally
            {
                paused.Resume.TrySetResult();
                await StopGatedGradingWorkersAsync(workers);
            }
        }
        finally { await DeleteServerStateAsync(); }
    }

    [Fact]
    public async Task ProductionHostedGrading_StopsWhileRecoveryIsBlockedWithoutClaimingOrRequeueing()
    {
        await SeedAsync();
        await SetActiveNodeAsync("node-a");
        try
        {
            var original = await ReadCanonicalSubmissionRowAsync();
            await using var services = ProductionGradingServices();
            var workers = GatedGradingWorkers(services);
            services.GetRequiredService<BusinessRecoveryGuard>()
                .Observe([new("synthetic-recovery", false, "合成恢复核对失败", true)], BoundaryNow);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                foreach (var worker in workers) await worker.StartAsync(timeout.Token);
                await AssertRecoveryBlocksGradingAsync(services, workers, timeout.Token);
            }
            finally { await StopGatedGradingWorkersAsync(workers); }

            Assert.Null(services.GetRequiredService<GradingWorker>().ExecuteTask);
            Assert.Null(services.GetRequiredService<RecalculationWorker>().ExecuteTask);
            Assert.True(await GradingDatabaseConditionAsync(UnchangedGradingWorkSql, timeout.Token));
            Assert.Equal(original, await ReadCanonicalSubmissionRowAsync());
        }
        finally { await DeleteServerStateAsync(); }
    }

    private static BackgroundService[] GatedGradingWorkers(ServiceProvider services)
    {
        // 解析生产注册的全部 hosted services，确保包装器及其真实依赖都能构造。
        var hosted = services.GetServices<IHostedService>().ToArray();
        Assert.DoesNotContain(hosted, service => service is GradingWorker or RecalculationWorker);
        return
        [
            Assert.Single(hosted.OfType<RecoveryGatedWorker<GradingWorker>>()),
            Assert.Single(hosted.OfType<RecoveryGatedWorker<RecalculationWorker>>())
        ];
    }

    private async Task AssertRecoveryBlocksGradingAsync(ServiceProvider services, BackgroundService[] workers,
        CancellationToken cancellationToken)
    {
        // 有界轮询跨过门禁的多个观察周期，逐次检查真实任务与成绩，避免一次即时断言遗漏异步启动。
        var observation = Stopwatch.StartNew();
        do
        {
            Assert.False(services.GetRequiredService<BusinessRecoveryGuard>().Snapshot.Ready);
            Assert.All(workers, worker =>
            {
                Assert.NotNull(worker.ExecuteTask);
                Assert.False(worker.ExecuteTask!.IsCompleted);
            });
            Assert.Null(services.GetRequiredService<GradingWorker>().ExecuteTask);
            Assert.Null(services.GetRequiredService<RecalculationWorker>().ExecuteTask);
            Assert.True(await GradingDatabaseConditionAsync(UnchangedGradingWorkSql, cancellationToken));
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        } while (observation.Elapsed < TimeSpan.FromMilliseconds(600));
    }

    private static async Task StopGatedGradingWorkersAsync(BackgroundService[] workers)
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var worker in workers) await worker.StopAsync(shutdown.Token);
        // StopAsync 在超时取消后可能直接返回，另等执行任务，才能确认取消确实让门禁退出。
        await Task.WhenAll(workers.Select(worker => worker.ExecuteTask ?? Task.CompletedTask))
            .WaitAsync(shutdown.Token);
        Assert.All(workers, worker => Assert.True(worker.ExecuteTask?.IsCompletedSuccessfully));
    }

    private async Task WaitForGradingDatabaseConditionAsync(string sql, CancellationToken cancellationToken)
    {
        while (!await GradingDatabaseConditionAsync(sql, cancellationToken))
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
    }

    private async Task<bool> GradingDatabaseConditionAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(SubmissionId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private const string UnchangedGradingWorkSql = """
        SELECT count(*) = 1
           AND bool_and(grading_version = 1 AND attempt_count = 0 AND claimed_at IS NULL
                        AND lease_expires_at IS NULL AND completed_at IS NULL AND last_error IS NULL)
           AND NOT EXISTS (SELECT 1 FROM exam.grading_results WHERE submission_id = $1)
          FROM exam.grading_outbox WHERE submission_id = $1;
        """;
}
