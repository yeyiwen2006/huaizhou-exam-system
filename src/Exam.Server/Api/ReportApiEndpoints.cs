using System.Security.Cryptography;
using Exam.Contracts.Commands;
using Exam.Application.Reporting;
using Exam.Domain.Reporting;
using Exam.Infrastructure.Reporting;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

public static class ReportApiEndpoints
{
    public static RouteGroupBuilder MapReportApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/reports").WithTags("G reporting");
        group.MapGet("/{examId}/status", async (string examId, [FromServices] ReportPublicationService reports,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return ApiEndpointResults.Success(context, configuration, context.TraceIdentifier,
                await reports.StatusAsync(examId, cancellationToken));
        });
        group.MapGet("/{examId}", async (string examId, string? snapshotId, [FromServices] ReportPublicationService reports,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return ApiEndpointResults.Success(context, configuration, context.TraceIdentifier,
                await reports.ReadAsync(examId, snapshotId, cancellationToken));
        });
        group.MapGet("/{examId}/xlsx", async (string examId, string snapshotId, [FromServices] ReportExportService exporter,
            [FromServices] ServerNodeEnvelope identity, HttpContext context, CancellationToken cancellationToken) =>
        {
            var bytes = await exporter.ExportAsync(examId, snapshotId, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Exam-Server-Id"] = identity.ServerId;
            context.Response.Headers["X-Exam-Node-Id"] = identity.NodeId;
            context.Response.Headers["X-Exam-Server-Epoch"] = identity.ServerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["X-Exam-Report-Snapshot"] = snapshotId;
            context.Response.Headers["X-Exam-Workbook-Sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            return Results.File(bytes, ReportExportService.ContentType, "report.xlsx", enableRangeProcessing: false);
        });
        return group;
    }
}

public sealed class ReportExportService(ReportPublicationService reports) : IDisposable
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly SemaphoreSlim _slots = new(2, 2);
    public async Task<byte[]> ExportAsync(string examId, string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        await _slots.WaitAsync(cancellationToken);
        try
        {
            var published = await reports.ReadAsync(examId, snapshotId, cancellationToken);
            var result = ReportJson.Read(published);
            var bytes = await Task.Run(() => new ReportWorkbookWriter().Write(result), cancellationToken);
            if (bytes.Length > Exam.Reporting.ReportWorkbookDownload.MaximumBytes)
                throw new InvalidOperationException("工作簿超过下载上限。");
            cancellationToken.ThrowIfCancellationRequested();
            await reports.ValidateAsync(examId, snapshotId, cancellationToken);
            return bytes;
        }
        finally { _slots.Release(); }
    }
    public void Dispose() => _slots.Dispose();
}
