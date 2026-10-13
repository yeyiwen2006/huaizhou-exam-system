using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.Resources;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.Security;

public sealed class ServerCertificateFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "exam-certificate-" + Guid.NewGuid().ToString("N"));
    private const string Password = "synthetic-pfx-password";

    public ServerCertificateFilesTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PfxPrivateKeyAndPublicCertificateFilesSupportPublicationAndUnlockWithoutStore()
    {
        using var certificate = CreateCertificate();
        var pfx = Path.Combine(_directory, "node.pfx");
        var publicPath = Path.Combine(_directory, "node.cer");
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pkcs12, Password));
        await File.WriteAllBytesAsync(publicPath, certificate.Export(X509ContentType.Cert));
        var source = new ServerCertificateSource(null, pfx, Password);
        var configuration = new ServiceConfiguration("server", "node", "0.0.0.0", 443, _directory, false, 1);
        var readiness = await new NodeCertificateStartupReadinessProbe(source).RunAsync(configuration, default);
        Assert.True(Assert.Single(readiness).Passed);

        var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>(StringComparer.Ordinal) { "node" });
        var provider = new CertificateStorePublicationProvider(
            new Dictionary<string, ServerCertificateSource> { ["node"] = new(certificate.Thumbprint, publicPath) }, policy);
        using var lease = provider.Acquire();
        Assert.False(Assert.Single(lease.Recipients).Certificate.HasPrivateKey);
        using var plaintext = new MemoryStream("跨设备部署题包"u8.ToArray());
        using var ciphertext = new MemoryStream();
        var envelope = await new PackageEncryptionService(policy: policy)
            .EncryptAsync(plaintext, ciphertext, "package", 1, lease.Recipients);
        using var material = new PackageUnlockMaterial(envelope.PackageId, envelope.PaperVersion,
            envelope.ChunkSize, envelope.NoncePrefix, new CertificateStorePackageKeyUnwrapper("node", source).Unwrap(envelope));
        ciphertext.Position = 0;
        using var restored = new MemoryStream();
        await PackageCiphertext.DecryptAsync(ciphertext, restored, material);
        Assert.Equal(plaintext.ToArray(), restored.ToArray());
    }

    [Fact]
    public async Task WrongPfxPasswordOrMismatchedCertificateBlocksStartup()
    {
        using var certificate = CreateCertificate();
        var path = Path.Combine(_directory, "node.pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pkcs12, Password));
        var configuration = new ServiceConfiguration("server", "node", "0.0.0.0", 443, _directory, false, 1);
        foreach (var source in new[]
        {
            new ServerCertificateSource(null, path, "incorrect-password"),
            new ServerCertificateSource("00", path, Password)
        })
        {
            var check = Assert.Single(await new NodeCertificateStartupReadinessProbe(source).RunAsync(configuration, default));
            Assert.False(check.Passed);
            Assert.DoesNotContain(Password, check.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublicationFromLocalPfxNeverExposesItsPrivateKey()
    {
        using var certificate = CreateCertificate();
        var path = Path.Combine(_directory, "node.pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pkcs12, Password));
        var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>(StringComparer.Ordinal) { "node" });
        using var lease = new CertificateStorePublicationProvider(
            new Dictionary<string, ServerCertificateSource> { ["node"] = new(null, path, Password) }, policy).Acquire();
        Assert.False(Assert.Single(lease.Recipients).Certificate.HasPrivateKey);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-file-node", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
