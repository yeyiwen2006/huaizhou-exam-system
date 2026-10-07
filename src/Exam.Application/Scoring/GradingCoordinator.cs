using Exam.Domain.Scoring;

namespace Exam.Application.Scoring;

/// <summary>一轮计分批次结果；只有结果持久化且当前领取代次结项成功才计入 Graded。</summary>
public sealed record GradingBatchReport(int Claimed, int Graded, int Failed, IReadOnlyList<string> FailureMessages)
{
    public static GradingBatchReport Empty { get; } = new(0, 0, 0, []);
}

/// <summary>
/// 有界批次协调器：收卷 outbox → 冻结答卷和最新有效规则 → 精确计分 → 结果持久化 → 结项。
/// 调度由业务宿主的后台工作器承担，并受主备写门、重算范围及报表版本校验保护。
/// </summary>
public sealed class GradingCoordinator
{
    public const int MaxBatchSize = 512;
    private readonly IGradingTaskSource _tasks;
    private readonly IGradingResultStore _results;
    private readonly IGradingInputs _inputs;
    private readonly int _batchSize;
    private readonly TimeSpan _lease;
    private readonly TimeProvider _timeProvider;

    public GradingCoordinator(IGradingTaskSource tasks, IGradingResultStore results, IGradingInputs inputs,
        int batchSize = 32, TimeSpan? lease = null, TimeProvider? timeProvider = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        if (batchSize is < 1 or > MaxBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _lease = lease ?? TimeSpan.FromMinutes(5);
        if (_lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        _batchSize = batchSize;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<GradingBatchReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claimed = await _tasks.ClaimAsync(_batchSize, _timeProvider.GetUtcNow(), _lease, cancellationToken);
        if (claimed.Count == 0) return GradingBatchReport.Empty;
        var graded = 0;
        var failures = new List<string>();
        foreach (var task in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? failure;
            try
            {
                failure = await ProcessAsync(task, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 不把取消伪装成业务失败；已经领取的任务保留，租约到期可恢复。
                throw;
            }
            catch (Exception)
            {
                // 异常可能携带源答案、数据库参数、连接串；仅对外提供稳定脱敏的业务错误。
                failure = "计分依赖或持久化失败，任务保留待重试。";
            }

            if (failure is null)
            {
                graded++;
                continue;
            }
            try
            {
                if (!await _tasks.FailAsync(task, failure, cancellationToken))
                    failure += " 原领取代次已失效或任务已结束。";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failure += " 失败状态未能持久化，原租约保留待超时恢复。";
            }
            failures.Add(failure);
        }
        return new(claimed.Count, graded, failures.Count, Array.AsReadOnly(failures.ToArray()));
    }

    private async ValueTask<string?> ProcessAsync(PendingGradingTask task, CancellationToken cancellationToken)
    {
        task.ValidateClaim();
        var submission = await _inputs.LoadSubmissionAsync(task.SubmissionId, cancellationToken);
        if (submission is null) return "找不到对应的最终答卷。";
        if (submission.SubmissionId != task.SubmissionId) return "计分任务与最终答卷标识不一致。";
        var published = await _inputs.LoadRulesAsync(submission.ExamId, cancellationToken);
        if (published is null) return "该考试尚未发布计分规则，等待发布后重试。";
        if (published.Rules.ExamId != submission.ExamId) return "已发布规则与答卷的考试不一致。";
        if (published.GradingVersion < task.GradingVersion)
            return "已发布计分版本落后于任务目标版本，禁止降版本计分。";
        var outcome = published.Rules.Grade(submission, published.GradingVersion);
        if (!outcome.Succeeded || outcome.Grade is null)
            return "冻结答卷与已发布规则不匹配或计分校验失败。";
        await _results.SaveAsync(new(outcome.Grade, _timeProvider.GetUtcNow()), cancellationToken);
        // 先落库再结项。重试时同版本同结果幂等，旧消费者不能结项新租约。
        return await _tasks.CompleteAsync(task, cancellationToken)
            ? null : "计分任务领取代次已失效，不能声明本任务完成。";
    }
}
