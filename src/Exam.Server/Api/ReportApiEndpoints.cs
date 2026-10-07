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
        var group = api.MapGroup("/reports").WithTags("Reporting");
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
            var published = await reports.ReadAsync(examId, snapshotId, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return new CurrentReportResult(reports, examId, published.Status.SnapshotId!,
                ApiEndpointResults.Success(context, configuration, context.TraceIdentifier, published));
        }).WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();
        group.MapGet("/{examId}/xlsx", async (string examId, string snapshotId, [FromServices] ReportExportService exporter,
            [FromServices] ReportPublicationService reports, [FromServices] ServerNodeEnvelope identity,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            var bytes = await exporter.ExportAsync(examId, snapshotId, cancellationToken);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            context.Response.Headers.CacheControl = "no-store";
            return new CurrentReportResult(reports, examId, snapshotId,
                Results.File(bytes, ReportExportService.ContentType, "report.xlsx", enableRangeProcessing: false), response =>
                {
                    response.Headers["X-Exam-Server-Id"] = identity.ServerId;
                    response.Headers["X-Exam-Node-Id"] = identity.NodeId;
                    response.Headers["X-Exam-Server-Epoch"] = identity.ServerEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    response.Headers["X-Exam-Report-Snapshot"] = snapshotId;
                    response.Headers["X-Exam-Workbook-Sha256"] = digest;
                });
        }).WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();
        return group;
    }

    // The durable-confirmation filter runs first. Re-read the authoritative snapshot after that
    // potentially slow peer exchange, before starting headers/body, without holding a database lock
    // while producing or transmitting the report. Download headers are issued only after validation.
    private sealed class CurrentReportResult(ReportPublicationService reports, string examId, string snapshotId,
        IResult result, Action<HttpResponse>? configureResponse = null) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await reports.ValidateAsync(examId, snapshotId, context.RequestAborted);
            configureResponse?.Invoke(context.Response);
            await result.ExecuteAsync(context);
        }
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
