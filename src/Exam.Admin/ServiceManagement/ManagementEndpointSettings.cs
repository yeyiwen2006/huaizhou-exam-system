using System.IO;
using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.Admin.ServiceManagement;

public sealed class ManagementEndpointSettings(string path)
{
    public ManagementTarget? Load()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8192 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("管理连接配置无效。");
        var target = JsonSerializer.Deserialize<ManagementTarget>(File.ReadAllText(path), JsonDefaults.Create())
            ?? throw new InvalidDataException("管理连接配置为空。");
        target.Validate();
        return target;
    }
    public void Save(ManagementTarget target)
    {
        target.Validate();
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        for (var node = new DirectoryInfo(parent); node is not null; node = node.Parent)
            if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("管理连接目录不能经过链接。");
        Directory.CreateDirectory(parent);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("管理连接配置不能是链接。");
        var temporary = Path.Combine(parent, ".endpoint-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(target, JsonDefaults.Create())); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
