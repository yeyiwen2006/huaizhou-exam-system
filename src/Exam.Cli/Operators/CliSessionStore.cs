using System.Security.Cryptography;
using System.Text;

/// <summary>
/// CLI 登录会话：用 Windows 当前用户加密保存在本机，之后的命令自动使用；logout 删除。
/// 业务服务会话与本机服务管理会话分开保存。环境变量 EXAM_OPERATOR_TOKEN 若已设置则优先使用。
/// </summary>
internal static class CliSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Huaizhou.Exam.Cli.Session.v1");

    public static string Directory => Environment.GetEnvironmentVariable("EXAM_CLI_SESSION_DIRECTORY") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuaizhouExam", "Cli");

    public static void Save(string kind, string token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CLI 登录会话只支持在 Windows 上保存。");
        System.IO.Directory.CreateDirectory(Directory);
        var plaintext = Encoding.UTF8.GetBytes(token);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            var path = PathOf(kind);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
        }
        finally { Array.Clear(plaintext); }
    }

    public static string? Load(string kind)
    {
        var path = PathOf(kind);
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        try
        {
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { Array.Clear(plaintext); }
        }
        catch (Exception error) when (error is CryptographicException or IOException)
        {
            return null;
        }
    }

    public static bool Delete(string kind)
    {
        var path = PathOf(kind);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static string PathOf(string kind) => Path.Combine(Directory, kind + ".session");
}
