using System.IO;
using System.Text;

namespace Exam.Student.Core;

internal static class StudentCrashDiagnostics
{
    public static string? TryWrite(string dataDirectory, Exception error)
    {
        try
        {
            var directory = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.txt");
            var text = new StringBuilder()
                .AppendLine($"发生时间 {DateTimeOffset.Now:O}")
                .AppendLine($"客户端程序集时间 {File.GetLastWriteTimeUtc(typeof(StudentCrashDiagnostics).Assembly.Location):O}");
            // Stack traces identify the failing code without recording credentials, answers or request bodies.
            for (var current = error; current is not null; current = current.InnerException)
                text.AppendLine(current.GetType().FullName).AppendLine(current.StackTrace ?? "无调用堆栈");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch { return null; }
    }
}
