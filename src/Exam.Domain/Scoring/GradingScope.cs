using Exam.Contracts.Grading;

namespace Exam.Domain.Scoring;

/// <summary>
/// 重算范围：某场考试的**全部最终答卷**及其当前计分状态。
///
/// 用途有两处：规则更新后确定要重算哪些答卷；发布前判断是否已全部对齐到目标规则版本
/// （`requirements.md`：重算期间旧表格失效，完成后统一更新，同表不得混用版本）。
/// 只读且**不返回原始答案**，因此范围判定不会改动任何答卷。
/// </summary>
public interface IGradingScope
{
    /// <summary>
    /// 列出该考试全部最终答卷的计分状态：从未计分的 GradedVersion 为 null，
    /// 计分失败过的标记 GradingFailed。
    /// </summary>
    ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(
        string examId,
        CancellationToken cancellationToken = default);
}
