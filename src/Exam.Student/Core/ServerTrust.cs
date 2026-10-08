using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Exam.Student.Core;

/// <summary>
/// 服务器信任策略：部署配置提供证书 SHA-256 指纹时，TLS 只信任与该指纹完全一致的
/// 服务器证书（校内自签证书无需在学生机上安装、无需管理员权限）；未提供指纹时保持
/// Windows 标准证书链校验。进程内单次配置，供 HTTP 与实时连接在 TLS 握手时读取。
/// </summary>
public static class ServerTrust
{
    private static string? _pinnedThumbprint;
    public static bool ValidateNodeCertificate(TrustedServerTopology topology, object? sender, X509Certificate? certificate)
    {
        if (sender is not SslStream stream || certificate is null) return false;
        string host;
        try { host = DeviceStore.NormalizeHost(stream.TargetHostName.Trim('[', ']')); }
        catch (ArgumentException) { return false; }
        var node = topology.Nodes.SingleOrDefault(node => DeviceStore.NormalizeHost(node.Host) == host);
        return node is not null && string.Equals(node.CertificateSha256,
            certificate.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前 pin 的证书 SHA-256 指纹（小写 64 位十六进制），未配置为 null。</summary>
    public static string? PinnedCertificateThumbprint => _pinnedThumbprint;

    /// <summary>
    /// 应用部署指纹。传入 null 或无效值表示清除 pin、回到标准链校验；
    /// 非空值经 ServerEndpointConfig.NormalizeThumbprint 规范化，无效返回 false 不改变现状。
    /// </summary>
    public static bool Configure(string? sha256Thumbprint)
    {
        if (string.IsNullOrWhiteSpace(sha256Thumbprint))
        {
            _pinnedThumbprint = null;
            return true;
        }

        var normalized = ServerEndpointConfig.NormalizeThumbprint(sha256Thumbprint);
        if (normalized is null)
        {
            return false;
        }

        _pinnedThumbprint = normalized;
        return true;
    }

    /// <summary>TLS 校验回调：配置了 pin 时要求指纹精确匹配；否则按标准链校验结果判定。</summary>
    public static bool ValidateCertificate(
        object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var pinned = _pinnedThumbprint;
        if (pinned is null)
        {
            return errors == SslPolicyErrors.None;
        }

        return certificate is not null && string.Equals(
            certificate.GetCertHashString(HashAlgorithmName.SHA256), pinned, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>供界面与诊断使用：当前是否处于指纹 pin 模式。</summary>
    public static bool IsPinned => _pinnedThumbprint is not null;
}
