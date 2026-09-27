using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

/// <summary>回执只绑定原考试、考生、参试、提交和试卷，不授予重新作答权限。</summary>
internal static class SubmissionReceiptValidation
{
    public static bool Matches(SubmissionReceipt? receipt, FixedSubmissionRecord record, string examId, string candidateId) =>
        receipt is not null && !string.IsNullOrWhiteSpace(receipt.ReceiptId) &&
        receipt.SubmissionId == record.SubmissionId && receipt.ExamId == examId && receipt.CandidateId == candidateId &&
        receipt.ParticipationId == record.ParticipationId && receipt.PaperVersion == record.PaperVersion &&
        receipt.PaperVersion > 0 && receipt.GradingVersion > 0 && receipt.ReceivedAt != default;
}

/// <summary>独立的回执持久化能力；未实现时必须报告失败，不能伪造可靠保存。</summary>
public interface ISubmissionReceiptStore
{
    Task<StoreWriteResult> SaveSubmissionReceiptAsync(LocalStateKey key, SubmissionReceipt receipt, CancellationToken ct = default);
}
