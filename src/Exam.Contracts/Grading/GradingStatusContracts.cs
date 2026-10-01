namespace Exam.Contracts.Grading;

/// <summary>
/// 某场考试**在本次读取时**的计分状态：当前已发布规则版本、完整答卷范围与计分完成情况。
///
/// 这是诊断面，配合报表整体发布使用：报表门只会给出「计分尚未完成」，
/// 本报告说明**还有哪些答卷没有按当前版本计分、原因是什么、是否被更高版本成绩阻断**，
/// 供考务端在发布前定位，而不是等发布被拒后无从下手。
///
/// 它是**范围快照，不是可跨请求缓存的授权**：规则发布或补收答卷都会使其过期；
/// 报表查看与导出仍须在访问时自行核对当前版本与全部最终答卷集合的一致性，
/// 也不得把 <see cref="Readiness"/> 当作 A06 首次发布规则的前置条件。
/// </summary>
public sealed record GradingStatusReport(
    string ExamId,
    long? CurrentGradingVersion,
    int FinalSubmissionCount,
    int GradedAtCurrentVersion,
    GradingReadiness? Readiness,
    IReadOnlyList<string> DuplicateSubmissionIds,
    DateTimeOffset ObservedAt);
