using System.Collections.ObjectModel;

namespace Exam.Admin.ViewModels;

public sealed class DependencyStatusViewModel
{
    public ObservableCollection<DependencyStatusRow> Items { get; } =
    [
        new(
            "C06 报表与导出入口",
            "正式结果未就绪",
            "依赖 C04 权威数据及 E02/E03 正式结果、排名和导出契约；当前不在 GUI 中自行计算成绩、排名、统计或伪造 Excel 导出。")
    ];
}

public sealed record DependencyStatusRow(string Module, string Status, string Reason);
