using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class NodeCertificateStartupReadinessProbeTests
{
    [Fact]
    public async Task UsablePrivateKey_PassesWithSamePaddingAsPackageUnlock()
    {
        using var rsa = RSA.Create(2048);
        var certificate = CreateCertificate(rsa, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var thumbprint = certificate.Thumbprint;
        var probe = new NodeCertificateStartupReadinessProbe(thumbprint, () => [certificate]);

        var check = Assert.Single(await probe.RunAsync(Configuration(), CancellationToken.None));

        Assert.True(check.Passed, check.Message);
        Assert.True(check.BlocksWrites);
    }

    [Fact]
    public async Task PublicCertificateWithoutPrivateKey_BlocksStartup()
    {
        using var rsa = RSA.Create(2048);
        using var original = CreateCertificate(rsa, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var publicOnly = X509CertificateLoader.LoadCertificate(original.Export(X509ContentType.Cert));
        var probe = new NodeCertificateStartupReadinessProbe(original.Thumbprint, () => [publicOnly]);

        var check = Assert.Single(await probe.RunAsync(Configuration(), CancellationToken.None));

        Assert.False(check.Passed);
        Assert.True(check.BlocksWrites);
    }

    [Fact]
    public async Task MissingOrExpiredCertificate_BlocksStartup()
    {
        using var rsa = RSA.Create(2048);
        var expired = CreateCertificate(rsa, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-1));
        var thumbprint = expired.Thumbprint;
        var missing = new NodeCertificateStartupReadinessProbe("00", () => []);
        var expiredProbe = new NodeCertificateStartupReadinessProbe(thumbprint, () => [expired]);

        Assert.False(Assert.Single(await missing.RunAsync(Configuration(), CancellationToken.None)).Passed);
        Assert.False(Assert.Single(await expiredProbe.RunAsync(Configuration(), CancellationToken.None)).Passed);
    }

    [Fact]
    public async Task InaccessibleStore_BlocksWithoutExposingUnderlyingError()
    {
        var probe = new NodeCertificateStartupReadinessProbe(
            new string('A', 40),
            () => throw new CryptographicException("synthetic private key path"));

        var check = Assert.Single(await probe.RunAsync(Configuration(), CancellationToken.None));

        Assert.False(check.Passed);
        Assert.True(check.BlocksWrites);
        Assert.DoesNotContain("synthetic private key path", check.Message, StringComparison.Ordinal);
    }

    private static X509Certificate2 CreateCertificate(RSA rsa, DateTimeOffset from, DateTimeOffset to)
    {
        var request = new CertificateRequest("CN=synthetic-f01-node", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(from, to);
    }

    private static ServiceConfiguration Configuration() =>
        new("synthetic-server", "synthetic-node", "127.0.0.1", 443, "/synthetic", false, 1);
}
