using System.Collections.ObjectModel;

namespace Exam.Admin.ViewModels;

public sealed class DependencyStatusViewModel
{
    public ObservableCollection<DependencyStatusRow> Items { get; } =
    [
        new("C05 导入与考试中修改", "等待 D02-D05/A06", "A06 已有版本与时间 API；逐题导入、预览、内容修订和安全发布链路尚无完整正式 API。"),
        new("C06 报告与服务", "等待 D05/E", "当前没有正式五类成绩报告 API；服务启动停止也缺共用业务入口。"),
        new("C07 双机热备", "等待 A/E", "主备 Contract 和核心服务已存在，但 Server 尚未开放考务状态查询、隔离确认和切换 API。")
    ];
}

public sealed record DependencyStatusRow(string Module, string Status, string Reason);
