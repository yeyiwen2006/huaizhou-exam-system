using System.Security.Cryptography;
using System.Text;
using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// E01-C49「规则修改与重新计分」的业务入口：只改**答案与计分规则**，不换试卷版本、不重新组包。
///
/// 流程：读材料包里的清单与完整参考答案（D01 校验，缺完整答案直接拒绝）→ 版本必须前进 →
/// A06 发布新的计分规则版本 → D05 重算调度按新版本规划全部最终答卷并入队。
///
/// 两条边界写在这里：
/// - **「规则已发布」不等于「成绩已重算完」**。发布结果与重算范围/入队/就绪分开报告；
///   发布成功但重算没能跑起来时如实报出 <see cref="GradingRuleRevisionResult.RecalculationError"/>。
/// - **原答案与旧成绩不被覆盖**：版本必须前进，A06 按「当前版本 = 目标版本 − 1」在事务内判定，
///   重算规划只按新版本补齐过期或缺失的成绩，遇到更高版本成绩会阻断本批而不是降版覆盖。
/// </summary>
public sealed class GradingRuleRevisionService(
    IExamVersioningService versioning,
    RecalculationScheduler recalculation,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<GradingRuleRevisionResult> ReviseAsync(
        string examId,
        GradingRuleRevisionRequest request,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(examId) || string.IsNullOrWhiteSpace(operatorId) ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            string.IsNullOrWhiteSpace(request.PackageRoot) ||
            !Path.IsPathFullyQualified(request.PackageRoot))
        {
            return Failed("InvalidRequest", "规则修订需要有效的考试、操作者、原因与绝对材料包目录。");
        }

        // 1. 材料：清单与**完整**参考答案缺一不可——缺答案就换规则等于让这场考试没法计分。
        var manifestPath = Path.Combine(request.PackageRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return Failed("MaterialMissing", $"找不到导入清单：{manifestPath}");
        }

        var manifestText = await ReadBoundedAsync(manifestPath, cancellationToken);
        var answerKeyPath = Path.Combine(request.PackageRoot, "server-only", "answer-key.json");
        if (!File.Exists(answerKeyPath))
        {
            return Failed("AnswerKeyIncomplete", "缺少完整参考答案，不能改计分规则。");
        }

        var answerKeyText = await ReadBoundedAsync(answerKeyPath, cancellationToken);
        var validation = new ExamManifestValidator().ValidateJson(manifestText, answerKeyText);
        if (!validation.IsValid)
        {
            return Failed("ManifestInvalid",
                string.Join("；", validation.Issues.Take(10).Select(issue => $"{issue.Rule}@{issue.Location}")));
        }

        if (validation.RequiresAnswerKeyArithmetic)
        {
            return Failed("AnswerKeyIncomplete", "参考答案的计分算术不完整，不能改计分规则。");
        }

        var manifest = System.Text.Json.JsonSerializer.Deserialize<ExamManifest>(
            manifestText, ImportJson.Create());
        if (manifest is null || !string.Equals(manifest.ExamId, examId, StringComparison.Ordinal))
        {
            return Failed("ExamMismatch", "清单的考试标识与本次修订的考试不一致。");
        }

        // 2. 版本必须前进。这是**早判定**：真正的并发门由 A06 在发布事务里按当前版本判定。
        var snapshot = versioning.GetExam(examId);
        if (!snapshot.Succeeded || snapshot.Value is null)
        {
            return Failed(snapshot.ErrorCode.ToString(), snapshot.Error ?? "读取考试版本状态失败。");
        }

        var current = snapshot.Value.Versions.GradingVersion;
        var target = request.GradingVersion > 0 ? request.GradingVersion : current + 1;
        if (target <= current)
        {
            return Failed("VersionConflict",
                $"目标规则版本必须大于服务器当前版本：当前 {current}，本次目标 {target}。" +
                "可用计分状态查询核对后重试。");
        }

        // 答案必须对应**已发布的试卷**：A06 会按已发布试卷的题目/题型/选项核对清单，
        // 尚无试卷版本时先说清楚原因，而不是让它以「结构不一致」收场。
        if (snapshot.Value.Paper is null)
        {
            return Failed("PaperNotPublished",
                "该考试尚未发布试卷版本：答案与计分规则必须对应一份已发布的试卷，请先完成导入发布。");
        }

        // 3. 发布新的计分规则版本。RecalculationReady 表示「重算路径可用、调用方为此负责」，
        //    不等于成绩已经重算完——真正的就绪结论由紧接着的重算快照给出。
        var manifestJson = manifestText;
        var ruleSetId = "rules-" + ContentId(manifestText + answerKeyText);
        var published = versioning.PublishGradingRules(new PublishGradingRulesRequest(
            examId,
            target - 1,
            ruleSetId,
            new GradingPublicationValidation(AnswersValidated: true, RulesValidated: true, RecalculationReady: true),
            operatorId,
            request.Reason,
            _timeProvider.GetUtcNow(),
            string.IsNullOrWhiteSpace(request.RequestId)
                ? "rule-revision-" + ContentId(examId + "|" + target + "|" + manifestJson + "|" + answerKeyText)
                : request.RequestId,
            manifestJson,
            answerKeyText));
        if (!published.Succeeded || published.Value is null)
        {
            return Failed(published.ErrorCode.ToString(), published.Error ?? "计分规则版本发布被拒绝。");
        }

        var version = published.Value.Versions.GradingVersion;

        // 4. 按新版本规划并入队重算。规则已经发布，这一步失败**不能**把结果说成发布失败。
        try
        {
            var run = await recalculation.RunOnceAsync(examId, version, cancellationToken);
            return new GradingRuleRevisionResult(true, version, [], run.SubmissionCount, run.Enqueued, run.Readiness);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消不伪装成业务失败：规则已经发布，如实报告已发布但重算未触发。
            return new GradingRuleRevisionResult(true, version, [], 0, 0, null, "重算已被取消，规则版本已发布。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new GradingRuleRevisionResult(true, version, [], 0, 0, null,
                $"规则版本 {version} 已发布，但重算未触发：{exception.Message}");
        }
    }

    private static GradingRuleRevisionResult Failed(string code, string message) =>
        new(false, null, [new ImportPipelineIssue(code, message)]);

    /// <summary>按元数据上限读取，避免把超大文件读进内存。</summary>
    private static async Task<string> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException($"材料文件大小超出允许范围：{path}");
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private static string ContentId(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();
}
