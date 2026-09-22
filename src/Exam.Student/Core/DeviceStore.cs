using System.IO;
using System.Text.Json;

namespace Exam.Student.Core;

/// <summary>本机激活设备信息（持久化表示）。</summary>
public sealed record DeviceRecord(
    string DeviceId,
    string Credential,
    string? ClientNumber,
    string Host,
    int Port,
    DateTimeOffset ActivatedAt);

/// <summary>
/// 设备身份持久化：%LOCALAPPDATA%\HuaizhouExam\Student\device.json。
/// 激活状态不依赖服务器在线；取消激活清除本记录。
/// TODO(B07)：真实凭据接入时改用 Windows DPAPI（当前用户级）加密存储，
/// 不以明文落盘（见执行指南"凭据与解锁恢复信息使用 Windows 当前用户的数据保护"）。
/// </summary>
public sealed class DeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public DeviceStore(string directory)
    {
        _directory = directory;
    }

    private string FilePath => Path.Combine(_directory, "device.json");

    public DeviceRecord? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<DeviceRecord>(File.ReadAllText(FilePath));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(DeviceRecord record)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(record, JsonOptions));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (IOException)
        {
            // 删除失败时下次启动仍按已激活处理；取消激活入口可重试
        }
    }
}
