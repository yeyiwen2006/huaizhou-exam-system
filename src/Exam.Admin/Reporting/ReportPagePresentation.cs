using System.Globalization;
using Exam.Contracts.Reporting;

namespace Exam.Admin.Reporting;

// Display-only projection of the existing authoritative status. No score/rank is recomputed here.
public sealed partial class ReportPageViewModel
{
    public bool IsBusy => _busy;
    public string ParticipantCountText => Count(_observedStatus?.ParticipantCount);
    public string CollectedCountText => Count(_observedStatus?.CollectedCount);
    public string GradedCountText => Count(_observedStatus?.GradedCount);
    public string GradingVersionText => _observedStatus is { GradingVersion: > 0 } status
        ? "V" + status.GradingVersion.ToString(CultureInfo.InvariantCulture) : "—";
    public string StateLabel => !_connected ? "未连接服务器" : _busy ? "操作进行中" :
        Session.HasTables ? "报表已开放" : _observedStatus?.State switch
        {
            ReportState.ExamNotEnded => "考试尚未结束",
            ReportState.NoParticipants => "暂无参试记录",
            ReportState.Collecting => "等待答卷回收",
            ReportState.Grading => "等待计分完成",
            ReportState.InvalidSource => "数据需要核对",
            _ => "等待加载报表"
        };

    private static string Count(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "—";
    private void NotifyPresentation()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(ParticipantCountText), nameof(CollectedCountText),
            nameof(GradedCountText), nameof(GradingVersionText), nameof(StateLabel) }) OnPropertyChanged(name);
    }
}
