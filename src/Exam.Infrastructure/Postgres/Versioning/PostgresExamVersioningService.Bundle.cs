using System.Data;
using Exam.Contracts.Administration;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Domain.Importing;
using Exam.Contracts.Exams;
using Exam.Contracts.Common;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace Exam.Infrastructure.Postgres.Versioning;

public sealed partial class PostgresExamVersioningService
{
    private const string PublishBundleOperation = "bundle.publish";

    /// <summary>
    /// 两条既有领域事件、两个有效版本、资源及运行状态一起提交。
    /// 保留 grading.publish 事件格式，既有重算和报表读取不会绕过最新规则。
    /// resourceToPersist 只能来自已完成真实包校验和加密的服务器应用层。
    /// </summary>
    public VersioningResult<ExamVersionSnapshot> PublishBundle(PublishExamBundleRequest request,
        VersionedEncryptedResource? resourceToPersist = null,
        PaperMaterialsRow? materialsToPersist = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length>128 ||
            string.IsNullOrWhiteSpace(request.ExamId) || string.IsNullOrWhiteSpace(request.ArtifactId) ||
            string.IsNullOrWhiteSpace(request.Operator) || string.IsNullOrWhiteSpace(request.Reason) ||
            request.ExpectedPaperVersion<0 || request.ExpectedGradingVersion<0 ||
            request.ChangedQuestionIds is null || request.PackageContentSha256?.Length!=64 ||
            !request.PackageContentSha256.All(Uri.IsHexDigit))
            return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.InvalidRequest,"发布缺少有效的身份、版本、材料或操作原因。");
        ImportedGradingRules rules;
        try { rules=ImportedGradingRules.Create(request.ManifestJson,request.AnswerKeyJson); }
        catch(ArgumentException) { return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.InvalidRequest,"清单、完整参考答案或满分校验未通过，未发布任何版本。"); }
        if(rules.ExamId!=request.ExamId)
            return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.InvalidRequest,"导入材料不属于当前考试，未发布。");
        if(resourceToPersist is not null && (resourceToPersist.ResourceId!=request.ArtifactId ||
            resourceToPersist.ExamId!=request.ExamId || resourceToPersist.ResourceKind!="paper-package" ||
            resourceToPersist.Version!=request.ExpectedPaperVersion+1))
            return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.InvalidRequest,"待保存学生包与当前考试和目标版本不一致。");
        if(materialsToPersist is not null && (materialsToPersist.ExamId!=request.ExamId ||
            materialsToPersist.PaperVersion!=request.ExpectedPaperVersion+1))
            return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.InvalidRequest,"待保存的源材料归档与当前考试或目标版本不一致。");
        using var connection=_dataSource.OpenConnection();
        using var transaction=connection.BeginTransaction(IsolationLevel.ReadCommitted);
        AcquireRequestLock(connection,transaction,request.RequestId);
        if(FindIdempotent<ExamVersionSnapshot,PublishExamBundleRequest>(connection,transaction,
            request.RequestId,PublishBundleOperation,request) is { } replayed)
        { transaction.Commit(); return replayed; }
        var revision=LockStream(connection,transaction,request.ExamId);
        if(revision is null)return NotFound<ExamVersionSnapshot>(request.ExamId);
        // Catalog editors lock the catalog before entry access. Keep that lock order to avoid
        // deadlocks, and prevent a saved preview from racing a change to the authoritative settings.
        (string Name, ExamSettings Settings)? catalog = null;
        using (var command = new NpgsqlCommand("SELECT exam_name, settings::text FROM exam.exam_catalog WHERE exam_id=$1 FOR SHARE;", connection, transaction))
        {
            command.Parameters.AddWithValue(request.ExamId);
            using var reader = command.ExecuteReader();
            if (reader.Read()) catalog = (reader.GetString(0), JsonSerializer.Deserialize<ExamSettings>(reader.GetString(1), JsonDefaults.Create())!);
        }
        LockEntryAccess(connection,transaction,request.ExamId);
        var service=Replay(connection,transaction,request.ExamId,revision.Value);
        if(SynchronizeAndValidateCanonicalParticipants(connection,transaction,service,request.ExamId,request) is { } canonicalError)
            return VersioningResult<ExamVersionSnapshot>.Failure(canonicalError.Code,canonicalError.Message);
        var before=service.GetExam(request.ExamId).Value ?? throw new InvalidDataException("考试版本流无法重建。");
        // 时间由服务器产生，不进入外层请求指纹；相同请求网络重试不会因时间变化失去幂等性。
        var now=DateTimeOffset.UtcNow;
        if (before.Paper is null)
        {
            if (before.LifecycleState != ExamLifecycle.Scheduled || now >= before.Schedule.StartsAt)
                return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.PublicationNotReady,
                    "首次导入必须在开考前完成，未保存或激活任何部分试卷。");
            if (catalog is { } configured)
            {
                try { ImportCatalogBinding.RequireMatch(request.ManifestJson, request.AnswerKeyJson, configured.Name, configured.Settings); }
                catch (ArgumentException)
                {
                    return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.VersionConflict,
                        "试题包与当前考试名称、科目、题组或计分设置不一致，请重新检查后发布。");
                }
            }
        }
        if (before.Paper is not null && before.Versions.GradingVersion != before.Versions.PaperVersion &&
            request.ExpectedGradingVersion == before.Versions.GradingVersion)
        {
            using var latest = new NpgsqlCommand("""
                SELECT payload::text FROM exam.version_events WHERE exam_id=$1 AND operation_type='grading.publish'
                  AND stream_revision<=$2 ORDER BY stream_revision DESC LIMIT 1
                """, connection, transaction);
            latest.Parameters.AddWithValue(request.ExamId);
            latest.Parameters.AddWithValue(revision.Value);
            var payload = latest.ExecuteScalar() as string;
            var current = payload is null ? null : JsonSerializer.Deserialize<PublishGradingRulesRequest>(payload, JsonDefaults.Create());
            if (current is null || current.ExamId != request.ExamId || current.ExpectedVersion != before.Versions.GradingVersion - 1 ||
                current.ManifestJson is null || current.AnswerKeyJson is null)
                return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.PublicationNotReady,
                    "当前规则源材料不可用，不能以旧归档覆盖最新规则。");
            if (!Exam.Infrastructure.Packaging.PaperRevisionMaterials.EquivalentGrading(request.ManifestJson, request.AnswerKeyJson,
                    current.ManifestJson, current.AnswerKeyJson))
                return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.VersionConflict,
                    "试卷修订必须保留当前完整答案与计分规则；请重新取回最新材料。独立答案/计分修改请使用规则修订入口。");
        }
        var paper=new PublishPaperRequest(request.ExamId,request.ExpectedPaperVersion,request.ArtifactId,
            rules.Structure,request.ChangedQuestionIds,new(true,true,true,true),request.Operator,request.Reason,now,request.RequestId+":paper",materialsToPersist?.ArchiveSha256);
        var grading=new PublishGradingRulesRequest(request.ExamId,request.ExpectedGradingVersion,
            "bundle-rules-"+request.ArtifactId,new(true,true,true),request.Operator,request.Reason,now,request.RequestId+":grading",
            request.ManifestJson,request.AnswerKeyJson);
        var paperResult=service.PublishPaper(paper);
        if(!paperResult.Succeeded)return paperResult;
        var result=service.PublishGradingRules(grading);
        if(!result.Succeeded)return result;
        try
        {
            if(resourceToPersist is not null)
                PostgresVersionedResourceStore.SaveInTransaction(connection,transaction,resourceToPersist);
            // 源材料归档与版本发布同一事务：不存在「版本发布了但没有归档」的半套状态。
            if(materialsToPersist is not null)
                PostgresPaperMaterialStore.SaveInTransaction(connection,transaction,materialsToPersist);
            if(ApplyRuntimeEffects(connection,transaction,PublishPaperOperation,paper.RequestId,paper,
                before,paperResult.Value!,now) is { } paperError)
                return VersioningResult<ExamVersionSnapshot>.Failure(paperError.Code,paperError.Message);
            if(ApplyRuntimeEffects(connection,transaction,PublishGradingOperation,grading.RequestId,grading,
                paperResult.Value!,result.Value!,now) is { } gradingError)
                return VersioningResult<ExamVersionSnapshot>.Failure(gradingError.Code,gradingError.Message);
            AppendEvent(connection,transaction,request.ExamId,checked(revision.Value+1),PublishPaperOperation,paper,now);
            AppendEvent(connection,transaction,request.ExamId,checked(revision.Value+2),PublishGradingOperation,grading,now);
            UpdateStreamHead(connection,transaction,request.ExamId,revision.Value,checked(revision.Value+2),now);
            UpsertProjection(connection,transaction,checked(revision.Value+2),result.Value!,now);
            SaveIdempotent(connection,transaction,request.RequestId,PublishBundleOperation,request,result);
            transaction.Commit();return result;
        }
        catch(PostgresException error) when(error.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { return VersioningResult<ExamVersionSnapshot>.Failure(VersioningErrorCode.VersionConflict,"资源或考试版本已变化，整体发布未提交，请刷新后重试。"); }
    }
}
