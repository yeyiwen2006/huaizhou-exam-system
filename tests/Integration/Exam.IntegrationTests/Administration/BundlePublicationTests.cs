using System.Net;
using System.Net.Http.Json;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Application.Scoring;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Administration;

[Collection(PostgresCollection.Name)]
[Trait("Category","Postgres")]
public sealed class BundlePublicationTests(PostgresDatabaseFixture database)
{
    [Fact]
    public async Task FirstUploadRejectedAfterDeadlineCanRetryUnchangedAfterScheduleExtension()
    {
        using var f = await Fixture.Create(database.DataSource, persistResource: false);
        var now = DateTimeOffset.UtcNow;
        var past = f.Versions.UpdateSchedule(new(f.ExamId, 1,
            new(now.AddMinutes(-1), now.AddHours(1)), now.AddMinutes(-2), Guid.NewGuid().ToString("N")));
        Assert.True(past.Succeeded, past.Error);
        var rejected = await f.Service.PublishUploadedAsync(f.ExamId, f.Upload,
            f.Resource.EncryptedContent, f.MaterialsCipher, "synthetic-operator");
        Assert.Equal(VersioningErrorCode.InitialPublicationWindowClosed, rejected.ErrorCode);
        Assert.Equal(0, f.Versions.GetExam(f.ExamId).Value!.Versions.PaperVersion);
        Assert.Null(await new PostgresPaperMaterialStore(database.DataSource).FindAsync(f.ExamId, 1));

        // Keep the exact request ID and ciphertext: only the saved exam schedule changes.
        var extended = f.Versions.UpdateSchedule(new(f.ExamId, 2,
            new(now.AddHours(1), now.AddHours(2)), now, Guid.NewGuid().ToString("N")));
        Assert.True(extended.Succeeded, extended.Error);
        var published = await f.Service.PublishUploadedAsync(f.ExamId, f.Upload,
            f.Resource.EncryptedContent, f.MaterialsCipher, "synthetic-operator");
        Assert.True(published.Succeeded, published.Error);
        Assert.Equal(extended.Value!.Schedule, published.Value!.Schedule);
        Assert.Equal(1, published.Value.Versions.PaperVersion);
    }

