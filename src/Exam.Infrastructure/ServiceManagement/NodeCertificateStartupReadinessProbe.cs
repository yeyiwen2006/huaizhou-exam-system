using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Checks that the configured node certificate can use its RSA private key.</summary>
public sealed class NodeCertificateStartupReadinessProbe : IStartupReadinessProbe
{
    private const string CheckName = "node-certificate-private-key";
    private readonly string _thumbprint;
    private readonly Func<X509Certificate2Collection> _loadCertificates;
    private readonly TimeProvider _timeProvider;
    private readonly bool _usesCertificateFile;

    public NodeCertificateStartupReadinessProbe(ServerCertificateSource source, TimeProvider? timeProvider = null)
        : this(source.Thumbprint ?? string.Empty, () => [source.Load()], timeProvider)
    {
        _usesCertificateFile = !string.IsNullOrWhiteSpace(source.CertificatePath);
    }

    public NodeCertificateStartupReadinessProbe(
        string thumbprint,
        StoreLocation storeLocation = StoreLocation.LocalMachine,
        TimeProvider? timeProvider = null)
        : this(thumbprint, () => LoadFromStore(storeLocation), timeProvider)
    {
    }

    public NodeCertificateStartupReadinessProbe(
        string thumbprint,
        Func<X509Certificate2Collection> loadCertificates,
        TimeProvider? timeProvider = null)
    {
        _thumbprint = thumbprint?.Replace(" ", string.Empty, StringComparison.Ordinal) ?? string.Empty;
        _loadCertificates = loadCertificates ?? throw new ArgumentNullException(nameof(loadCertificates));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<StartupCheckResult>> RunAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (_thumbprint.Length == 0 && !_usesCertificateFile)
            return Task.FromResult(Block("节点解密证书指纹未配置。"));
        if (_thumbprint.Length > 128 || _thumbprint.Length % 2 != 0 ||
            !_thumbprint.All(char.IsAsciiHexDigit))
            return Task.FromResult(Block("节点解密证书指纹无效。"));

        try
        {
            var certificates = _loadCertificates();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matching = _thumbprint.Length == 0 ? certificates :
                    certificates.Find(X509FindType.FindByThumbprint, _thumbprint, validOnly: false);
                if (matching.Count != 1)
                    return Task.FromResult(Block("节点解密证书不存在或不唯一。"));

                var certificate = matching[0];
                var now = _timeProvider.GetUtcNow();
                if (now < certificate.NotBefore || now > certificate.NotAfter)
                    return Task.FromResult(Block("节点解密证书不在有效期内。"));

                using var privateKey = certificate.GetRSAPrivateKey();
                using var publicKey = certificate.GetRSAPublicKey();
                if (privateKey is null || publicKey is null)
                    return Task.FromResult(Block("节点解密证书没有可用的 RSA 私钥。"));

                // Use the same padding and private-key operation as package key release.
                var challenge = RandomNumberGenerator.GetBytes(32);
                var encrypted = publicKey.Encrypt(challenge, RSAEncryptionPadding.OaepSHA256);
                var decrypted = privateKey.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(challenge, decrypted))
                        return Task.FromResult(Block("节点解密证书私钥验证失败。"));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(challenge);
                    CryptographicOperations.ZeroMemory(decrypted);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StartupCheckResult>>(
                    [new(CheckName, true, "节点解密证书及私钥可用。", true)]);
            }
            finally
            {
                foreach (var certificate in certificates)
                    certificate.Dispose();
            }
        }
        catch (Exception exception) when (exception is CryptographicException or UnauthorizedAccessException or
            IOException or SecurityException or PlatformNotSupportedException)
        {
            return Task.FromResult(Block("节点解密证书或私钥不可访问。"));
        }
    }

    private static X509Certificate2Collection LoadFromStore(StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates;
    }

    private static IReadOnlyList<StartupCheckResult> Block(string message) =>
        [new(CheckName, false, message, true)];
}
