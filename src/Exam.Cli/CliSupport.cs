using System.Net;

internal static partial class ExamCli
{
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
