using Exam.Domain.Scoring;

namespace Exam.Application.Scoring;

/// <summary>一轮计分批次的执行结果，供监测、CLI 与日志使用。</summary>
public sealed record GradingBatchReport(
    int Claimed,
    int Graded,
    int Failed,
    IReadOnlyList<string> FailureMessages)
{
    public static GradingBatchReport Empty { get; } = new(0, 0, 0, []);
}

/// <summary>
/// 后台计分协调器：领取计分任务 → 读取冻结答卷与已发布规则 → 精确计分 → 落库 → 结项。
///
/// 依据 requirements.md 与执行指南：
/// 1. **判分采用最新有效参考答案及规则**（「提交记录实际使用版本…判分仍采用最新有效参考答案及规则」），
///    因此结果按当前已发布的计分版本标记，而不是提交时记录的版本；旧版本的任务在产出最新版本结果后即可结项。
/// 2. **单条失败不阻塞其他任务**：每条任务独立处理，失败只记入结果并保留待办，绝不删除任务。
/// 3. **先落库再结项**：如果先结项后落库，落库失败就会永久丢失这份成绩；反过来做，
///    重复领取时按答卷去重的结果表保证不会重复累计。
/// 4. 无状态、有界：每次只领取一批（batchSize 上限），不无限循环，由调用方按调度节奏反复调用。
/// </summary>
public sealed class GradingCoordinator
{
    public const int MaxBatchSize = 512;

    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);

    private readonly IGradingTaskSource _tasks;
    private readonly IGradingResultStore _results;
    private readonly IGradingInputs _inputs;
    private readonly int _batchSize;
    private readonly TimeSpan _lease;
    private readonly TimeProvider _timeProvider;

    public GradingCoordinator(
        IGradingTaskSource tasks,
        IGradingResultStore results,
        IGradingInputs inputs,
        int batchSize = 32,
        TimeSpan? lease = null,
        TimeProvider? timeProvider = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _results = results ?? throw new ArgumentNullException(nameof(results));
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));

        if (batchSize < 1 || batchSize > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var effectiveLease = lease ?? DefaultLease;
        if (effectiveLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }

        _batchSize = batchSize;
        _lease = effectiveLease;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>执行一轮：领取至多一批任务并逐条处理。返回本批统计。</summary>
    public async ValueTask<GradingBatchReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var claimed = await _tasks.ClaimAsync(_batchSize, now, _lease, cancellationToken);
        if (claimed.Count == 0)
        {
            return GradingBatchReport.Empty;
        }

        var graded = 0;
        var failures = new List<string>();

        foreach (var task in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 每条任务单独隔离：一条失败不得影响本批其他任务。
            var failure = await ProcessAsync(task, cancellationToken);
            if (failure is null)
            {
                graded++;
                continue;
            }

            await _tasks.FailAsync(task.SubmissionId, task.GradingVersion, failure, cancellationToken);
            failures.Add($"{task.SubmissionId}: {failure}");
        }

        return new GradingBatchReport(claimed.Count, graded, failures.Count, failures);
    }

    /// <summary>处理单条任务；成功返回 null，失败返回可直接记录的原因。</summary>
    private async ValueTask<string?> ProcessAsync(PendingGradingTask task, CancellationToken cancellationToken)
    {
        var submission = await _inputs.LoadSubmissionAsync(task.SubmissionId, cancellationToken);
        if (submission is null)
        {
            // 不删除任务：保留可见并记录原因，由考务确认是数据缺失还是需要人工处理。
            return "找不到对应的最终答卷。";
        }

        var published = await _inputs.LoadRulesAsync(submission.ExamId, cancellationToken);
        if (published is null)
        {
            // 规则尚未发布属可恢复情况：保留任务，等规则发布后按最新版本计分。
            return "该考试尚未发布计分规则，等待发布后重试。";
        }

        if (!string.Equals(published.Rules.ExamId, submission.ExamId, StringComparison.Ordinal))
        {
            return $"已发布规则属于考试 {published.Rules.ExamId}，与答卷的考试 {submission.ExamId} 不一致。";
        }

        var outcome = published.Rules.Grade(submission, published.GradingVersion);
        if (!outcome.Succeeded || outcome.Grade is null)
        {
            return outcome.FailureReason ?? "计分失败。";
        }

        await _results.SaveAsync(
            new StoredGradingResult(outcome.Grade, _timeProvider.GetUtcNow()),
            cancellationToken);

        // 先落库再结项；重复领取会重新落库，但结果表按答卷去重，不会重复累计。
        await _tasks.CompleteAsync(task.SubmissionId, task.GradingVersion, cancellationToken);
        return null;
    }
}
