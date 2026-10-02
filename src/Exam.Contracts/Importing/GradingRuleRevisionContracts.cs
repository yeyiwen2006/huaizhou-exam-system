using Exam.Contracts.Grading;

namespace Exam.Contracts.Importing;

/// <summary>
/// 只改**答案与计分规则**的修订输入（E01-C49）：材料包仍按导入包给出
/// （manifest.json 与 server-only/answer-key.json），但本次不换试卷版本、不重新组包。
///
/// <paramref name="GradingVersion"/> 是**本次发布出的规则版本**（与导入里的目标试卷版本同一约定）；
/// 传 0 表示由服务器取「当前规则版本 + 1」。版本必须前进，对不上直接拒绝，不会覆盖已有规则。
/// </summary>
public sealed record GradingRuleRevisionRequest(
    string PackageRoot,
    long GradingVersion,
    string Reason,
    string RequestId = "");

/// <summary>
/// 一次规则修订的结果。**「规则已发布」与「重算已完成」是两件事**，这里分开报告：
/// <paramref name="Succeeded"/> 只表示规则版本已经发布生效；
/// 重算范围、入队数量与就绪情况由 <paramref name="SubmissionCount"/>、
/// <paramref name="Enqueued"/> 与 <paramref name="Readiness"/> 如实给出，
/// 发布成功但重算没能跑起来时填 <paramref name="RecalculationError"/>，
/// 不把「规则已换」说成「成绩已重算完」。
/// </summary>
public sealed record GradingRuleRevisionResult(
    bool Succeeded,
    long? PublishedGradingVersion,
    IReadOnlyList<ImportPipelineIssue> Issues,
    int SubmissionCount = 0,
    int Enqueued = 0,
    GradingReadiness? Readiness = null,
    string? RecalculationError = null);
