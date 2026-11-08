using System.Net;
using System.Net.Sockets;

internal static partial class ExamCli
{
    // Match the admin GUI: Radmin's 26.* virtual LAN must connect directly;
    // public endpoints keep the system proxy and the standard TLS validation.
    internal static HttpClientHandler CreateBusinessHandler(Uri address) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = !(IPAddress.TryParse(address.Host, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] == 26)
    };

    internal static Task<string> ReadStandardInputAsync(CancellationToken cancellationToken) =>
        Task.Run(Console.In.ReadToEnd, cancellationToken).WaitAsync(cancellationToken);

    /// <summary>
    /// 解析并校验目标服务器地址：只接受 HTTPS，或在回环地址且显式允许明文时才接受 HTTP。
    /// 业务命令共用，避免各自实现出不一样的放宽条件。
    /// </summary>
    private static bool TryResolveServer(string? server, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var parsed) ||
            parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 ||
            parsed.Scheme != "https" &&
            !(parsed.Scheme == "http" && IPAddress.TryParse(parsed.Host, out var ip) &&
              IPAddress.IsLoopback(ip) && Environment.GetEnvironmentVariable(InsecureLoopbackEnvironment) == "true"))
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
