using System.Collections.ObjectModel;

namespace Exam.Admin.ViewModels;

public sealed class DependencyStatusViewModel
{
    public ObservableCollection<DependencyStatusRow> Items { get; } =
    [
        new("C05 导入与考试中修改", "待接入", "已有 D01 schema 和 A06 版本接口；编译导入、预览与规则发布的完整联调仍未完成。"),
        new("G03 成绩报表", "已接线，待审计", "成绩报表页读取权威结束、回收、当前计分状态，并使用同一快照查询及导出；A 审计及整体测试另行记录。"),
        new("C07 主备管理", "等待 A08 管理入口", "已有主备公共契约；考务端所需状态查询与接管管理 HTTP 接口尚未接通。")
    ];
}

public sealed record DependencyStatusRow(string Module, string Status, string Reason);
