using Exam.Contracts.Grading;

namespace Exam.Domain.Scoring;

/// <summary>
/// 规则版本与重算的范围判定。依据 requirements.md：
/// 「修改参考答案或计分标准须校验并建立新版本，对全部已收卷答卷重算，后续补收也使用该版本」、
/// 「重算期间旧表格失效并暂停查看、导出，完成后统一更新分数、排名和平均分样本；同表不得混用版本」。
///
/// 纯逻辑：只看答卷标识与已计分版本，**不读原始答案、不写数据库**，因此重算规划不会改动任何答案。
/// 计分结果持久化与后台消费需要数据库迁移，属后续交付；本类型是那一步的决策核心。
/// </summary>
public static class RegradePlanner
{
    /// <summary>
    /// 规划需要重算的答卷。按答卷与规则版本去重：已按目标版本计分且未失败的答卷不进 Pending，
    /// 规则未变时不会重复计分；补交的答卷（从未计分）按目标版本计分，即「补收使用最新版本」。
    /// </summary>
    public static RegradePlan Plan(long targetVersion, IReadOnlyList<SubmissionGradingState> submissions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        ArgumentNullException.ThrowIfNull(submissions);

        var pending = new List<RegradeItem>();
        var upToDate = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var submission in submissions)
        {
            ArgumentNullException.ThrowIfNull(submission);

            if (!seen.Add(submission.SubmissionId))
            {
                // 同一答卷出现两次会让成绩重复累计：只规划一次，并把重复本身报出来。
                duplicates.Add(submission.SubmissionId);
                continue;
            }

            var reason = Classify(submission, targetVersion);
            if (reason is null)
            {
                upToDate.Add(submission.SubmissionId);
                continue;
            }

            pending.Add(new RegradeItem(submission.SubmissionId, reason.Value));
        }

        return new RegradePlan(targetVersion, pending, upToDate, duplicates);
    }

    /// <summary>
    /// 发布前判定：全部答卷都按目标版本计分、且没有重复记录时才就绪。
    /// 只要还有未完成项就返回 not ready，避免把不同版本的成绩混在一张表里发布。
    /// </summary>
    public static GradingReadiness EvaluateReadiness(
        long targetVersion,
        IReadOnlyList<SubmissionGradingState> submissions)
    {
        var plan = Plan(targetVersion, submissions);
        var blockers = new List<string>();

        if (plan.DuplicateSubmissionIds.Count > 0)
        {
            blockers.Add($"存在重复的答卷记录，会重复累计成绩：{string.Join('、', plan.DuplicateSubmissionIds)}。");
        }

        AddBlocker(
            blockers,
            plan,
            RegradeReason.NeverGraded,
            count => $"{count} 份答卷尚未计分。");

        AddBlocker(
            blockers,
            plan,
            RegradeReason.StaleVersion,
            count => $"{count} 份答卷仍按旧规则版本计分，需按版本 {targetVersion} 重算。");

        AddBlocker(
            blockers,
            plan,
            RegradeReason.GradingFailed,
            count => $"{count} 份答卷上次计分失败，必须先修复再重算。");

        return new GradingReadiness(targetVersion, blockers.Count == 0, plan.Pending, blockers);
    }

    private static RegradeReason? Classify(SubmissionGradingState submission, long targetVersion)
    {
        if (submission.GradingFailed)
        {
            return RegradeReason.GradingFailed;
        }

        if (submission.GradedVersion is not { } gradedVersion)
        {
            return RegradeReason.NeverGraded;
        }

        // 双向比较，不只是「旧版本要重算」：已计分版本**高于**目标版本也属于混用版本
        // （例如规则回退后，表里仍留着更新版本的分数），必须按目标版本重算。
        return gradedVersion != targetVersion ? RegradeReason.StaleVersion : null;
    }

    private static void AddBlocker(
        List<string> blockers,
        RegradePlan plan,
        RegradeReason reason,
        Func<int, string> describe)
    {
        var count = plan.Pending.Count(item => item.Reason == reason);
        if (count > 0)
        {
            blockers.Add(describe(count));
        }
    }
}
