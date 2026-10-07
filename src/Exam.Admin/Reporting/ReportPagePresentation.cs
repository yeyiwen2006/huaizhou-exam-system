using System.Globalization;
using Exam.Contracts.Reporting;

namespace Exam.Admin.Reporting;

// Display-only projection of the existing authoritative status. No score/rank is recomputed here.
public sealed partial class ReportPageViewModel
{
    public bool IsBusy => _busy;
    private string _examName = string.Empty;

    /// <summary>当前考试名称，仅用于显示；考试由主页进入时设定。</summary>
    public string ExamName { get => _examName; set => SetProperty(ref _examName, value ?? string.Empty); }
    public string ParticipantCountText => Count(_observedStatus?.ParticipantCount);
    public string CollectedCountText => Count(_observedStatus?.CollectedCount);
    public string GradedCountText => Count(_observedStatus?.GradedCount);
    public string GradingVersionText => _observedStatus is { GradingVersion: > 0 } status
        ? "第 " + status.GradingVersion.ToString(CultureInfo.InvariantCulture) + " 版" : "—";
    public string StateLabel => !_connected ? "未连接服务器" : _busy ? "操作进行中" :
        Session.HasTables ? "成绩已生成" : _observedStatus?.State switch
        {
            ReportState.ExamNotEnded => "考试尚未结束",
            ReportState.NoParticipants => "暂无参加考试记录",
            ReportState.Collecting => "等待答卷回收",
            ReportState.Grading => "等待计分完成",
            ReportState.InvalidSource => "数据需要核对",
            _ => "等待加载成绩"
        };

    private static string Count(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "—";
    private void NotifyPresentation()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(ParticipantCountText), nameof(CollectedCountText),
            nameof(GradedCountText), nameof(GradingVersionText), nameof(StateLabel) }) OnPropertyChanged(name);
    }
}
