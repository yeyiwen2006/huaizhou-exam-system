using Exam.Contracts.Grading;
using Exam.Contracts.Submissions;

namespace Exam.Domain.Scoring;

/// <summary>
/// 已持久化的计分结果。一份答卷只有一行：换规则版本重算时**更新**该行，成绩不会被重复累计。
/// 逐题与单科精确值以既有字符串形式保存，报表读取方不必反解分数格式即可整体切换发布。
/// </summary>
public sealed record StoredGradingResult(SubmissionGrade Grade, DateTimeOffset GradedAt)
{
    public string SubmissionId => Grade.SubmissionId;

    public string ExamId => Grade.ExamId;

    public long GradingVersion => Grade.GradingVersion;
}

/// <summary>计分结果的持久化与读取。写入按答卷去重，不产生第二行。</summary>
public interface IGradingResultStore
{
    ValueTask SaveAsync(StoredGradingResult result, CancellationToken cancellationToken = default);

    ValueTask<StoredGradingResult?> FindAsync(
        string submissionId,
        CancellationToken cancellationToken = default);

    /// <summary>按考试与规则版本读取全部结果；报表与重算完成度核对使用同一范围。</summary>
    ValueTask<IReadOnlyList<StoredGradingResult>> ListAsync(
        string examId,
        long gradingVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>待计分任务。来自 A05 的计分 outbox；同一（答卷，规则版本）只会有一行。</summary>
public sealed record PendingGradingTask(string SubmissionId, long GradingVersion, int AttemptCount);

/// <summary>
/// 计分任务的领取与结项。实现必须满足三点：
/// 1. 重复领取不会让成绩重复累计（结果表按答卷去重，消费者也须幂等）；
/// 2. 失败只累加尝试次数与最近错误，**绝不删除待发任务**（需求：持久待发任务不得因重试次数被删除）；
/// 3. 领取用租约表达，避免同一任务被并发消费。
/// </summary>
public interface IGradingTaskSource
{
    ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(
        int maxCount,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        string submissionId,
        long gradingVersion,
        CancellationToken cancellationToken = default);

    ValueTask FailAsync(
        string submissionId,
        long gradingVersion,
        string error,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 已发布的计分规则及其版本。需求要求「判分仍采用最新有效参考答案及规则」，
/// 因此计分用最新规则，并把该版本记在结果上，而不是沿用提交当时看到的版本。
/// </summary>
public sealed record PublishedGradingRules(ImportedGradingRules Rules, long GradingVersion);

/// <summary>计分所需的输入：冻结答卷与已发布的计分规则。</summary>
public interface IGradingInputs
{
    /// <summary>读取最终答卷（含冻结答案与回执标识）；不存在时返回 null。</summary>
    ValueTask<FinalSubmission?> LoadSubmissionAsync(
        string submissionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取该考试当前已发布的计分规则；返回 null 表示尚未发布规则，此时不得计分，
    /// 也不能用默认规则顶替。规则源数据损坏时抛出异常，由调用方记录为失败重试，不静默降级。
    /// </summary>
    ValueTask<PublishedGradingRules?> LoadRulesAsync(
        string examId,
        CancellationToken cancellationToken = default);
}
