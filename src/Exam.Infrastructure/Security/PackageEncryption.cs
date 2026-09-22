using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Exam.Contracts.Resources;

namespace Exam.Infrastructure.Security;

public sealed record NodeKeyRecipient(string NodeId, X509Certificate2 Certificate);

public sealed record EncryptedPackageEnvelope(
    string PackageId,
    long PaperVersion,
    int ChunkSize,
    string NoncePrefix,
    IReadOnlyDictionary<string, string> WrappedKeys,
    IReadOnlyDictionary<string, string> CertificateThumbprints);

public sealed record ManagedNodeEncryptionPolicy(IReadOnlySet<string> RequiredNodeIds)
{
    public static ManagedNodeEncryptionPolicy None { get; } = new(new HashSet<string>(StringComparer.Ordinal));
}

public sealed class PackageEncryptionService(
    int chunkSize = 1024 * 1024,
    ManagedNodeEncryptionPolicy? policy = null)
{
    private static readonly byte[] Magic = "HEXAMPKG"u8.ToArray();
    private const byte FormatVersion = 2;
    private const int NoncePrefixLength = 8;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly int _chunkSize = chunkSize is >= 4096 and <= 4 * 1024 * 1024
        ? chunkSize
        : throw new ArgumentOutOfRangeException(nameof(chunkSize), "加密分块必须在 4 KiB 至 4 MiB 之间。");
    private readonly ManagedNodeEncryptionPolicy _policy = policy ?? ManagedNodeEncryptionPolicy.None;

    public async Task<EncryptedPackageEnvelope> EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        string packageId,
        long paperVersion,
        IEnumerable<NodeKeyRecipient> recipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!plaintext.CanRead || !ciphertext.CanWrite)
        {
            throw new ArgumentException("加密输入必须可读且输出必须可写。");
        }

        if (string.IsNullOrWhiteSpace(packageId) || paperVersion < 1)
        {
            throw new ArgumentException("题包标识和试卷版本必须有效。");
        }

        var recipientArray = recipients?.ToArray() ?? throw new ArgumentNullException(nameof(recipients));
        if (recipientArray.Length == 0 ||
            recipientArray.Any(recipient => string.IsNullOrWhiteSpace(recipient.NodeId)) ||
            recipientArray.Select(recipient => recipient.NodeId).Distinct(StringComparer.Ordinal).Count() != recipientArray.Length)
        {
            throw new ArgumentException("至少需要一个节点证书，且节点标识不能重复。", nameof(recipients));
        }

        var recipientIds = recipientArray.Select(recipient => recipient.NodeId).ToHashSet(StringComparer.Ordinal);
        if (!_policy.RequiredNodeIds.IsSubsetOf(recipientIds))
        {
            throw new CryptographicException("题包加密收件人未覆盖全部受管主备节点。");
        }

        var dataKey = RandomNumberGenerator.GetBytes(32);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        try
        {
            var wrappedKeys = WrapForNodes(dataKey, recipientArray);
            await WriteHeaderAsync(ciphertext, noncePrefix, cancellationToken);

            using var aes = new AesGcm(dataKey, TagLength);
            var plainBuffer = new byte[_chunkSize];
            var encryptedBuffer = new byte[_chunkSize];
            var tag = new byte[TagLength];
            var chunkIndex = 0;
            while (true)
            {
                var read = await ReadChunkAsync(plaintext, plainBuffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var nonce = CreateNonce(noncePrefix, chunkIndex);
                var associatedData = CreateAssociatedData(packageId, paperVersion, chunkIndex);
                aes.Encrypt(
                    nonce,
                    plainBuffer.AsSpan(0, read),
                    encryptedBuffer.AsSpan(0, read),
                    tag,
                    associatedData);

                await WriteInt32Async(ciphertext, read, cancellationToken);
                await ciphertext.WriteAsync(encryptedBuffer.AsMemory(0, read), cancellationToken);
                await ciphertext.WriteAsync(tag, cancellationToken);
                chunkIndex = checked(chunkIndex + 1);
            }

            await WriteInt32Async(ciphertext, -1, cancellationToken);
            var finalNonce = CreateNonce(noncePrefix, chunkIndex);
            aes.Encrypt(
                finalNonce,
                ReadOnlySpan<byte>.Empty,
                Span<byte>.Empty,
                tag,
                CreateFinalAssociatedData(packageId, paperVersion, chunkIndex));
            await ciphertext.WriteAsync(tag, cancellationToken);
            await ciphertext.FlushAsync(cancellationToken);
            return new EncryptedPackageEnvelope(
                packageId,
                paperVersion,
                _chunkSize,
                Convert.ToBase64String(noncePrefix),
                new ReadOnlyDictionary<string, string>(wrappedKeys),
                new ReadOnlyDictionary<string, string>(recipientArray.ToDictionary(
                    recipient => recipient.NodeId,
                    recipient => recipient.Certificate.Thumbprint,
                    StringComparer.Ordinal)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        EncryptedPackageEnvelope envelope,
        string nodeId,
        X509Certificate2 nodeCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var material = new PackageUnlockMaterial(
            envelope.PackageId,
            envelope.PaperVersion,
            envelope.ChunkSize,
            envelope.NoncePrefix,
            UnwrapDataKey(envelope, nodeId, nodeCertificate));
        await PackageCiphertext.DecryptAsync(ciphertext, plaintext, material, cancellationToken);
    }

    public static byte[] UnwrapDataKey(
        EncryptedPackageEnvelope envelope,
        string nodeId,
        X509Certificate2 nodeCertificate)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(nodeCertificate);
        if (!envelope.WrappedKeys.TryGetValue(nodeId, out var wrappedKeyText))
        {
            throw new CryptographicException("当前节点没有该题包的解锁材料。");
        }

        if (!envelope.CertificateThumbprints.TryGetValue(nodeId, out var expectedThumbprint) ||
            !string.Equals(expectedThumbprint, nodeCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("当前节点证书与题包解锁信封记录的证书不一致。");
        }

        using var privateKey = nodeCertificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("节点证书不包含 RSA 私钥。");
        var dataKey = privateKey.Decrypt(Convert.FromBase64String(wrappedKeyText), RSAEncryptionPadding.OaepSHA256);
        if (dataKey.Length != 32)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new CryptographicException("题包数据密钥长度无效。");
        }

        return dataKey;
    }

    private Dictionary<string, string> WrapForNodes(
        byte[] dataKey,
        IEnumerable<NodeKeyRecipient> recipients)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var recipient in recipients)
        {
            using var publicKey = recipient.Certificate.GetRSAPublicKey()
                ?? throw new CryptographicException($"节点 {recipient.NodeId} 的证书不包含 RSA 公钥。");
            result.Add(
                recipient.NodeId,
                Convert.ToBase64String(publicKey.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256)));
        }

        return result;
    }

    private async Task WriteHeaderAsync(
        Stream output,
        byte[] noncePrefix,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(Magic, cancellationToken);
        await output.WriteAsync(new[] { FormatVersion }, cancellationToken);
        await WriteInt32Async(output, _chunkSize, cancellationToken);
        await output.WriteAsync(noncePrefix, cancellationToken);
    }

    private static byte[] CreateNonce(byte[] prefix, int chunkIndex)
    {
        if (chunkIndex < 0)
        {
            throw new CryptographicException("题包分块编号溢出。");
        }

        var nonce = new byte[NonceLength];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(NoncePrefixLength), chunkIndex);
        return nonce;
    }

    private static byte[] CreateAssociatedData(string packageId, long paperVersion, int chunkIndex) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"{packageId}\n{paperVersion}\n{chunkIndex}"));

    private static byte[] CreateFinalAssociatedData(string packageId, long paperVersion, int chunkCount) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"{packageId}\n{paperVersion}\nfinal\n{chunkCount}"));

    private static async Task<int> ReadChunkAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task WriteInt32Async(
        Stream output,
        int value,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        await output.WriteAsync(buffer, cancellationToken);
    }

}
