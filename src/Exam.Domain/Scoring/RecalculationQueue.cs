namespace Exam.Domain.Scoring;

/// <summary>
/// 重算专用入队口，不改变 收卷 正常收卷的 IGradingTaskQueue 去重语义。
/// 以考试/答卷/目标版本为边界；缺失或旧成绩可排队，已完成但缺失成绩的任务可原子重开。
/// 未完成或仍持有有效租约的任务不得被重置；相同或更新版本结果存在时不重复排队。
/// </summary>
public interface IRecalculationQueue
{
    /// <returns>仅新建或重新打开任务时返回 true。</returns>
    ValueTask<bool> EnsureQueuedAsync(string examId, string submissionId, long targetVersion,
        CancellationToken cancellationToken = default);
}
