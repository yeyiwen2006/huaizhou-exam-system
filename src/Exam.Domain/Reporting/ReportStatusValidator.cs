using Exam.Contracts.Reporting;

namespace Exam.Domain.Reporting;

public static class ReportStatusValidator
{
    public static void Validate(ReportStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (string.IsNullOrWhiteSpace(status.ExamId) || !Enum.IsDefined(status.State) ||
            status.ParticipantCount < 0 || status.CollectedCount < 0 || status.GradedCount < 0 ||
            status.CollectedCount > status.ParticipantCount || status.GradedCount > status.CollectedCount ||
            status.RosterVersion < 0 || status.PaperVersion < 0 || status.GradingVersion < 0 ||
            status.MissingParticipants is null ||
            status.MissingParticipants.Count != status.ParticipantCount - status.CollectedCount ||
            status.MissingParticipants.Any(member => member is null || string.IsNullOrWhiteSpace(member.CandidateId)) ||
            status.MissingParticipants.Select(member => member.CandidateId).Distinct(StringComparer.Ordinal).Count() != status.MissingParticipants.Count)
            throw new ArgumentException("报表进度计数、名单或版本无效。");
        if (status.State == ReportState.Ready)
        {
            if (status.ParticipantCount == 0 || status.CollectedCount != status.ParticipantCount ||
                status.GradedCount != status.ParticipantCount || status.PaperVersion == 0 || status.GradingVersion == 0 ||
                string.IsNullOrWhiteSpace(status.SnapshotId))
                throw new ArgumentException("报表开放状态与计数或版本不一致。");
        }
        else if (status.SnapshotId is not null ||
            status.State == ReportState.NoParticipants && status.ParticipantCount != 0 ||
            status.State == ReportState.Collecting && status.MissingParticipants.Count == 0)
            throw new ArgumentException("报表未开放状态与快照或名单不一致。");
    }
}
