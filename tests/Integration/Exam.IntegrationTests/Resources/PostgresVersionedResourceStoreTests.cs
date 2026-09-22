using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Resources;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresVersionedResourceStoreTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task EncryptedPackage_RoundTripsThroughDatabase_AndBothNodesCanDecrypt()
    {
        using var nodeA = CreateCertificate("node-a");
        using var nodeB = CreateCertificate("node-b");
        var source = RandomNumberGenerator.GetBytes(16_777);
        var packageId = $"package-{Guid.NewGuid():N}";
        var examId = $"exam-{Guid.NewGuid():N}";
        var policy = new ManagedNodeEncryptionPolicy(
            new HashSet<string>(["node-a", "node-b"], StringComparer.Ordinal));
        var encryption = new PackageEncryptionService(4096, policy);
        await using var encrypted = new MemoryStream();
        var envelope = await encryption.EncryptAsync(
            new MemoryStream(source),
            encrypted,
            packageId,
            2,
            [new("node-a", nodeA), new("node-b", nodeB)]);
        var createdAt = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);

        await new PostgresVersionedResourceStore(fixture.DataSource, policy).SaveAsync(
            new VersionedEncryptedResource(
                packageId,
                examId,
                "paper-package",
                2,
                encrypted.ToArray(),
                envelope,
                createdAt,
                "integration-test"));
        var restored = await new PostgresVersionedResourceStore(fixture.DataSource, policy)
            .FindAsync(examId, "paper-package", 2);

        Assert.NotNull(restored);
        Assert.Equal(createdAt, restored.CreatedAt);
        foreach (var (nodeId, certificate) in new[] { ("node-a", nodeA), ("node-b", nodeB) })
        {
            await using var plaintext = new MemoryStream();
            await encryption.DecryptAsync(
                new MemoryStream(restored.EncryptedContent),
                plaintext,
                restored.Envelope,
                nodeId,
                certificate);
            Assert.Equal(source, plaintext.ToArray());
        }
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
