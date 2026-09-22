using System.Security.Cryptography;

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
    public void Dispose()
    {
        if (DataKey is not null)
        {
            CryptographicOperations.ZeroMemory(DataKey);
        }
    }
}
