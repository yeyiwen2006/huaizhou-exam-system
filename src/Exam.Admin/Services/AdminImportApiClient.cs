using System.Net.Http;
using Exam.Contracts.Importing;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    /// <summary>
    /// 服务端预检：校验清单与答案、逐题编译、逐题抽取选项坐标，返回**逐题摘要**供导入预览确认。
    /// 不组包、不入库、不发布——确认后才调用发布。
    /// </summary>
    public Task<ImportPipelineResult> PreviewPaperImportAsync(
        string packageRoot,
        string examId,
        long expectedPaperVersion,
        string compilationDirectory,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendAsync<ImportPipelineResult>(HttpMethod.Post, "api/v1/imports/preview", new
        {
            packageRoot,
            examId,
            expectedPaperVersion,
            compilationOutputDirectory = compilationDirectory,
            reason
        }, cancellationToken);

    /// <summary>
    /// 服务端发布：在预检同一准备结果上继续完成组包加密、版本资源入库，
    /// 并由 A06 的原子入口在**同一事务**里发布试卷版本与计分规则。
    ///
    /// 加密收件人取**服务器受管节点**的公钥，本请求不传证书。
    /// 该考试已发布过试卷版本时（开考后修订）必须逐题声明
    /// <paramref name="declaredChangedQuestionIds"/>：不声明整卷都变、让全体考生复核全卷；
    /// 漏报变更题则该题考生收不到复核提示。
    /// </summary>
    public Task<ImportPipelineResult> PublishPaperImportAsync(
        string packageRoot,
        string examId,
        long expectedPaperVersion,
        string compilationDirectory,
        string packageOutputDirectory,
        string reason,
        IReadOnlyList<string>? declaredChangedQuestionIds = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<ImportPipelineResult>(HttpMethod.Post, "api/v1/imports/publish", new
        {
            packageRoot,
            examId,
            expectedPaperVersion,
            compilationOutputDirectory = compilationDirectory,
            packageOutputDirectory,
            declaredChangedQuestionIds = declaredChangedQuestionIds is { Count: > 0 } ? declaredChangedQuestionIds : null,
            reason
        }, cancellationToken);

    /// <summary>
    /// 只改**答案与计分规则**（不换试卷版本、不重新组包）：发布新的规则版本并按新版本触发重算。
    /// <paramref name="gradingVersion"/> 传 0 表示由服务器取当前规则版本的下一版。
    /// 返回结果把「规则已发布」与「重算是否就绪」分开报告。
    /// </summary>
    public Task<GradingRuleRevisionResult> ReviseGradingRulesAsync(
        string examId,
        string packageRoot,
        long gradingVersion,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendAsync<GradingRuleRevisionResult>(HttpMethod.Post,
            $"api/v1/imports/{Uri.EscapeDataString(examId)}/rule-revisions", new
            {
                packageRoot,
                gradingVersion,
                reason
            }, cancellationToken);
}
