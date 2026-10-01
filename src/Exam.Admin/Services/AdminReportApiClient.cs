using System.Net.Http;
using System.IO;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;
using Exam.Domain.Reporting;
using Exam.Reporting;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    public async Task<ReportStatus> GetReportStatusAsync(string examId, CancellationToken cancellationToken = default)
    {
        var status = await SendAsync<ReportStatus>(HttpMethod.Get,
            $"api/v1/reports/{Uri.EscapeDataString(examId)}/status", null, cancellationToken);
        if (status.ExamId != examId) throw new InvalidDataException("报表状态的考试不一致。");
        ReportStatusValidator.Validate(status);
        return status;
    }

    public async Task<PublishedReport> GetReportAsync(string examId, string snapshotId, CancellationToken cancellationToken = default)
    {
        var report = await SendAsync<PublishedReport>(HttpMethod.Get,
            $"api/v1/reports/{Uri.EscapeDataString(examId)}?snapshotId={Uri.EscapeDataString(snapshotId)}", null, cancellationToken);
        if (report.SchemaVersion != 1 || report.Status.ExamId != examId || report.Status.State != ReportState.Ready ||
            report.Status.SnapshotId != snapshotId) throw new InvalidDataException("报表结果版本或考试不一致。");
        ReportJson.Read(report);
        return report;
    }

    public async Task ExportReportAsync(string examId, string snapshotId, string destination,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        cancellationToken = deadline.Token;
        var generation = Generation;
        var client = _httpClient;
        using var response = await client.GetAsync(
            $"api/v1/reports/{Uri.EscapeDataString(examId)}/xlsx?snapshotId={Uri.EscapeDataString(snapshotId)}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureCurrentConnection(generation);
        if (!response.IsSuccessStatusCode)
            throw new AdminApiException("REPORT_EXPORT_FAILED", "导出被服务器拒绝，请刷新报表状态。", response.StatusCode);
        var workbook = await ReportWorkbookDownload.ReadAsync(response, snapshotId, cancellationToken);
        EnsureCurrentConnection(generation);
        ValidateEnvelope(new ApiEnvelope<object>(workbook.ServerId, workbook.NodeId, workbook.ServerEpoch, "report-export", null, null));
        await ReportWorkbookDownload.SaveAsync(workbook, destination, async () =>
        {
            var status = await GetReportStatusAsync(examId, cancellationToken);
            EnsureCurrentConnection(generation);
            if (status.State != ReportState.Ready || status.SnapshotId != snapshotId)
                throw new InvalidDataException("报表已失效，未替换目标文件。");
        }, cancellationToken);
    }
}
