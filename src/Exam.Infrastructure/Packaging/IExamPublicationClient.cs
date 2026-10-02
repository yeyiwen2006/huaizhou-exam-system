using Exam.Contracts.Administration;
using Exam.Contracts.Exams;
using Exam.Contracts.Versioning;

namespace Exam.Infrastructure.Packaging;

/// <summary>只消费当前考务 API；客户端不指定服务器读写路径，也不自行选择受管证书。</summary>
public interface IExamPublicationClient
{
    long Generation { get; }
    string? ServerId { get; }
    Task<ExamVersionSnapshot> GetExamAsync(string examId, CancellationToken ct = default);
    Task<ExamDetail> GetExamDetailAsync(string examId, CancellationToken ct = default);
    Task<PublicationRecipients> GetPublicationRecipientsAsync(CancellationToken ct = default);
    Task<ExamVersionSnapshot> UploadPublicationAsync(string examId, UploadExamBundleRequest upload,
        string encryptedPath, CancellationToken ct = default);
}
