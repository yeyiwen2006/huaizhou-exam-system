using System.IO;
using System.Net.Http;
using Exam.Contracts.Common;

namespace Exam.Admin.Services;

/// <summary>
/// 把服务器错误码、网络异常和文件异常转成考务人员能看懂的一句话。
/// 原始技术信息不直接显示；未知错误码只在服务器给出中文说明时转述，否则给出通用提示。
/// </summary>
public static class FriendlyErrors
{
    public static string Describe(Exception error) => error switch
    {
        AdminApiException api => Describe(api.Code, api.Message),
        HttpRequestException => "无法连接服务器，请检查网络和服务器地址后重试。",
        TaskCanceledException or TimeoutException => "服务器响应超时，请稍后重试。",
        UnauthorizedAccessException => "没有权限访问该文件或位置，请换一个位置后重试。",
        IOException => "文件读写失败，请确认文件没有被其他程序占用后重试。",
        _ when IsReadableChinese(error.Message) => error.Message,
        _ => "操作未完成，请重试；如仍未完成，请联系系统管理员。",
    };

    public static string Describe(string code, string? serverMessage) => code switch
    {
        ErrorCodes.VersionConflict => "数据已被其他人修改，请重新读取后再保存。",
        ErrorCodes.NotFound or "EXAM_NOT_FOUND" => "没有找到对应的考试或记录，请核对后重试。",
        ErrorCodes.Forbidden or "UNAUTHORIZED" or "OPERATOR_AUTH_FAILED" => "没有操作权限或登录已失效，请重新登录后再试。",
        ErrorCodes.NotActiveServer or ErrorCodes.StaleServerEpoch or "STALE_SERVER_IDENTITY" or "INVALID_SERVER_IDENTITY"
            => "当前连接的不是正在提供服务的服务器，请核对服务器地址。",
        ErrorCodes.ServiceRecovering or "SERVICE_NOT_READY" => "服务器正在启动或恢复，请稍后再试。",
        ErrorCodes.ReplicaUnavailable => "备用服务器暂时没有确认，操作结果未生效，请稍后重试。",
        ErrorCodes.PersistenceFailed => "服务器暂时无法保存，请稍后重试。",
        ErrorCodes.ReportNotReady => "成绩尚未生成，请等待考试结束、答卷收齐并完成计分。",
        "CONNECTION_CHANGED" => "连接已更换，请在当前服务器上重新查看操作结果。",
        _ when IsReadableChinese(serverMessage) => serverMessage!,
        _ => "服务器未完成本次操作，请重试。",
    };

    // 只转述以中文为主、不含明显技术标记的说明；英文异常文本不直接给考务人员看。
    private static bool IsReadableChinese(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var chinese = message.Count(character => character is >= '一' and <= '鿿');
        return chinese >= message.Length / 3 && !message.Contains("HTTP", StringComparison.Ordinal) &&
            !message.Contains("API", StringComparison.Ordinal) && !message.Contains("Exception", StringComparison.Ordinal);
    }
}