    [Fact]
    public async Task ValidatedBundle_CommitsBothVersions_Replays_AndFeedsExistingGradingReader()
    {
        using var f=await Fixture.Create(database.DataSource);
        var result=await f.Service.PublishAsync(f.ExamId,f.Api,"synthetic-operator");
        Assert.True(result.Succeeded,result.Error);Assert.Equal(1,result.Value!.Versions.PaperVersion);Assert.Equal(1,result.Value.Versions.GradingVersion);
        var replay=await f.Service.PublishAsync(f.ExamId,f.Api,"synthetic-operator");Assert.True(replay.Succeeded,replay.Error);
        Assert.Equal(result.Value.Versions,replay.Value!.Versions);
        Assert.Equal(3L,await f.Scalar("SELECT count(*) FROM exam.version_events WHERE exam_id=$1",f.ExamId));
        var rules=await new PostgresGradingInputs(database.DataSource).LoadRulesAsync(f.ExamId);
        Assert.NotNull(rules);Assert.Equal(1,rules.GradingVersion);
        Assert.Contains(await new PostgresGradingPublicationDirectory(database.DataSource).ListPublishedAsync(),x=>x.ExamId==f.ExamId&&x.GradingVersion==1);
        Assert.Equal(true,await f.Scalar("SELECT package_ready FROM exam.exam_entry_access WHERE exam_id=$1",f.ExamId));
        var restarted=new PostgresExamVersioningService(database.DataSource);
        var changed=restarted.UpdateSchedule(new(f.ExamId,1,new(DateTimeOffset.UtcNow.AddHours(2),DateTimeOffset.UtcNow.AddHours(3)),DateTimeOffset.UtcNow,Guid.NewGuid().ToString("N")));
        Assert.True(changed.Succeeded,changed.Error);Assert.Equal(1,changed.Value!.Versions.GradingVersion);
        var snapshot=JsonSerializer.Serialize(result,JsonDefaults.Create());
        Assert.DoesNotContain("answerKeyJson",snapshot);Assert.DoesNotContain("correctOptionIds",snapshot);Assert.DoesNotContain("wrappedKeys",snapshot);
    }
    [Fact]
    public async Task InvalidSecondVersion_DoesNotActivateFirstVersion()
    {
        using var f=await Fixture.Create(database.DataSource);
        var result=await f.Service.PublishAsync(f.ExamId,f.Api with{ExpectedGradingVersion=99},"synthetic-operator");
        Assert.False(result.Succeeded);Assert.Equal(VersioningErrorCode.VersionConflict,result.ErrorCode);
        await f.AssertUnpublished();
    }
    [Fact]
    public async Task SameRequestIdWithChangedRulesOrReason_IsNotSilentlyReused()
    {
        using var f=await Fixture.Create(database.DataSource);
        Assert.True((await f.Service.PublishAsync(f.ExamId,f.Api,"synthetic-operator")).Succeeded);
        var conflict=await f.Service.PublishAsync(f.ExamId,f.Api with{Reason="同请求不同内容"},"synthetic-operator");
        Assert.Equal(VersioningErrorCode.IdempotencyConflict,conflict.ErrorCode);
        Assert.Equal(3L,await f.Scalar("SELECT count(*) FROM exam.version_events WHERE exam_id=$1",f.ExamId));
    }
    [Fact]
    public async Task ConcurrentBundles_OnlyOneExpectedVersionWins()
    {
        using var f=await Fixture.Create(database.DataSource);
        var results=await Task.WhenAll(Enumerable.Range(0,6).Select(_=>Task.Run(()=>
            f.Versions.PublishBundle(f.Trusted with{RequestId=Guid.NewGuid().ToString("N")}))));
        Assert.Single(results,x=>x.Succeeded);
        Assert.All(results.Where(x=>!x.Succeeded),x=>Assert.Equal(VersioningErrorCode.VersionConflict,x.ErrorCode));
        Assert.Equal(3L,await f.Scalar("SELECT count(*) FROM exam.version_events WHERE exam_id=$1",f.ExamId));
    }
    [Fact]
    public async Task FailureDuringSecondEvent_RollsBackResourceRuntimeStateAndFirstEvent()
    {
        using var f=await Fixture.Create(database.DataSource,persistResource:false);
        var trigger="synthetic_bundle_"+Guid.NewGuid().ToString("N");
        await f.Sql($"""
            CREATE FUNCTION exam.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $function$
            BEGIN
              IF NEW.exam_id='{f.ExamId}' AND NEW.operation_type='grading.publish' THEN
                RAISE EXCEPTION 'synthetic atomicity fault';
              END IF;
              RETURN NEW;
            END; $function$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON exam.version_events FOR EACH ROW EXECUTE FUNCTION exam.{trigger}();
            """);
        try
        {
            Assert.Throws<PostgresException>(()=>f.Versions.PublishBundle(f.Trusted,f.Resource));
            await f.AssertUnpublished();
            Assert.Equal(0L,await f.Scalar("SELECT count(*) FROM exam.versioned_resources WHERE resource_id=$1",f.Resource.ResourceId));
        }
        finally { await f.Sql($"DROP TRIGGER IF EXISTS {trigger} ON exam.version_events; DROP FUNCTION IF EXISTS exam.{trigger}();"); }
        Assert.True(f.Versions.PublishBundle(f.Trusted,f.Resource).Succeeded);
        Assert.Equal(1L,await f.Scalar("SELECT count(*) FROM exam.versioned_resources WHERE resource_id=$1",f.Resource.ResourceId));
    }
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,true)]
    public async Task MissingPreviewOrTamperedCiphertext_CannotBecomeReady(bool preview,bool tampered)
    {
        using var f=await Fixture.Create(database.DataSource,tamper:tampered);
        var result=await f.Service.PublishAsync(f.ExamId,f.Api with{PreviewApproved=preview},"synthetic-operator");
        Assert.Equal(VersioningErrorCode.PublicationNotReady,result.ErrorCode);await f.AssertUnpublished();
    }
    [Fact]
    public async Task DisplayStructureMustMatchActualAnswers_AndLegacyPackageHasNoImplicitCoordinates()
    {
        using var different=await Fixture.Create(database.DataSource,mismatchedDisplay:true);
        Assert.Equal(VersioningErrorCode.PublicationNotReady,(await different.Service.PublishAsync(different.ExamId,different.Api,"synthetic-operator")).ErrorCode);
        await different.AssertUnpublished();
        using var legacy=await Fixture.Create(database.DataSource,legacy:true);
        Assert.Equal(VersioningErrorCode.PublicationNotReady,(await legacy.Service.PublishAsync(legacy.ExamId,legacy.Api,"synthetic-operator")).ErrorCode);
        await legacy.AssertUnpublished();
    }
    [Fact]
    public async Task WrongExamAndMissingManagedRecipient_AreRejectedBeforePublication()
    {
        using var f=await Fixture.Create(database.DataSource);
        Assert.False((await f.Service.PublishAsync("other-exam",f.Api,"synthetic-operator")).Succeeded);
        var policy=new ManagedNodeEncryptionPolicy(new HashSet<string>(["node-a","missing-node"],StringComparer.Ordinal));
        var service=new PostgresBundlePublicationService(f.Versions,new(database.DataSource,policy),new TestUnwrapper(f.Certificate),policy,
            new PaperMaterialsVerifier(new TestUnwrapper(f.Certificate),policy,new PostgresPaperMaterialStore(database.DataSource)));
        Assert.Equal(VersioningErrorCode.PublicationNotReady,(await service.PublishAsync(f.ExamId,f.Api,"synthetic-operator")).ErrorCode);
        await f.AssertUnpublished();
    }
    [Fact]
    public async Task EncryptedUpload_PersistsResourceAndVersionsTogether_AndRewrapsOnlyToRealPublicCertificates()
    {
        using var f=await Fixture.Create(database.DataSource,persistResource:false);
        var recipients=f.Service.GetRecipients();var recipient=Assert.Single(recipients.Recipients);
        using(var publicOnly=X509CertificateLoader.LoadCertificate(Convert.FromBase64String(recipient.CertificateDerBase64)))
        {Assert.False(publicOnly.HasPrivateKey);Assert.Equal(f.Certificate.Thumbprint,publicOnly.Thumbprint);}
        var result=await f.Service.PublishUploadedAsync(f.ExamId,f.Upload,f.Resource.EncryptedContent,f.MaterialsCipher,"synthetic-operator");
        Assert.True(result.Succeeded,result.Error);Assert.Equal(1,result.Value!.Versions.GradingVersion);
        var stored=await new PostgresVersionedResourceStore(database.DataSource).FindByIdAsync(f.Resource.ResourceId);
        Assert.NotNull(stored);Assert.NotEqual(f.Resource.Envelope.WrappedKeys["node-a"],stored.Envelope.WrappedKeys["node-a"]);
        var replay=await f.Service.PublishUploadedAsync(f.ExamId,f.Upload,f.Resource.EncryptedContent,f.MaterialsCipher,"synthetic-operator");
        Assert.True(replay.Succeeded,replay.Error);Assert.Equal(3L,await f.Scalar("SELECT count(*) FROM exam.version_events WHERE exam_id=$1",f.ExamId));
        using var input=new MemoryStream(stored.EncryptedContent);using var plaintext=new MemoryStream();
        await new PackageEncryptionService().DecryptAsync(input,plaintext,stored.Envelope,"node-a",f.Certificate);
        Assert.Equal(f.ExamId,StudentPaperPackage.ReadManifest(plaintext.ToArray(),f.ExamId,1).ExamId);
    }
    [Fact]
    public async Task MultipartUpload_UsesSameAtomicService_AndRejectsExtraFiles()
    {
        using var f=await Fixture.Create(database.DataSource,persistResource:false);
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(f.Service);builder.Services.AddSingleton<PublicationUploadGate>();
        // 发布端点提交后会及时安排一轮重算：夹具按生产装配方式提供同一实现。
        builder.Services.AddSingleton(new GradingRecalculationTrigger(new PostgresGradingInputs(database.DataSource),
            new RecalculationScheduler(new PostgresGradingScope(database.DataSource),
                new PostgresRecalculationQueue(database.DataSource)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GradingRecalculationTrigger>.Instance));
        await using var app=builder.Build();
        // Transport fixture identity only; separate AdministrationApiTests exercise production authentication middleware.
        app.Use(async(context,next)=>{context.Items["Exam.AuthenticatedOperatorId"]="synthetic-operator";await next();});
        app.MapGroup("/api/v1").MapPublicationUploadApi();await app.StartAsync();using var client=app.GetTestClient();
        using(var body=new MultipartFormDataContent())
        {
            body.Add(new StringContent(JsonSerializer.Serialize(f.Upload,JsonDefaults.Create()),Encoding.UTF8,"application/json"),"metadata");
            body.Add(new ByteArrayContent(f.Resource.EncryptedContent),"package","synthetic.hxp");
            body.Add(new ByteArrayContent(f.MaterialsCipher),"materials","synthetic-materials.hxp");
            var response=await client.PostAsync($"/api/v1/exams/{f.ExamId}/publications/upload",body);
            var text=await response.Content.ReadAsStringAsync();Assert.True(response.StatusCode==HttpStatusCode.OK,text);
            var envelope=JsonSerializer.Deserialize<ApiEnvelope<ExamVersionSnapshot>>(text,JsonDefaults.Create());
            Assert.Equal(1,envelope!.Result!.Versions.PaperVersion);Assert.Equal(1,envelope.Result.Versions.GradingVersion);
        }
        using(var invalid=new MultipartFormDataContent())
        {
            invalid.Add(new StringContent(JsonSerializer.Serialize(f.Upload,JsonDefaults.Create()),Encoding.UTF8,"application/json"),"metadata");
            invalid.Add(new ByteArrayContent([1,2,3]),"package","one.hxp");invalid.Add(new ByteArrayContent([1]),"materials","two.hxp");
            invalid.Add(new ByteArrayContent([1]),"unexpected","three.hxp");
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync($"/api/v1/exams/{f.ExamId}/publications/upload",invalid)).StatusCode);
        }
    }
    [Fact]
    public async Task UploadedBundle_WithSecondVersionConflictLeavesNoStoredResource()
    {
        using var f=await Fixture.Create(database.DataSource,persistResource:false);
        var result=await f.Service.PublishUploadedAsync(f.ExamId,f.Upload with{Publication=f.Api with{ExpectedGradingVersion=99}},f.Resource.EncryptedContent,f.MaterialsCipher,"synthetic-operator");
        Assert.Equal(VersioningErrorCode.VersionConflict,result.ErrorCode);await f.AssertUnpublished();
        Assert.Null(await new PostgresVersionedResourceStore(database.DataSource).FindByIdAsync(f.Resource.ResourceId));
        // 源材料归档与版本同一事务：冲突时归档行同样不得留下。
        Assert.Null(await new PostgresPaperMaterialStore(database.DataSource).FindAsync(f.ExamId,1));
    }
    private sealed class PublicCertificateProvider(X509Certificate2 certificate):IPublicationCertificateProvider
    {
        public PublicationCertificateLease Acquire()=>new([new("node-a",X509CertificateLoader.LoadCertificate(certificate.RawData))]);
    }

    private sealed class Fixture(NpgsqlDataSource source,X509Certificate2 certificate,string examId,
        VersionedEncryptedResource resource,PublishExamBundleApiRequest api,PublishExamBundleRequest trusted,
        byte[] materialsCipher,EncryptedPackageEnvelope materialsEnvelope):IDisposable
    {
        public byte[] MaterialsCipher=>materialsCipher;
        public EncryptedPackageEnvelope MaterialsEnvelope=>materialsEnvelope;
        public string ExamId=>examId;
        public X509Certificate2 Certificate=>certificate;
        public VersionedEncryptedResource Resource=>resource;
        public PublishExamBundleApiRequest Api=>api;
        public PublishExamBundleRequest Trusted=>trusted;
        public PostgresExamVersioningService Versions{get;}=new(source);
        public PaperMaterialsVerifier Verifier=>new(new TestUnwrapper(certificate),Policy,new PostgresPaperMaterialStore(source));
        public PostgresBundlePublicationService Service=>new(Versions,new(source,Policy),new TestUnwrapper(certificate),Policy,Verifier,new PublicCertificateProvider(certificate));
        public UploadExamBundleRequest Upload=>new(Api,new(Resource.Envelope.PackageId,Resource.Envelope.PaperVersion,
            Resource.Envelope.ChunkSize,Resource.Envelope.NoncePrefix,Resource.Envelope.WrappedKeys,Resource.Envelope.CertificateThumbprints),
            new(MaterialsEnvelope.PackageId,MaterialsEnvelope.PaperVersion,MaterialsEnvelope.ChunkSize,
                MaterialsEnvelope.NoncePrefix,MaterialsEnvelope.WrappedKeys,MaterialsEnvelope.CertificateThumbprints));
        private static readonly ManagedNodeEncryptionPolicy Policy=new(new HashSet<string>(["node-a"],StringComparer.Ordinal));
        public void Dispose()=>certificate.Dispose();
        public async Task AssertUnpublished()
        {
            var state=Versions.GetExam(ExamId).Value!;Assert.Equal(0,state.Versions.PaperVersion);Assert.Equal(0,state.Versions.GradingVersion);
            Assert.Equal(false,await Scalar("SELECT package_ready FROM exam.exam_entry_access WHERE exam_id=$1",ExamId));
            Assert.Equal(1L,await Scalar("SELECT count(*) FROM exam.version_events WHERE exam_id=$1",ExamId));
            Assert.Null(await new PostgresGradingInputs(source).LoadRulesAsync(ExamId));
        }
        public async Task Sql(string sql,params object[] values)
        {await using var cmd=source.CreateCommand(sql);foreach(var value in values)cmd.Parameters.AddWithValue(value);await cmd.ExecuteNonQueryAsync();}
        public async Task<object?> Scalar(string sql,params object[] values)
        {await using var cmd=source.CreateCommand(sql);foreach(var value in values)cmd.Parameters.AddWithValue(value);return await cmd.ExecuteScalarAsync();}
        public static async Task<Fixture> Create(NpgsqlDataSource source,bool persistResource=true,bool tamper=false,bool legacy=false,bool mismatchedDisplay=false)
        {
            var exam="bundle-test-"+Guid.NewGuid().ToString("N");var artifact="paper-"+Guid.NewGuid().ToString("N");
            var now=DateTimeOffset.UtcNow;
            var versions=new PostgresExamVersioningService(source);
            Assert.True(versions.CreateExam(new(exam,new(now.AddYears(80),now.AddYears(80).AddHours(2)),now,Guid.NewGuid().ToString("N"))).Succeeded);
            var manifest=new ExamManifest(1,exam,"合成整体发布测试",
                [new("subject","科目","1")],[new("group","subject","单选","single",1,1,true,"1",new(new("1","0","0"),null))],
                [new("q1",1,"subject","group","questions/合成整体发布测试_1.tex",["A","B"],null)],[]);
            var key=new AnswerKeyFile(1,exam,[new("q1",1,["A"])]);
            var manifestJson=JsonSerializer.Serialize(manifest,ImportJson.Create());var keyJson=JsonSerializer.Serialize(key,ImportJson.Create());
            var rules=ImportedGradingRules.Create(manifestJson,keyJson);var structure=rules.Structure.Questions[0];
            if(mismatchedDisplay)structure=structure with{QuestionId="different-question"};
            var display=new StudentPaperManifest(legacy?1:2,exam,"合成整体发布测试",1,[new("subject","科目")],
                [new("group","subject","单选",1)],[new(structure,"questions/synthetic.pdf",null)],[]);
            byte[] payload;
            using(var memory=new MemoryStream())
            {
                using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,leaveOpen:true))
                {
                    static void Add(ZipArchive zip,string path,byte[] bytes){using var stream=zip.CreateEntry(path).Open();stream.Write(bytes);}
                    Add(zip,"manifest.json",JsonSerializer.SerializeToUtf8Bytes(display,ImportJson.Create()));
                    Add(zip,"questions/synthetic.pdf",MinimalPdf());
                    if(!legacy)Add(zip,OptionRegionArtifact.FileName,JsonSerializer.SerializeToUtf8Bytes(new OptionRegionArtifact(1,OptionRegionArtifact.AnchorSchemeName,exam,1,
                        [new(structure.QuestionId,[new("A",0,.1,.2,.05,.03),new("B",0,.1,.3,.05,.03)])]),ImportJson.Create()));
                }
                payload=memory.ToArray();
            }
            using var rsa=RSA.Create(2048);var certRequest=new CertificateRequest("CN=synthetic-node-a",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var cert=certRequest.CreateSelfSigned(now.AddMinutes(-1),now.AddDays(1));
            using var input=new MemoryStream(payload,writable:false);using var encrypted=new MemoryStream();
            var envelope=await new PackageEncryptionService(4096,Policy).EncryptAsync(input,encrypted,artifact,1,[new("node-a",cert)]);
            var cipher=encrypted.ToArray();if(tamper)cipher[^1]^=1;
            var resource=new VersionedEncryptedResource(artifact,exam,"paper-package",1,cipher,envelope,now,"synthetic-operator");
            if(persistResource)await new PostgresVersionedResourceStore(source,Policy).SaveAsync(resource);
            // 源材料归档：清单、独立答案与题目源文件，按同一受管公钥加密后随发布一起提交。
            var materialsArchive=PaperMaterialsArchive.Build(exam,1,
            [
                new("manifest.json",Encoding.UTF8.GetBytes(manifestJson)),
                new("server-only/answer-key.json",Encoding.UTF8.GetBytes(keyJson)),
                new("questions/合成整体发布测试_1.tex",Encoding.UTF8.GetBytes("\\documentclass{ctexart}\\begin{document}合成题 A B\\end{document}"))
            ]);
            using var materialsInput=new MemoryStream(materialsArchive,writable:false);using var materialsEncrypted=new MemoryStream();
            var materialsEnvelope=await new PackageEncryptionService(4096,Policy).EncryptAsync(
                materialsInput,materialsEncrypted,artifact+"-materials",1,[new("node-a",cert)]);
            var materialsCipher=materialsEncrypted.ToArray();
            var materialsSha256=Convert.ToHexString(SHA256.HashData(materialsArchive)).ToLowerInvariant();
            var id=Guid.NewGuid().ToString("N");var api=new PublishExamBundleApiRequest(id,0,0,artifact,manifestJson,keyJson,[],"合成整体发布测试",true,materialsSha256);
            var trusted=new PublishExamBundleRequest(exam,id,0,0,artifact,Convert.ToHexString(SHA256.HashData(payload)),manifestJson,keyJson,[],"synthetic-operator","合成整体发布测试");
            CryptographicOperations.ZeroMemory(payload);
            return new(source,cert,exam,resource,api,trusted,materialsCipher,materialsEnvelope);
        }
        // This is a deterministic one-page synthetic PDF for package integrity tests, not an asserted XeLaTeX compilation.
        private static byte[] MinimalPdf()
        {
            var objects=new[]{"<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R >>","<< /Length 0 >>\nstream\n\nendstream"};
            var text=new StringBuilder("%PDF-1.4\n");var offsets=new List<int>();
            for(int i=0;i<objects.Length;i++){offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));text.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n");}
            var xref=Encoding.ASCII.GetByteCount(text.ToString());text.Append("xref\n0 5\n0000000000 65535 f \n");
            foreach(var offset in offsets)text.Append(offset.ToString("D10",System.Globalization.CultureInfo.InvariantCulture)+" 00000 n \n");
            text.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");return Encoding.ASCII.GetBytes(text.ToString());
        }
    }
    private sealed class TestUnwrapper(X509Certificate2 certificate):IPackageKeyUnwrapper
    {public byte[] Unwrap(EncryptedPackageEnvelope envelope)=>PackageEncryptionService.UnwrapDataKey(envelope,"node-a",certificate);}
}
