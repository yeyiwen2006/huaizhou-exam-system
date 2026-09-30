using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;
using Exam.Domain.Common;
using Exam.Domain.Reporting;

namespace Exam.Application.Reporting;

public sealed record ReportSourceSnapshot(ReportStatus Status, ReportCalculationInput? Input);
public interface IReportSource
{
    ValueTask<ReportSourceSnapshot> ReadAsync(string examId, CancellationToken cancellationToken = default);
}

// Rebuildable from durable inputs after a restart; no stale background task can
// overwrite a newer publication. All five tables switch as one document.
public sealed class ReportPublicationService(IReportSource source)
{
    public async Task<ReportStatus> StatusAsync(string examId, CancellationToken cancellationToken = default)
    {
        var status = (await source.ReadAsync(examId, cancellationToken)).Status;
        ReportStatusValidator.Validate(status);
        if (status.ExamId != examId) throw new ArgumentException("报表源考试不一致。");
        return status;
    }
    public async Task<PublishedReport> ReadAsync(string examId, string? expectedSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await source.ReadAsync(examId, cancellationToken);
        if (snapshot.Status.ExamId != examId || snapshot.Input is { } input && input.ExamId != examId)
            throw new ArgumentException("报表源考试不一致。");
        EnsureReady(snapshot.Status, expectedSnapshot);
        if (snapshot.Input is null) throw new DomainRuleException(ErrorCodes.ReportNotReady, "报表缺少权威输入。");
        var result = await Task.Run(() => ReportCalculator.Calculate(snapshot.Input), cancellationToken);
        ReportResultShapeValidator.Validate(result);
        await ValidateAsync(examId, snapshot.Status.SnapshotId!, cancellationToken);
        return new(1, snapshot.Status, JsonSerializer.SerializeToElement(result, ReportJson.Create()));
    }
    public async Task ValidateAsync(string examId, string expectedSnapshot, CancellationToken cancellationToken = default) =>
        EnsureReady(await StatusAsync(examId, cancellationToken), expectedSnapshot);
    private static void EnsureReady(ReportStatus status, string? expectedSnapshot)
    {
        ReportStatusValidator.Validate(status);
        if (status.State != ReportState.Ready || string.IsNullOrWhiteSpace(status.SnapshotId))
            throw new DomainRuleException(ErrorCodes.ReportNotReady, status.Reason, retryable: true);
        if (expectedSnapshot is not null && !string.Equals(expectedSnapshot, status.SnapshotId, StringComparison.Ordinal))
            throw new DomainRuleException(ErrorCodes.VersionConflict, "报表版本已变化，请重新加载。", retryable: true);
    }
}
