using Exam.Desktop.Shared.Controls;

namespace Exam.Student.Views;

/// <summary>
/// 学生端界面提示统一用通俗说法；运行时、网络或服务器返回的原始技术信息只放在提示框的悬停说明里，
/// 供监考人员排查，不直接显示给考生。
/// </summary>
internal static class StudentMessages
{
    public const string ConnectionProblem = "与服务器的连接异常，已作答内容已保存在本机。请联系监考人员。";
    public const string AskProctor = "请联系监考人员。";

    /// <summary>显示通俗提示，并把原始信息放进悬停说明。</summary>
    public static void ShowWithDetail(Banner banner, string message, string? detail)
    {
        banner.Show(message);
        banner.ToolTip = string.IsNullOrWhiteSpace(detail) || detail == message ? null : detail;
    }
}
