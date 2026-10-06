using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 监测页的显示映射。把服务端权威取值翻译为考务可读文案，界面不直接展示内部标识；
/// 未知取值一律显示为明确的“未知”而不是原样透出。
/// </summary>
internal static class MonitoringDisplay
{
    /// <summary>业务状态：与连接状态分开显示，离线不改变业务状态。</summary>
    public static string BusinessState(ParticipationBusinessState state) => state switch
    {
        ParticipationBusinessState.Waiting => "候考",
        ParticipationBusinessState.Answering => "作答中",
        ParticipationBusinessState.Submitted => "已交卷",
        ParticipationBusinessState.NotParticipating => "未参加",
        _ => "未知状态"
    };

    /// <summary>连接状态：没有有效通信记录时按需求明确标识“未登录”。</summary>
    public static string ConnectionState(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Online => "在线",
        DeviceConnectionState.Offline => "离线",
        DeviceConnectionState.Recovering => "恢复中",
        _ => "未登录"
    };

    public static string CommandType(CommandEventType type) => type switch
    {
        CommandEventType.Start => "开始答题",
        CommandEventType.End => "结束答题",
        CommandEventType.ScheduleChanged => "时间调整",
        CommandEventType.PaperUpdated => "试题修订",
        CommandEventType.DeviceDecided => "设备裁决",
        CommandEventType.CollectionCompleted => "答卷收齐",
        _ => "其他指令"
    };

    /// <summary>当前题号及其已保存答案；没有上报题号时不冒充有进度。</summary>
    public static string CurrentAnswer(string? questionId, IReadOnlyDictionary<string, AnswerValue> answers, int? questionNo,
        IReadOnlyList<string>? optionIds = null)
    {
        if (string.IsNullOrWhiteSpace(questionId)) return "—";
        var label = questionNo is > 0 ? $"第 {questionNo} 题" : "当前题（题号未提供）";
        if (!answers.TryGetValue(questionId, out var value)) return $"{label}：未保存";
        var selected = value.Kind switch
        {
            AnswerKind.Selected => SelectedOptions(value.OptionIds, optionIds),
            AnswerKind.Abstain => "不作答",
            _ => "未作答"
        };
        return $"{label}：{selected}";
    }

    private static string SelectedOptions(IReadOnlyList<string> selected, IReadOnlyList<string>? options)
    {
        if (selected.Count == 0) return "—";
        var labels = selected.Select(id => OptionLabel(id, options)).ToArray();
        return labels.All(label => label.Length == 1) ? string.Concat(labels) : string.Join("、", labels);
    }

    private static string OptionLabel(string id, IReadOnlyList<string>? options)
    {
        if (id.Length == 1 && id[0] is >= 'A' and <= 'Z') return id;
        if (options is not null)
            for (var index = 0; index < options.Count; index++)
                if (options[index] == id) return $"第 {index + 1} 个选项";
        return "选项待核对";
    }

    /// <summary>试卷版本与更新结果：实际版本与目标版本不一致时同时显示，便于辨认更新失败。</summary>
    public static string PaperState(long installedVersion, long targetVersion, string downloadState)
    {
        var download = string.IsNullOrWhiteSpace(downloadState) ? "未报告" : downloadState;
        return installedVersion == targetVersion
            ? $"第 {targetVersion} 版（{download}）"
            : $"第 {installedVersion} 版 → 第 {targetVersion} 版（{download}）";
    }

    /// <summary>指令与确认情况：只显示可理解的指令名称和确认结果，不显示事件标识。</summary>
    public static string CommandSummary(IReadOnlyList<MonitoringCommand> commands)
    {
        if (commands.Count == 0) return "暂无指令";
        return string.Join("；", commands.Select(command =>
            command.AcknowledgedAt is { } acknowledged
                ? $"{CommandType(command.EventType)}：{command.Confirmation}（{acknowledged.ToOffset(TimeSpan.FromHours(8)):HH:mm:ss}）"
                : $"{CommandType(command.EventType)}：{command.Confirmation}"));
    }

    public static string LastCommunication(DateTimeOffset? lastCommunicationAt) =>
        lastCommunicationAt is { } at ? at.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss") : "无通信记录";

    public static string ReceivedAt(DateTimeOffset? receivedAt) =>
        receivedAt is { } at ? at.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss") : "—";

    /// <summary>
    /// 失焦汇总。用整型秒数换算而不是 <see cref="TimeSpan"/>：设备上报的时长只校验过非负，
    /// 极大值会让 TimeSpan 溢出并把整页拖成失败。
    /// </summary>
    public static string FocusSummary(int count, long milliseconds)
    {
        if (count <= 0) return "暂无记录";
        var totalSeconds = milliseconds / 1000;
        var text = totalSeconds >= 3600
            ? $"{totalSeconds / 3600} 时 {totalSeconds % 3600 / 60} 分 {totalSeconds % 60} 秒"
            : totalSeconds >= 60
                ? $"{totalSeconds / 60} 分 {totalSeconds % 60} 秒"
                : $"{totalSeconds} 秒";
        return $"{count} 次 / 共 {text}";
    }

    /// <summary>修订复核状态；考生尚未进入修订流程时显示“—”。</summary>
    public static string PaperReviewState(Exam.Contracts.Versioning.ParticipantVersionStatus? revision) =>
        revision?.PaperUpdateState switch
        {
            Exam.Contracts.Versioning.PaperUpdateState.ReviewRequired => "未复核",
            Exam.Contracts.Versioning.PaperUpdateState.Updated => "已复核",
            Exam.Contracts.Versioning.PaperUpdateState.DownloadFailed => "修订未安装",
            _ => "—"
        };
}
