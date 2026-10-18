using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>
/// 只接收**两份密文**：加密学生包与加密源材料归档。
/// 归档里含独立答案，所以和包一样先加密再上传——表单临时缓冲不会写入题目明文、答案文件或节点私钥。
/// 两份都在**同一次发布事务**里落库：不存在「版本发布了但没有归档」的半套状态。
/// </summary>
public static class PublicationUploadEndpoints
{
    public static RouteGroupBuilder MapPublicationUploadApi(this RouteGroupBuilder api)
    {
        api.MapGet("/publications/recipients",([FromServices] PostgresBundlePublicationService service,HttpContext context,IConfiguration configuration)=>
        {
            try{return ApiEndpointResults.Success(context,configuration,context.TraceIdentifier,service.GetRecipients());}
            catch(CryptographicException){return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,
                "PUBLICATION_NOT_READY","发布公钥证书不可用；请核对受管节点及公开证书配置。",StatusCodes.Status503ServiceUnavailable);}
        }).WithMetadata(new ReadOnlyObservation());
        api.MapPost("/exams/{examId}/publications/upload",async (string examId,[FromServices] PostgresBundlePublicationService service,
            [FromServices] GradingRecalculationTrigger recalculation,
            [FromServices] PublicationUploadGate admission,HttpContext context,IConfiguration configuration,CancellationToken ct)=>
        {
            const long maximum=(long)StudentPaperPackage.MaxPackageBytes+PaperMaterialsArchive.MaxArchiveBytes
                +4L*StudentPackageLayout.MaxMetadataBytes+2*1024*1024;
            if(!context.Request.HasFormContentType||context.Request.ContentLength>maximum)
                return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,"INVALID_INPUT","上传格式或大小无效。",StatusCodes.Status400BadRequest);
            if(context.Features.Get<IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} size)size.MaxRequestBodySize=maximum;
            await admission.Slots.WaitAsync(ct);
            try
            {
                var options=new FormOptions
                {
                    // ValueCountLimit 把**文件部分也计入**：1 个 metadata 字段 + 2 份密文 = 3，留一点余量。
                    // 真正要求「恰好两份密文」的是下面的显式形状检查。
                    BufferBody=false,ValueCountLimit=4,ValueLengthLimit=checked(4*StudentPackageLayout.MaxMetadataBytes),
                    MultipartBodyLengthLimit=StudentPaperPackage.MaxPackageBytes+PaperMaterialsArchive.MaxArchiveBytes+1024L*1024,
                    MultipartBoundaryLengthLimit=70,MultipartHeadersLengthLimit=16384,MultipartHeadersCountLimit=16
                };
                context.Features.Set<IFormFeature>(new FormFeature(context.Request,options));
                var form=await context.Request.ReadFormAsync(ct);
                if(form.Count!=1||form["metadata"].Count!=1||form.Files.Count!=2)
                    throw new InvalidDataException("需要一个 metadata 字段与 package、materials 两份密文。");
                var package=form.Files.FirstOrDefault(file=>file.Name=="package");
                var materials=form.Files.FirstOrDefault(file=>file.Name=="materials");
                if(package is null||materials is null||
                   package.Length is <1||package.Length>StudentPaperPackage.MaxPackageBytes+1024L*1024||
                   materials.Length is <1||materials.Length>PaperMaterialsArchive.MaxArchiveBytes+1024L*1024)
                    throw new InvalidDataException("package 与 materials 两份密文的字段名或大小无效。");
                var upload=JsonSerializer.Deserialize<UploadExamBundleRequest>(form["metadata"][0]!,JsonDefaults.Create())
                    ??throw new InvalidDataException("发布信息不能为空。");
                if(upload.Publication is null||upload.Envelope is null||upload.MaterialsEnvelope is null)
                    throw new InvalidDataException("发布清单、学生包信封与源材料信封都不能为空。");
                var result=await service.PublishUploadedAsync(examId,upload,
                    await ReadAsync(package,ct),await ReadAsync(materials,ct),
                    ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context),ct);
                // 整体发布也发布了规则版本：提交之后同样及时安排一轮重算（失败由后台循环补偿）。
                if(result.Succeeded)
                {
                    await recalculation.TryTriggerAsync(examId,ct);
                }

