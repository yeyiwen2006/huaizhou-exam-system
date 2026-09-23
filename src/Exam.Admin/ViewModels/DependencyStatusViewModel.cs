using System.Collections.ObjectModel;

namespace Exam.Admin.ViewModels;

public sealed class DependencyStatusViewModel
{
    public ObservableCollection<DependencyStatusRow> Items { get; } =
    [
        new("C05 导入与考试中修改", "版本可查询", "已接入 A06 正式版本和逐客户端更新状态；编译导入、预览与规则修订仍缺完整服务接口。"),
        new("C06 报表与服务页面", "等待 A/D/E 接口", "完整成绩报表和导出 HTTP 接口尚未提供；不在 GUI 中另算排名或伪造导出结果。"),
        new("C07 主备管理", "等待 A08 管理入口", "已有主备公共契约；考务端所需状态查询与接管管理 HTTP 接口尚未接通。")
    ];
}

public sealed record DependencyStatusRow(string Module, string Status, string Reason);
