using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// B07 服务器信任策略：部署指纹 pin 精确匹配证书，未配置 pin 时保持标准链校验；
/// 并用本地 SslStream 回路验证生产 HttpStudentApi 的端到端行为
/// （指纹不符 → UntrustedServerException；指纹相符 → 请求送达真实 TLS 服务器）。
/// ServerTrust 为进程级静态状态：本类串行使用并在 Dispose 恢复，避免影响其他测试。
/// </summary>
public sealed class ServerTrustTests : IDisposable
{
    private static readonly DateTimeOffset NotBefore = DateTimeOffset.UtcNow.AddHours(-1);
    private static readonly DateTimeOffset NotAfter = DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public void PinnedThumbprint_MatchingCertificate_IsTrusted()
    {
        using var certificate = CreateCertificate();
        Assert.True(ServerTrust.Configure(certificate.GetCertHashString(HashAlgorithmName.SHA256)));
        Assert.True(ServerTrust.IsPinned);

        var trusted = ServerTrust.ValidateCertificate(null, certificate, null, SslPolicyErrors.None);
        Assert.True(trusted);
    }

    [Fact]
    public void PinnedThumbprint_MismatchingCertificate_IsRejectedEvenWhenChainIsStandard()
    {
        using var pinned = CreateCertificate();
        using var other = CreateCertificate();
        Assert.True(ServerTrust.Configure(pinned.GetCertHashString(HashAlgorithmName.SHA256)));

        var trusted = ServerTrust.ValidateCertificate(null, other, null, SslPolicyErrors.None);
        Assert.False(trusted);
    }

    [Fact]
    public void WithoutPin_StandardChainPolicyApplies()
    {
        ServerTrust.Configure(null);
        Assert.False(ServerTrust.IsPinned);

        using var certificate = CreateCertificate();
        Assert.True(ServerTrust.ValidateCertificate(null, certificate, null, SslPolicyErrors.None));
        Assert.False(ServerTrust.ValidateCertificate(null, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ServerTrust.ValidateCertificate(null, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void Configure_InvalidThumbprint_IsRejectedAndKeepsCurrentPin()
    {
        using var certificate = CreateCertificate();
        var valid = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        Assert.True(ServerTrust.Configure(valid));

        Assert.False(ServerTrust.Configure("not-a-thumbprint"));
        Assert.Equal(valid.ToLowerInvariant(), ServerTrust.PinnedCertificateThumbprint);
    }

    [Fact]
    public async Task HttpStudentApi_WrongPinnedThumbprint_FailsWithUntrustedServerException()
    {
        using var certificate = CreateCertificate();
        using var other = CreateCertificate();
        Assert.True(ServerTrust.Configure(other.GetCertHashString(HashAlgorithmName.SHA256)));
        var (port, server) = StartTlsServer(certificate, _ => "{}");
        try
        {
            using var api = new HttpStudentApi();
            api.ConfigureConnection(CreateDevice(port));

            var error = await Assert.ThrowsAsync<HttpStudentApi.UntrustedServerException>(
                () => api.GetServerTimeAsync());
            Assert.Contains("不受信任", error.Message);
        }
        finally
        {
            await IgnoreServerFaults(server);
        }
    }

    [Fact]
    public async Task HttpStudentApi_MatchingPinnedThumbprint_DeliversRequestToServer()
    {
        using var certificate = CreateCertificate();
        Assert.True(ServerTrust.Configure(certificate.GetCertHashString(HashAlgorithmName.SHA256)));
        var requestedPath = string.Empty;
        var (port, server) = StartTlsServer(certificate, requestLine =>
        {
            requestedPath = requestLine.Split(' ')[1];
            return """
                {"serverId":"b07-srv","nodeId":"n1","serverEpoch":1,"correlationId":"c1",
                 "result":{"serverTime":"2026-10-01T12:00:00Z","server":{"serverId":"b07-srv","nodeId":"n1","serverEpoch":1}},
                 "error":null}
                """;
        });
        try
        {
            using var api = new HttpStudentApi();
            api.ConfigureConnection(CreateDevice(port, "b07-srv"));

            var envelope = await api.GetServerTimeAsync();

            Assert.Equal("/api/v1/commands/time", requestedPath);
            Assert.True(envelope.IsSuccess);
            Assert.NotNull(envelope.Result);
            Assert.Equal("b07-srv", envelope.Result.Server.ServerId);
        }
        finally
        {
            await IgnoreServerFaults(server);
        }
    }

    private static DeviceRecord CreateDevice(int port, string serverId = "b07-srv") => new(
        "device-b07", "credential", "A-001", "127.0.0.1", port, DateTimeOffset.UtcNow, serverId,
        ActivationStatus.Active);

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=b07-student-trust", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var created = request.CreateSelfSigned(NotBefore, NotAfter);
        // Windows Schannel 不能使用 CertificateRequest 的临时密钥做服务端认证，
        // 导出 PFX 后以默认密钥集重新导入（密钥落在本机用户配置文件，证书 1 小时后过期）。
        // X509CertificateLoader 的无参重载在本机加载带私钥的 PFX 抛 CryptographicException，
        // 故沿用旧构造函数并抑制 SYSLIB0057。
#pragma warning disable SYSLIB0057
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null);
#pragma warning restore SYSLIB0057
    }

    /// <summary>本地 HTTPS 回路：单连接 SslStream 服务器，读取请求头后按回调返回响应体。</summary>
    private static (int Port, Task Server) StartTlsServer(X509Certificate2 certificate, Func<string, string> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var tcp = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
                using var ssl = new SslStream(tcp.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false,
                    enabledSslProtocols: SslProtocols.None, checkCertificateRevocation: false);

                var buffer = new byte[8192];
                var head = new StringBuilder();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await ssl.ReadAsync(buffer);
                    if (read == 0) { break; }
                    head.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }

                var body = respond(head.ToString());
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(header);
                await ssl.WriteAsync(payload);
                await ssl.FlushAsync();
            }
            catch (Exception error) when (error is IOException or AuthenticationException or SocketException or OperationCanceledException)
            {
                // 客户端拒绝握手（负向用例）时服务器侧观察到的连接中断属预期，不作为失败。
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, server);
    }

    private static async Task IgnoreServerFaults(Task server)
    {
        try { await server; }
        catch (Exception error) when (error is IOException or AuthenticationException or SocketException or OperationCanceledException)
        {
            // 回路服务器在客户端断开后退出；仅确保它不悬挂。
        }
    }

    public void Dispose() => ServerTrust.Configure(null);
}
