using Exam.Contracts.Importing;
using System.Security.Cryptography;
using Exam.Contracts.Administration;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.Postgres.Administration;

/// <summary>不能把调用方声明的“已编译/已加密”当作事实；解密验证当前节点真实可用的学生包后才整体发布。</summary>
public sealed partial class PostgresBundlePublicationService(PostgresExamVersioningService versions,
    PostgresVersionedResourceStore resources,IPackageKeyUnwrapper unwrapper,ManagedNodeEncryptionPolicy policy,
    PaperMaterialsVerifier materials,IPublicationCertificateProvider? certificates=null)
{
    public async Task<VersioningResult<ExamVersionSnapshot>> PublishAsync(string examId,
        PublishExamBundleApiRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(!request.PreviewApproved)return Fail(VersioningErrorCode.PublicationNotReady,"请完成逐题版式与选项绑定预览并确认后再发布。");
        if(string.IsNullOrWhiteSpace(examId)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(request.ResourceId)||
            request.ExpectedPaperVersion<0||request.ExpectedPaperVersion==long.MaxValue||request.ExpectedGradingVersion<0||
            string.IsNullOrWhiteSpace(request.ManifestJson)||request.ManifestJson.Length>StudentPackageLayout.MaxMetadataBytes||
            string.IsNullOrWhiteSpace(request.AnswerKeyJson)||request.AnswerKeyJson.Length>StudentPackageLayout.MaxMetadataBytes)
            return Fail(VersioningErrorCode.InvalidRequest,"发布参数或材料大小无效。");
        ImportedGradingRules rules;
        try { rules=ImportedGradingRules.Create(request.ManifestJson,request.AnswerKeyJson); }
        catch(ArgumentException) { return Fail(VersioningErrorCode.InvalidRequest,"清单、完整参考答案及计分规则未通过校验，未发布。"); }
        if(rules.ExamId!=examId)return Fail(VersioningErrorCode.InvalidRequest,"清单与当前考试不一致，未发布。");
        var resource=await resources.FindByIdAsync(request.ResourceId,ct);
        if(resource is null||resource.ExamId!=examId||resource.ResourceKind!="paper-package"||resource.Version!=request.ExpectedPaperVersion+1)
            return Fail(VersioningErrorCode.PublicationNotReady,"目标版本的加密学生包尚未准备好。");
        var verified=await VerifyResourceAsync(resource,rules,ct);
        if(verified.Error is not null)return Fail(VersioningErrorCode.PublicationNotReady,verified.Error);
        ct.ThrowIfCancellationRequested();
        return versions.PublishBundle(new(examId,request.RequestId,request.ExpectedPaperVersion,request.ExpectedGradingVersion,
            request.ResourceId,verified.ContentHash!,request.ManifestJson,request.AnswerKeyJson,request.ChangedQuestionIds,actor,request.Reason));
    }
    internal async Task<(string? ContentHash,string? Error)> VerifyResourceAsync(VersionedEncryptedResource resource,
        ImportedGradingRules rules,CancellationToken ct)
    {
        byte[]? key=null;byte[]? plaintext=null;
        try
        {
            if(resource.Envelope.PackageId!=resource.ResourceId||resource.Envelope.PaperVersion!=resource.Version||
                resource.EncryptedContent.Length>StudentPaperPackage.MaxPackageBytes+1024*1024||
                !policy.RequiredNodeIds.IsSubsetOf(resource.Envelope.WrappedKeys.Keys.ToHashSet(StringComparer.Ordinal))||
                !policy.RequiredNodeIds.IsSubsetOf(resource.Envelope.CertificateThumbprints.Keys.ToHashSet(StringComparer.Ordinal)))
                return(null,"加密学生包的身份、版本或受管节点覆盖不完整。");
            key=unwrapper.Unwrap(resource.Envelope);
            using var material=new PackageUnlockMaterial(resource.ResourceId,resource.Version,resource.Envelope.ChunkSize,resource.Envelope.NoncePrefix,key);
            using var input=new MemoryStream(resource.EncryptedContent,writable:false);
            using var output=new BoundedMemoryStream(StudentPaperPackage.MaxPackageBytes);
            await PackageCiphertext.DecryptAsync(input,output,material,ct);
            plaintext=output.ToArray();
            var manifest=StudentPaperPackage.ReadManifest(plaintext,resource.ExamId,resource.Version,ct);
            if(manifest.SchemaVersion<StudentPaperPackage.OptionRegionSchemaVersion||!rules.Matches(manifest.Structure))
                return(null,"学生包的题目结构或选项坐标与参考答案不一致，未激活新版本。");
            return(Convert.ToHexString(SHA256.HashData(plaintext)),null);
        }
        catch(Exception error) when(error is CryptographicException or InvalidDataException or ArgumentException or FormatException)
        { return(null,"加密学生包、显示清单或节点解锁材料校验失败，未发布；请核对编译产物和节点证书。"); }
        finally
        {
            if(key is not null)CryptographicOperations.ZeroMemory(key);
            if(plaintext is not null)CryptographicOperations.ZeroMemory(plaintext);
        }
    }
    private static VersioningResult<ExamVersionSnapshot> Fail(VersioningErrorCode code,string text)=>VersioningResult<ExamVersionSnapshot>.Failure(code,text);

    private sealed class BoundedMemoryStream(int maximum):MemoryStream
    {
        private void Check(int count){if(count<0||Position+count>maximum)throw new InvalidDataException("学生包解密结果超出允许大小。");}
        public override void Write(byte[] buffer,int offset,int count){Check(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Check(buffer.Length);base.Write(buffer);}
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {Check(buffer.Length);return base.WriteAsync(buffer,cancellationToken);}
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken)
        {Check(count);return base.WriteAsync(buffer,offset,count,cancellationToken);}
        protected override void Dispose(bool disposing)
        {if(disposing&&TryGetBuffer(out var segment)&&segment.Array is { } bytes)CryptographicOperations.ZeroMemory(bytes.AsSpan(segment.Offset,segment.Count));base.Dispose(disposing);}
    }
}
