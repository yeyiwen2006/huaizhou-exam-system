using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Versioning;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresPaperStructureReaderTests(PostgresDatabaseFixture database)
{
    [Fact]
    public async Task ReaderUsesOneConnectionAndObservesNewlyPublishedStructure()
    {
        var id = "paper-read-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var service = new PostgresExamVersioningService(database.DataSource);
        var created = service.CreateExam(new(id, new(now.AddHours(1), now.AddHours(2)), now, id + "-create"));
        Assert.True(created.Succeeded, created.Error);
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { MaxPoolSize = 1, MinPoolSize = 0, Timeout = 5 };
        await using var oneConnection = NpgsqlDataSource.Create(settings.ConnectionString);
        var reader = new PostgresExamVersioningService(oneConnection);
        Assert.Null(await reader.GetPaperStructureAsync("missing-" + id));
        Assert.Null(await reader.GetPaperStructureAsync(id));
        var structure = new PaperStructure([new("q", 1, "s", "g", "single", ["A", "B"])]);
        var resourceId = id + "-package";
        await StoreSyntheticPackageAsync(id, resourceId, now);
        var published = service.PublishPaper(new(id, 0, resourceId, structure, [],
            new(true, true, true, true), "synthetic-operator", "合成试卷", now, id + "-publish"));
        Assert.True(published.Succeeded, published.Error);
        var expected = JsonSerializer.Serialize(structure, JsonDefaults.Create());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            reader.GetPaperStructureAsync(id, cancellation.Token).AsTask()));
        Assert.All(reads, item => Assert.Equal(expected, JsonSerializer.Serialize(item, JsonDefaults.Create())));
        // Full exam reads also must return their first connection before reading the canonical roster.
        var full = reader.GetExam(id);
        Assert.True(full.Succeeded);
        Assert.Equal(expected, JsonSerializer.Serialize(full.Value!.Paper!.Structure, JsonDefaults.Create()));
    }

    private async Task StoreSyntheticPackageAsync(string examId, string resourceId, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=node-a", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
        var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>(["node-a"], StringComparer.Ordinal));
        var encryption = new PackageEncryptionService(4096, policy);
        await using var encrypted = new MemoryStream();
        // The projection test needs a persisted encrypted resource, not actual import/layout materials.
        var envelope = await encryption.EncryptAsync(new MemoryStream(RandomNumberGenerator.GetBytes(1024)),
            encrypted, resourceId, 1, [new("node-a", certificate)]);
        await new PostgresVersionedResourceStore(database.DataSource, policy).SaveAsync(
            new VersionedEncryptedResource(resourceId, examId, "paper-package", 1,
                encrypted.ToArray(), envelope, now, "synthetic-operator"));
    }

    [Fact]
    public async Task WaitingForTheOnlyConnectionCanBeCancelledWithoutBlockingCaller()
    {
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        { MaxPoolSize = 1, MinPoolSize = 0, Timeout = 5 };
        await using var oneConnection = NpgsqlDataSource.Create(settings.ConnectionString);
        await using var held = await oneConnection.OpenConnectionAsync();
        var reader = new PostgresExamVersioningService(oneConnection);
        using var cancellation = new CancellationTokenSource();
        var pending = reader.GetPaperStructureAsync("synthetic-wait", cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
