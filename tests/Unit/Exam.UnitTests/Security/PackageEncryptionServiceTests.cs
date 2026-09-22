using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Infrastructure.Security;

namespace Exam.UnitTests.Security;

public sealed class PackageEncryptionServiceTests
{
    [Fact]
    public async Task Package_CanBeDecryptedByEveryConfiguredNode()
    {
        using var nodeA = CreateCertificate("node-a");
        using var nodeB = CreateCertificate("node-b");
        var source = RandomNumberGenerator.GetBytes(23_417);
        var service = new PackageEncryptionService(4096);
        await using var encrypted = new MemoryStream();

        var envelope = await service.EncryptAsync(
            new MemoryStream(source),
            encrypted,
            "paper-package-1",
            3,
            [new("node-a", nodeA), new("node-b", nodeB)]);

        foreach (var (nodeId, certificate) in new[] { ("node-a", nodeA), ("node-b", nodeB) })
        {
            encrypted.Position = 0;
            await using var restored = new MemoryStream();
            await service.DecryptAsync(encrypted, restored, envelope, nodeId, certificate);
            Assert.Equal(source, restored.ToArray());
        }
    }

    [Fact]
    public async Task UnknownNode_AndTamperedCiphertext_AreRejected()
    {
        using var nodeA = CreateCertificate("node-a");
        using var intruder = CreateCertificate("intruder");
        var service = new PackageEncryptionService(4096);
        await using var encrypted = new MemoryStream();
        var envelope = await service.EncryptAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(5000)),
            encrypted,
            "paper-package-2",
            1,
            [new("node-a", nodeA)]);

        encrypted.Position = 0;
        await Assert.ThrowsAsync<CryptographicException>(() => service.DecryptAsync(
            encrypted,
            new MemoryStream(),
            envelope,
            "intruder",
            intruder));

        var bytes = encrypted.ToArray();
        const int firstCiphertextByte = 8 + 1 + sizeof(int) + 8 + sizeof(int);
        bytes[firstCiphertextByte] ^= 0x5A;
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => service.DecryptAsync(
            new MemoryStream(bytes),
            new MemoryStream(),
            envelope,
            "node-a",
            nodeA));
    }

    [Fact]
    public async Task TruncatedAtChunkBoundary_AndTrailingData_AreRejectedWithoutLeavingPlaintext()
    {
        using var nodeA = CreateCertificate("node-a");
        var service = new PackageEncryptionService(4096);
        await using var encrypted = new MemoryStream();
        var envelope = await service.EncryptAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(9000)),
            encrypted,
            "paper-package-3",
            1,
            [new("node-a", nodeA)]);
        var bytes = encrypted.ToArray();

        var truncated = bytes[..(bytes.Length - 16)];
        await using var truncatedOutput = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() => service.DecryptAsync(
            new MemoryStream(truncated),
            truncatedOutput,
            envelope,
            "node-a",
            nodeA));
        Assert.Empty(truncatedOutput.ToArray());

        var withTrailingData = bytes.Concat(new byte[] { 0x42 }).ToArray();
        await using var trailingOutput = new MemoryStream();
        await Assert.ThrowsAsync<CryptographicException>(() => service.DecryptAsync(
            new MemoryStream(withTrailingData),
            trailingOutput,
            envelope,
            "node-a",
            nodeA));
        Assert.Empty(trailingOutput.ToArray());
    }

    [Fact]
    public async Task ManagedNodePolicy_RejectsPackageThatCannotBeUnlockedOnEveryRequiredNode()
    {
        using var nodeA = CreateCertificate("node-a");
        var policy = new ManagedNodeEncryptionPolicy(
            new HashSet<string>(["node-a", "node-b"], StringComparer.Ordinal));
        var service = new PackageEncryptionService(4096, policy);

        await Assert.ThrowsAsync<CryptographicException>(() => service.EncryptAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(5000)),
            new MemoryStream(),
            "paper-package-required-nodes",
            1,
            [new("node-a", nodeA)]));
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
