using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Exam.Contracts.Resources;

/// <summary>The shared server/student decoder for HEXAMPKG format version 2.</summary>
public static class PackageCiphertext
{
    private static readonly byte[] Magic = "HEXAMPKG"u8.ToArray();
    private const int NoncePrefixLength = 8;
    private const int TagLength = 16;

    /// <summary>
    /// Decode into an unpublished temporary stream. No consumer may read it until this
    /// method completes: the final authenticated record is required even for empty input.
    /// Appended plaintext is rolled back on any failure; material.DataKey is always cleared.
    /// </summary>
    public static async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        PackageUnlockMaterial material,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        byte[]? plainBuffer = null;
        long? initialPosition = null;
        try
        {
            ArgumentNullException.ThrowIfNull(ciphertext);
            ArgumentNullException.ThrowIfNull(plaintext);
            if (!ciphertext.CanRead || !plaintext.CanWrite || !plaintext.CanSeek ||
                plaintext.Position != plaintext.Length)
            {
                throw new ArgumentException("解密输入必须可读，输出必须是可追加并可回滚的临时流。");
            }

            initialPosition = plaintext.Position;
            if (string.IsNullOrWhiteSpace(material.PackageId) || material.PaperVersion < 1 ||
                material.ChunkSize is < 4096 or > 4 * 1024 * 1024 || material.DataKey?.Length != 32)
            {
                throw new CryptographicException("题包解锁材料无效。");
            }

            var expectedPrefix = Convert.FromBase64String(material.NoncePrefix);
            if (expectedPrefix.Length != NoncePrefixLength)
            {
                throw new CryptographicException("题包随机数前缀无效。");
            }

            var header = new byte[Magic.Length + 1 + sizeof(int) + NoncePrefixLength];
            await ciphertext.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || header[Magic.Length] != 2 ||
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(Magic.Length + 1, sizeof(int))) != material.ChunkSize)
            {
                throw new CryptographicException("题包格式或分块配置与解锁材料不匹配。");
            }

            var prefix = header.AsSpan(Magic.Length + 1 + sizeof(int)).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(prefix, expectedPrefix))
            {
                throw new CryptographicException("题包内容与解锁材料不匹配。");
            }

            using var aes = new AesGcm(material.DataKey, TagLength);
            var encryptedBuffer = new byte[material.ChunkSize];
            plainBuffer = new byte[material.ChunkSize];
            var tag = new byte[TagLength];
            var lengthBuffer = new byte[sizeof(int)];
            var chunkIndex = 0;
            while (true)
            {
                await ciphertext.ReadExactlyAsync(lengthBuffer, cancellationToken);
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
                if (length == -1)
                {
                    await ciphertext.ReadExactlyAsync(tag, cancellationToken);
                    aes.Decrypt(
                        CreateNonce(prefix, chunkIndex),
                        ReadOnlySpan<byte>.Empty,
                        tag,
                        Span<byte>.Empty,
                        Encoding.UTF8.GetBytes(FormattableString.Invariant(
                            $"{material.PackageId}\n{material.PaperVersion}\nfinal\n{chunkIndex}")));
                    var trailing = new byte[1];
                    if (await ciphertext.ReadAsync(trailing, cancellationToken) != 0)
                    {
                        throw new CryptographicException("题包认证结束标记之后仍有多余数据。");
                    }

                    break;
                }

                if (length <= 0 || length > material.ChunkSize)
                {
                    throw new CryptographicException("题包分块长度无效。");
                }

                await ciphertext.ReadExactlyAsync(encryptedBuffer.AsMemory(0, length), cancellationToken);
                await ciphertext.ReadExactlyAsync(tag, cancellationToken);
                aes.Decrypt(
                    CreateNonce(prefix, chunkIndex),
                    encryptedBuffer.AsSpan(0, length),
                    tag,
                    plainBuffer.AsSpan(0, length),
                    Encoding.UTF8.GetBytes(FormattableString.Invariant(
                        $"{material.PackageId}\n{material.PaperVersion}\n{chunkIndex}")));
                await plaintext.WriteAsync(plainBuffer.AsMemory(0, length), cancellationToken);
                chunkIndex = checked(chunkIndex + 1);
            }

            await plaintext.FlushAsync(cancellationToken);
        }
        catch
        {
            if (initialPosition is { } position)
            {
                plaintext.Position = position;
                plaintext.SetLength(position);
            }

            throw;
        }
        finally
        {
            material.Dispose();
            if (plainBuffer is not null)
            {
                CryptographicOperations.ZeroMemory(plainBuffer);
            }
        }
    }

    private static byte[] CreateNonce(byte[] prefix, int chunkIndex)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(NoncePrefixLength), chunkIndex);
        return nonce;
    }
}
