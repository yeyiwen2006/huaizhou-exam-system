using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using Exam.Contracts.Administration;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.Postgres.Administration;

public sealed partial class PostgresBundlePublicationService
{
    public PublicationRecipients GetRecipients()
    {
        using var lease=(certificates??throw new CryptographicException("尚未配置发布公钥证书。")).Acquire();
        if(!policy.RequiredNodeIds.SetEquals(lease.Recipients.Select(x=>x.NodeId)))
            throw new CryptographicException("发布公钥与受管节点列表不一致。");
        return new(lease.Recipients.Select(x=>new PublicationRecipient(x.NodeId,Convert.ToBase64String(x.Certificate.RawData))).ToArray());
    }

    public async Task<VersioningResult<ExamVersionSnapshot>> PublishUploadedAsync(string examId,UploadExamBundleRequest upload,
        byte[] encryptedContent,byte[] materialsContent,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(upload);ArgumentNullException.ThrowIfNull(encryptedContent);
        ArgumentNullException.ThrowIfNull(materialsContent);
        var request=upload.Publication;var envelope=upload.Envelope;
        if(request is null||envelope is null||!request.PreviewApproved||request.ExpectedPaperVersion is <0 or long.MaxValue||
           request.ExpectedGradingVersion<0||string.IsNullOrWhiteSpace(request.ResourceId)||
           request.ResourceId!=envelope.PackageId||envelope.PaperVersion!=request.ExpectedPaperVersion+1||
           encryptedContent.Length is 0||encryptedContent.Length>StudentPaperPackage.MaxPackageBytes+1024*1024||
           string.IsNullOrWhiteSpace(request.ManifestJson)||request.ManifestJson.Length>StudentPackageLayout.MaxMetadataBytes||
           string.IsNullOrWhiteSpace(request.AnswerKeyJson)||request.AnswerKeyJson.Length>StudentPackageLayout.MaxMetadataBytes||
           envelope.WrappedKeys is null||envelope.CertificateThumbprints is null||envelope.WrappedKeys.Count>32)
            return Fail(VersioningErrorCode.InvalidRequest,"整体发布参数、预览确认、包身份或材料大小无效。");
        ImportedGradingRules rules;
        try{rules=ImportedGradingRules.Create(request.ManifestJson,request.AnswerKeyJson);}
        catch(ArgumentException){return Fail(VersioningErrorCode.InvalidRequest,"清单、参考答案和计分规则未通过校验，未保存学生包。");}
        if(rules.ExamId!=examId)return Fail(VersioningErrorCode.InvalidRequest,"上传材料与当前考试不一致。");
        var resource=new VersionedEncryptedResource(request.ResourceId,examId,"paper-package",envelope.PaperVersion,
            encryptedContent,new(envelope.PackageId,envelope.PaperVersion,envelope.ChunkSize,envelope.NoncePrefix,envelope.WrappedKeys,envelope.CertificateThumbprints),
            DateTimeOffset.UtcNow,actor);
        var verified=await VerifyResourceAsync(resource,rules,ct);
        if(verified.Error is not null)return Fail(VersioningErrorCode.PublicationNotReady,verified.Error);
        // 源材料归档与声明变更题号的核对：解密、比对声明哈希、逐题摘要、与上一版本基线比对。
        // 全部在进入发布事务之前完成——不在持有考试流锁时做 IO 与哈希。
        var materialsVerdict=await materials.VerifyAsync(examId,request,upload.MaterialsEnvelope,materialsContent,ct);
        if(!materialsVerdict.Accepted)
            return Fail(materialsVerdict.ErrorCode,materialsVerdict.Message??"源材料归档或变更题号声明未通过核对。");
        byte[]? dataKey=null;
        try
        {
            // 重包全部受管节点的密钥，不能相信上传方声称“备用节点也能解密”。无需导出任何节点私钥。
            dataKey=unwrapper.Unwrap(resource.Envelope);
            using var lease=(certificates??throw new CryptographicException("尚未配置发布公钥证书。")).Acquire();
            if(!policy.RequiredNodeIds.SetEquals(lease.Recipients.Select(x=>x.NodeId)))
                return Fail(VersioningErrorCode.PublicationNotReady,"发布公钥与受管节点列表不一致。");
            var wrapped=new Dictionary<string,string>(StringComparer.Ordinal);var thumbs=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var recipient in lease.Recipients)
            {
                using var rsa=recipient.Certificate.GetRSAPublicKey()??throw new CryptographicException("受管节点证书缺少 RSA 公钥。");
                if(rsa.KeySize<2048)throw new CryptographicException("发布证书公钥长度不足。");
                wrapped.Add(recipient.NodeId,Convert.ToBase64String(rsa.Encrypt(dataKey,RSAEncryptionPadding.OaepSHA256)));
                thumbs.Add(recipient.NodeId,recipient.Certificate.Thumbprint);
            }
            resource=resource with{Envelope=resource.Envelope with{WrappedKeys=wrapped,CertificateThumbprints=thumbs}};
        }
        catch(CryptographicException){return Fail(VersioningErrorCode.PublicationNotReady,"受管节点公开证书不可用，未发布或保存半份试卷；请核对发布证书配置。");}
        finally{if(dataKey is not null)CryptographicOperations.ZeroMemory(dataKey);}
        ct.ThrowIfCancellationRequested();
        // 归档行与学生包明文哈希一起入库：两者来自同一次发布，可相互印证。
        var materialsRow=materialsVerdict.ToRow(verified.ContentHash!,actor,DateTimeOffset.UtcNow);
        return versions.PublishBundle(new(examId,request.RequestId,request.ExpectedPaperVersion,request.ExpectedGradingVersion,
            request.ResourceId,verified.ContentHash!,request.ManifestJson,request.AnswerKeyJson,request.ChangedQuestionIds,actor,request.Reason,
            request.MaterialsSha256),resource,materialsRow);
    }
}
