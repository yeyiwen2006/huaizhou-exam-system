using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>只接收已加密学生包；表单临时缓冲也不会写入题目明文、答案文件或节点私钥。</summary>
public static class PublicationUploadEndpoints
{
    public static RouteGroupBuilder MapPublicationUploadApi(this RouteGroupBuilder api)
    {
        api.MapGet("/publications/recipients",([FromServices] PostgresBundlePublicationService service,HttpContext context,IConfiguration configuration)=>
        {
            try{return ApiEndpointResults.Success(context,configuration,context.TraceIdentifier,service.GetRecipients());}
            catch(CryptographicException){return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,
                "PUBLICATION_NOT_READY","发布公钥证书不可用；请核对受管节点及公开证书配置。",StatusCodes.Status503ServiceUnavailable);}
        });
        api.MapPost("/exams/{examId}/publications/upload",async (string examId,[FromServices] PostgresBundlePublicationService service,
            [FromServices] PublicationUploadGate admission,HttpContext context,IConfiguration configuration,CancellationToken ct)=>
        {
            const long maximum=(long)StudentPaperPackage.MaxPackageBytes+4L*StudentPackageLayout.MaxMetadataBytes+2*1024*1024;
            if(!context.Request.HasFormContentType||context.Request.ContentLength>maximum)
                return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,"INVALID_INPUT","上传格式或大小无效。",StatusCodes.Status400BadRequest);
            if(context.Features.Get<IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} size)size.MaxRequestBodySize=maximum;
            await admission.Slots.WaitAsync(ct);
            try
            {
                var options=new FormOptions
                {
                    BufferBody=false,ValueCountLimit=2,ValueLengthLimit=checked(4*StudentPackageLayout.MaxMetadataBytes),
                    MultipartBodyLengthLimit=StudentPaperPackage.MaxPackageBytes+1024L*1024,
                    MultipartBoundaryLengthLimit=70,MultipartHeadersLengthLimit=16384,MultipartHeadersCountLimit=16
                };
                context.Features.Set<IFormFeature>(new FormFeature(context.Request,options));
                var form=await context.Request.ReadFormAsync(ct);
                if(form.Count!=1||form["metadata"].Count!=1||form.Files.Count!=1||form.Files[0].Name!="package"||
                    form.Files[0].Length is <1||form.Files[0].Length>StudentPaperPackage.MaxPackageBytes+1024L*1024)
                    throw new InvalidDataException("需要一个 metadata 字段和一个加密 package 文件。");
                var upload=JsonSerializer.Deserialize<UploadExamBundleRequest>(form["metadata"][0]!,JsonDefaults.Create())
                    ??throw new InvalidDataException("发布信息不能为空。");
                if(upload.Publication is null||upload.Envelope is null)throw new InvalidDataException("发布清单和信封不能为空。");
                using var buffer=new MemoryStream(checked((int)form.Files[0].Length));
                await form.Files[0].CopyToAsync(buffer,ct);
                var result=await service.PublishUploadedAsync(examId,upload,buffer.ToArray(),
                    ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context),ct);
                return ApiEndpointResults.FromVersioning(context,configuration,upload.Publication.RequestId,result);
            }
            catch(Exception error)when(error is JsonException or InvalidDataException or ArgumentException)
            {return ApiEndpointResults.Error(context,configuration,context.TraceIdentifier,"INVALID_INPUT","上传的加密包或发布说明无效，未激活新版本。",StatusCodes.Status400BadRequest);}
            finally{admission.Slots.Release();}
        }).DisableAntiforgery().WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();
        return api;
    }
}

public sealed class PublicationUploadGate:IDisposable
{
    public SemaphoreSlim Slots{get;}=new(2,2);
    public void Dispose()=>Slots.Dispose();
}
