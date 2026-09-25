using Exam.Contracts.Grading;

namespace Exam.Domain.Scoring;

/// <summary>
/// 只读的重算规划：全部最终答卷必须对齐到同一个已发布目标版本，不读取或改写原始答案。
/// A06 版本只递增；恢复旧规则内容必须发布新版本，不能把更高版本成绩降版覆盖。
/// </summary>
public static class RegradePlanner
{
    public static RegradePlan Plan(long targetVersion, IReadOnlyList<SubmissionGradingState> submissions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        ArgumentNullException.ThrowIfNull(submissions);
        var snapshot = submissions.ToArray();
        foreach (var submission in snapshot)
        {
            ArgumentNullException.ThrowIfNull(submission);
            ArgumentException.ThrowIfNullOrWhiteSpace(submission.SubmissionId);
            if (submission.GradedVersion is { } version)
                ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        }

        var pending = new List<RegradeItem>();
        var current = new List<string>();
        var duplicates = new List<string>();
        foreach (var group in snapshot.GroupBy(item => item.SubmissionId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var rows = group.ToArray();
            if (rows.Length > 1) duplicates.Add(group.Key);
            // 保守合并重复输入，分类不依赖顺序；重复本身仍阻断就绪和调度写入。
            RegradeReason? reason = rows.Any(item => item.GradedVersion > targetVersion) ? RegradeReason.VersionAhead
                : rows.Any(item => item.GradingFailed) ? RegradeReason.GradingFailed
                : rows.Any(item => item.GradedVersion is null) ? RegradeReason.NeverGraded
                : rows.Any(item => item.GradedVersion < targetVersion) ? RegradeReason.StaleVersion
                : null;
            if (reason is { } value) pending.Add(new(group.Key, value));
            else current.Add(group.Key);
        }
        return new(targetVersion, Array.AsReadOnly(pending.ToArray()), Array.AsReadOnly(current.ToArray()),
            Array.AsReadOnly(duplicates.ToArray()));
    }

    public static GradingReadiness EvaluateReadiness(long targetVersion, IReadOnlyList<SubmissionGradingState> submissions) =>
        EvaluateReadiness(Plan(targetVersion, submissions));

    /// <summary>
    /// 复用同一不可变计划，避免排队期间再次读取可变输入产生不同的就绪结论。
    /// 结果是一个范围快照，不替代报表读取/导出时的当前版本与收卷集合一致性检查。
    /// </summary>
    public static GradingReadiness EvaluateReadiness(RegradePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var blockers = new List<string>();
        if (plan.DuplicateSubmissionIds.Count > 0)
            blockers.Add($"存在重复的答卷记录，共 {plan.DuplicateSubmissionIds.Count} 个标识；必须先排除范围歧义。");
        AddBlocker(blockers, plan, RegradeReason.NeverGraded, count => $"{count} 份答卷尚未计分。");
        AddBlocker(blockers, plan, RegradeReason.StaleVersion,
            count => $"{count} 份答卷仍按旧规则版本计分，需按版本 {plan.TargetVersion} 重算。");
        AddBlocker(blockers, plan, RegradeReason.GradingFailed, count => $"{count} 份答卷上次计分失败，必须先修复再重算。");
        AddBlocker(blockers, plan, RegradeReason.VersionAhead,
            count => $"{count} 份答卷的成绩版本高于目标版本；目标已过期或数据不一致，禁止降版本重算。");
        return new(plan.TargetVersion, plan.IsComplete && blockers.Count == 0, plan.Pending,
            Array.AsReadOnly(blockers.ToArray()));
    }

    private static void AddBlocker(List<string> blockers, RegradePlan plan, RegradeReason reason, Func<int, string> describe)
    {
        var count = plan.Pending.Count(item => item.Reason == reason);
        if (count > 0) blockers.Add(describe(count));
    }
}
