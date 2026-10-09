#if DEBUG
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Exam.Admin.ServiceManagement;

internal static class LocalDebugManagementTransport
{
    public static HttpMessageHandler Create()
    {
        var path = Environment.GetEnvironmentVariable("EXAM_ADMIN_MANAGEMENT_CERTIFICATE");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请通过本机测试入口启动考务端。");
        using var expected = X509CertificateLoader.LoadCertificateFromFile(path);
        var raw = expected.RawData;
        return new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null &&
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                certificate.RawData.AsSpan().SequenceEqual(raw)
        };
    }
}
#endif