                return ApiEndpointResults.FromVersioning(context,configuration,upload.Publication.RequestId,result);
            }
            catch(Exception error)when(error is JsonException or InvalidDataException or ArgumentException)
            {return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,"INVALID_INPUT","上传的密文或发布说明无效，未激活新版本。",StatusCodes.Status400BadRequest);}
            finally{admission.Slots.Release();}
        }).DisableAntiforgery().WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        // 源材料归档的按版本取回：考务登录会话才能访问（不在 /resources/ 前缀下，避免被划为设备凭据路由）。
        // 归档含独立答案，属敏感出口：只报哈希与身份，内容按流返回，不整包缓冲。
        api.MapGet("/exams/{examId}/paper-materials",async (string examId,[FromQuery] long version,
            [FromServices] PostgresPaperMaterialStore materials,HttpContext context,IConfiguration configuration,
            CancellationToken ct)=>
        {
            if(version<1)
                return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,
                    "INVALID_INPUT","必须指定要取回的试卷版本。",StatusCodes.Status400BadRequest);
            var info=await materials.FindInfoAsync(examId,version,ct);
            if(info is null)
                return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,
                    "MATERIALS_NOT_FOUND",$"该考试没有版本 {version} 的源材料归档。",StatusCodes.Status404NotFound);
            var identity=configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
                ??throw new InvalidOperationException("缺少服务器身份配置。");
            context.Response.Headers["X-Exam-Server-Id"]=identity.ServerId;
            context.Response.Headers["X-Exam-Node-Id"]=identity.NodeId;
            context.Response.Headers["X-Exam-Server-Epoch"]=identity.ServerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Exam-Paper-Version"]=version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Exam-Materials-Sha256"]=info.ArchiveSha256;
            context.Response.Headers["X-Exam-Materials-File-Count"]=info.FileCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl="no-store";
            var archive=await materials.OpenArchiveAsync(examId,version,ct);
            if(archive is null)
                return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,
                    "MATERIALS_NOT_FOUND",$"该考试没有版本 {version} 的源材料归档。",StatusCodes.Status404NotFound);
            return Results.Stream(archive,"application/zip",
                fileDownloadName:$"{examId}-materials-v{version}.zip",enableRangeProcessing:false);
        }).WithMetadata(new ReadOnlyObservation());

        // Complete answer material is operator-only and never part of grading status or student resources.
        api.MapGet("/exams/{examId}/grading-materials", async (string examId,
            [FromServices] Exam.Domain.Scoring.IGradingInputs inputs, HttpContext context,
            IConfiguration configuration, CancellationToken ct) =>
        {
            _ = ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
            context.Response.Headers.CacheControl = "no-store";
            if (inputs is not Exam.Infrastructure.Postgres.Scoring.PostgresGradingInputs stored)
                return ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                    "PUBLICATION_NOT_READY", "当前计分源材料读取不可用。", StatusCodes.Status503ServiceUnavailable);
            try
            {
                var source = await stored.LoadSourceAsync(examId, ct);
                return source is null
                    ? ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                        "MATERIALS_NOT_FOUND", "当前考试没有完整已发布的计分源材料。", StatusCodes.Status404NotFound)
                    : ApiEndpointResults.Success(context, configuration, context.TraceIdentifier, source);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
            {
                return ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                    "PUBLICATION_NOT_READY", "当前规则源材料不可用，禁止返回旧版替代。", StatusCodes.Status503ServiceUnavailable);
            }
        }).WithMetadata(new ReadOnlyObservation());

        api.MapGet("/exams/{examId}/grading-history", async (string examId,
            [FromServices] Npgsql.NpgsqlDataSource dataSource, HttpContext context,
            IConfiguration configuration, CancellationToken ct) =>
        {
            _ = ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
            context.Response.Headers.CacheControl = "no-store";
            var history = await new Exam.Infrastructure.Postgres.Scoring.PostgresGradingHistory(dataSource)
                .ListAsync(examId, ct);
            return ApiEndpointResults.Success(context, configuration, context.TraceIdentifier, history);
        }).WithMetadata(new ReadOnlyObservation());

        return api;
    }

    private static async Task<byte[]> ReadAsync(IFormFile file,CancellationToken ct)
    {
        using var buffer=new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(buffer,ct);
        return buffer.ToArray();
    }
}

public sealed class PublicationUploadGate:IDisposable
{
    public SemaphoreSlim Slots{get;}=new(2,2);
    public void Dispose()=>Slots.Dispose();
}
