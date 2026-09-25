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

    /// <summary>复制不可变成绩快照，防止内存适配器保存调用方仍能修改的集合。</summary>
    public StoredGradingResult Snapshot()
    {
        ArgumentNullException.ThrowIfNull(Grade);
        ArgumentException.ThrowIfNullOrWhiteSpace(SubmissionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Grade.CandidateId);
        ArgumentOutOfRangeException.ThrowIfLessThan(GradingVersion, 1);
        ArgumentNullException.ThrowIfNull(Grade.Questions);
        ArgumentNullException.ThrowIfNull(Grade.Subjects);
        ArgumentException.ThrowIfNullOrWhiteSpace(Grade.ExactTotalScore);
        if (Grade.Questions.Count == 0 || Grade.Subjects.Count == 0 ||
            Grade.Questions.Any(item => item is null) || Grade.Subjects.Any(item => item is null))
            throw new ArgumentException("计分结果必须包含完整的逐题与科目成绩。");
        return this with
        {
            Grade = Grade with
            {
                Questions = Array.AsReadOnly(Grade.Questions.ToArray()),
                Subjects = Array.AsReadOnly(Grade.Subjects.ToArray())
            },
            GradedAt = GradedAt.ToUniversalTime()
        };
    }

    /// <summary>规则版本只递增；同版本仅接受相同结果的幂等重试。</summary>
    public void EnsureCanReplace(StoredGradingResult previous)
    {
        if (SubmissionId != previous.SubmissionId || ExamId != previous.ExamId ||
            Grade.CandidateId != previous.Grade.CandidateId || GradingVersion < previous.GradingVersion ||
            (GradingVersion == previous.GradingVersion &&
             (Grade.ExactTotalScore != previous.Grade.ExactTotalScore ||
              !Grade.Questions.SequenceEqual(previous.Grade.Questions) ||
              !Grade.Subjects.SequenceEqual(previous.Grade.Subjects))))
            throw new InvalidOperationException("计分结果归属、版本或同版本内容冲突；原结果未被覆盖。");
    }
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
public sealed record PendingGradingTask(string SubmissionId, long GradingVersion, int AttemptCount)
{
    public const int MaxErrorLength = 2000;

    public void ValidateClaim()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SubmissionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(GradingVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(AttemptCount, 1);
    }

    // 调用方只传脱敏的业务错误，不传答案、密钥或完整异常文本。
    public static string LimitError(string? error)
    {
        var text = error ?? string.Empty;
        if (text.Length <= MaxErrorLength) return text;
        var length = char.IsHighSurrogate(text[MaxErrorLength - 1]) ? MaxErrorLength - 1 : MaxErrorLength;
        return text[..length];
    }
}

/// <summary>
/// 计分任务的领取与结项。实现必须满足三点：
/// 1. 重复领取不会让成绩重复累计（结果表按答卷去重，消费者也须幂等）；
/// 2. 失败只累加尝试次数与最近错误，**绝不删除待发任务**（需求：持久待发任务不得因重试次数被删除）；
/// 3. 领取用租约表达，AttemptCount 作为单调递增的领取代次，阻止旧消费者结项新租约。
/// </summary>
public interface IGradingTaskSource
{
    ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(
        int maxCount,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>仅当前领取代次可结项；false 表示租约已释放、被重领或任务已完成。</summary>
    ValueTask<bool> CompleteAsync(
        PendingGradingTask task,
        CancellationToken cancellationToken = default);

    /// <summary>仅当前领取代次可记录失败；不会改动已完成任务或其他消费者的租约。</summary>
    ValueTask<bool> FailAsync(
        PendingGradingTask task,
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
