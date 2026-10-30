using System.Security.Cryptography;
using Exam.Contracts.Submissions;

namespace Exam.Contracts.Resources;

public sealed record PackageUnlockRequest(
    string RequestId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    long PaperVersion,
    string UnlockReference,
    string ServerId,
    long ServerEpoch);

/// <summary>
/// Released only to the currently authorized device over HTTPS after the server has
/// started the exam. Contains no server private key or wrapped node-key material.
/// Dispose after use; the shared decoder also clears DataKey on success and failure.
/// </summary>
public sealed record PackageUnlockMaterial(
    string PackageId,
    long PaperVersion,
    int ChunkSize,
    string NoncePrefix,
    byte[] DataKey) : IDisposable
{
    public AuthorizedAnswerRecovery? AnswerRecovery { get; init; }

    public void Dispose()
    {
        if (DataKey is not null)
        {
            CryptographicOperations.ZeroMemory(DataKey);
        }
    }
}

/// <summary>与本次解锁同事务授权的答题恢复；Progress 为空表示服务器尚无已保存答案。</summary>
public sealed record AuthorizedAnswerRecovery(
    string ExamId, string CandidateId, string ParticipationId, long EntryVersion,
    string DeviceId, long AuthorizationVersion, PersistedProgress? Progress)
{
    /// <summary>从此授权版本起，本设备一直保有授权；旧客户端未提供时不接续旧授权本地编辑。</summary>
    public long? RetainLocalAnswersFromAuthorizationVersion { get; init; }
}
