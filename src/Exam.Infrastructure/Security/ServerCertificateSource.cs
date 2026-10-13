using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Exam.Infrastructure.Security;

/// <summary>Loads a server-owned certificate from a protected file or the Windows certificate store.</summary>
public sealed record ServerCertificateSource(
    string? Thumbprint,
    string? CertificatePath = null,
    string? CertificatePassword = null,
    StoreLocation StoreLocation = StoreLocation.LocalMachine)
{
    public override string ToString() => $"ServerCertificateSource {{ Thumbprint = {Thumbprint}, CertificatePath = {CertificatePath} }}";

    public X509Certificate2 Load(bool requirePrivateKey = true)
    {
        var thumbprint = Thumbprint?.Replace(" ", string.Empty, StringComparison.Ordinal) ?? string.Empty;
        if (thumbprint.Length > 128 || thumbprint.Length % 2 != 0 || !thumbprint.All(char.IsAsciiHexDigit))
            throw new CryptographicException("服务器证书指纹无效。");

        X509Certificate2 certificate;
        if (!string.IsNullOrWhiteSpace(CertificatePath))
        {
            // Schannel needs a temporary key container on Windows. Linux can keep the key in memory.
            // Neither mode persists an imported private key beyond the certificate lifetime.
            var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509Certificate2.GetCertContentType(CertificatePath) == X509ContentType.Pkcs12
                ? X509CertificateLoader.LoadPkcs12FromFile(CertificatePath, CertificatePassword, flags)
                : X509CertificateLoader.LoadCertificateFromFile(CertificatePath);
        }
        else
        {
            if (thumbprint.Length == 0)
                throw new CryptographicException("服务器证书路径或指纹未配置。");
            if (!OperatingSystem.IsWindows() && StoreLocation == StoreLocation.LocalMachine)
                throw new CryptographicException("此平台须通过证书文件配置服务器证书。");
            using var store = new X509Store(StoreName.My, StoreLocation);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certificates = store.Certificates;
            certificate = null!;
            try
            {
                foreach (var candidate in certificates)
                {
                    if (!string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)) continue;
                    if (certificate is not null)
                        throw new CryptographicException("服务器证书不唯一。");
                    certificate = candidate;
                }
                if (certificate is null) throw new CryptographicException("服务器证书未安装。");
            }
            catch
            {
                certificate?.Dispose();
                throw;
            }
            finally
            {
                foreach (var candidate in certificates)
                    if (!ReferenceEquals(candidate, certificate)) candidate.Dispose();
            }
        }

        try
        {
            if (thumbprint.Length > 0 && !string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("证书文件与配置的指纹不一致。");
            if (requirePrivateKey && !certificate.HasPrivateKey)
                throw new CryptographicException("服务器证书缺少私钥。");
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }
}
