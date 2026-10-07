namespace Exam.Contracts.Grading;

/// <summary>需要重新计分的原因。</summary>
public enum RegradeReason
{
    /// <summary>从未计分过（新收到的补交答卷）。</summary>
    NeverGraded,

    /// <summary>按旧规则版本计过分，规则已更新。</summary>
    StaleVersion,

    /// <summary>上次计分失败。</summary>
    GradingFailed,

    /// <summary>已有更高版本成绩：目标已过期或状态冲突，禁止按较低目标入队。</summary>
    VersionAhead
}

/// <summary>
/// 一份答卷实际持久化成绩的当前状态；没有成绩时版本为 null，不能根据任务完成推定。判定重算范围只需要这些信息，**不需要也不携带原始答案**，
/// 因此重算规划不会改动任何答案。
/// </summary>
public sealed record SubmissionGradingState(
    string SubmissionId,
    long? GradedVersion,
    bool GradingFailed = false);

/// <summary>一份需要重新计分的答卷及原因。</summary>
public sealed record RegradeItem(string SubmissionId, RegradeReason Reason);

/// <summary>
/// 重算计划。按答卷与规则版本去重：已按目标版本计分且未失败的答卷不出现在 Pending 里，
/// 避免规则未变时重复计分。DuplicateSubmissionIds 记录输入里重复出现的答卷标识——
/// 重复会让成绩重复累计，必须显式报出而不是静默去重。
/// </summary>
public sealed record RegradePlan(
    long TargetVersion,
    IReadOnlyList<RegradeItem> Pending,
    IReadOnlyList<string> UpToDate,
    IReadOnlyList<string> DuplicateSubmissionIds)
{
    /// <summary>是否全部答卷都已按目标版本计分。</summary>
    public bool IsComplete => Pending.Count == 0 && DuplicateSubmissionIds.Count == 0;

    public int TotalCount => Pending.Count + UpToDate.Count;
}

/// <summary>
/// 已发布规则对应的成绩快照就绪判定；不是 版本服务 首次发布规则的前置条件。对应 requirements.md「重算期间旧表格失效并暂停查看、导出，完成后统一更新…
/// 同表不得混用版本」：只要还有答卷没按目标版本计分，就不得发布，避免发布混合版本的成绩。
/// </summary>
public sealed record GradingReadiness(
    long TargetVersion,
    bool RecalculationReady,
    IReadOnlyList<RegradeItem> Outstanding,
    IReadOnlyList<string> Blockers);
