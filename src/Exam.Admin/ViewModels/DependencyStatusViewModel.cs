using System.Collections.ObjectModel;

namespace Exam.Admin.ViewModels;

public sealed class DependencyStatusViewModel
{
    public ObservableCollection<DependencyStatusRow> Items { get; } =
    [
        new("C05 成绩查询", "等待 D05", "当前 Contracts/Server 没有正式成绩查询 API；C 不在考务端计算或伪造成绩。"),
        new("C06 报告导出", "等待 D05", "当前没有正式成绩报告/导出 API；C 不自行聚合统计结果。"),
        new("C07 双机热备", "等待 A05/E01", "当前可见契约没有服务器主备状态机与手动切换 API；C 只负责未来的状态展示与操作入口。")
    ];
}

public sealed record DependencyStatusRow(string Module, string Status, string Reason);
