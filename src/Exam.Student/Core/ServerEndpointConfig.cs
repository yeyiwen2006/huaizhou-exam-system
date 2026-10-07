using System.IO;
using System.Text.Json;

namespace Exam.Student.Core;

/// <summary>
/// 发布配置：部署时放在学生端可执行文件旁的 server.json，向全新设备提供
/// 可信服务器地址（与可选的服务器证书 SHA-256 指纹）。文件由监考/部署方放置，
/// 不随仓库内置真实地址或凭据；缺失或无效时界面给出明确提示，手动输入仍可用。
/// </summary>
public sealed record ServerEndpointConfig(string Host, int Port, string? CertificateSha256)
{
    /// <summary>部署配置文件名（位于应用基目录，即 exe 旁）。</summary>
    public const string FileName = "server.json";

    /// <summary>当前支持的配置格式版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>读取配置失败的原因（含文件名与具体字段），供界面提示使用。</summary>
    public sealed record LoadError(string Message);

    /// <summary>
    /// 从目录读取 server.json。文件不存在时返回 (null, null)（部署尚未放置，界面提示缺配置）；
    /// 文件存在但内容无效时返回 (null, error)。解析对未知字段宽松、对已知字段严格。
    /// </summary>
    public static (ServerEndpointConfig? Config, LoadError? Error) Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("必须提供配置所在目录。", nameof(directory));
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return (null, null);
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException error)
        {
            return (null, new LoadError($"无法读取 {FileName}：{error.Message}"));
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, new LoadError($"{FileName} 的内容必须是 JSON 对象。"));
            }

            if (!root.TryGetProperty("formatVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
            {
                return (null, new LoadError($"{FileName} 缺少有效的 formatVersion 数字字段。"));
            }

            if (version != CurrentFormatVersion)
            {
                return (null, new LoadError(
                    $"{FileName} 的 formatVersion 为 {version}，当前学生端只支持 {CurrentFormatVersion}。"));
            }

            if (!root.TryGetProperty("host", out var hostElement) || hostElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(hostElement.GetString()))
            {
                return (null, new LoadError($"{FileName} 缺少有效的 host 字符串字段。"));
            }

            var host = hostElement.GetString()!.Trim();
            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                return (null, new LoadError($"{FileName} 的 host 必须是服务器 IP 地址（与激活页输入要求一致）。"));
            }

            if (!root.TryGetProperty("port", out var portElement) || portElement.ValueKind != JsonValueKind.Number ||
                !portElement.TryGetInt32(out var port))
            {
                return (null, new LoadError($"{FileName} 缺少有效的 port 数字字段。"));
            }

            if (port is < 1 or > 65535)
            {
                return (null, new LoadError($"{FileName} 的 port 必须在 1–65535 之间。"));
            }

            string? thumbprint = null;
            if (root.TryGetProperty("certificateSha256", out var thumbprintElement))
            {
                if (thumbprintElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(thumbprintElement.GetString()))
                {
                    return (null, new LoadError($"{FileName} 的 certificateSha256 必须是十六进制指纹字符串。"));
                }

                thumbprint = NormalizeThumbprint(thumbprintElement.GetString()!);
                if (thumbprint is null)
                {
                    return (null, new LoadError(
                        $"{FileName} 的 certificateSha256 必须是 64 个十六进制字符的 SHA-256 指纹。"));
                }
            }

            return (new ServerEndpointConfig(host, port, thumbprint), null);
        }
        catch (JsonException error)
        {
            return (null, new LoadError($"{FileName} 不是有效的 JSON：{error.Message}"));
        }
    }

    /// <summary>
    /// 规范化证书 SHA-256 指纹：去掉空格与冒号后应为 64 个十六进制字符，输出统一小写；
    /// 无效输入返回 null。
    /// </summary>
    public static string? NormalizeThumbprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value.Replace(" ", string.Empty).Replace(":", string.Empty);
        if (compact.Length != 64)
        {
            return null;
        }

        foreach (var c in compact)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return null;
            }
        }

        return compact.ToLowerInvariant();
    }
}
