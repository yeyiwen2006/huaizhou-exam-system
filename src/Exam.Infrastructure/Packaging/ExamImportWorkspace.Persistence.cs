using System.Security.AccessControl;
using System.Security.Principal;

namespace Exam.Infrastructure.Packaging;

public sealed partial class ExamImportWorkspace
{
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("材料路径经过文件链接或目录联接，已拒绝读取。");
    }

    internal static void RestrictAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static ExamImportWorkspace OpenPersistent(string path)
    {
        var full = Path.GetFullPath(path);
        RejectLinks(full);
        if (!Directory.Exists(full) || !File.Exists(Path.Combine(full, "preview.json")))
            throw new InvalidDataException("没有找到已保存的导入预览，请先生成预览。");
        // One CLI process owns a saved preview until it finishes or is cancelled. A second process
        // cannot replace the ciphertext or replay journal while the first request is in flight.
        var lockPath = Path.Combine(full, "publication.lock");
        RejectLinks(lockPath);
        return new ExamImportWorkspace(full)
        {
            Persistent = true,
            _persistentLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
        };
    }
}
