using System.Windows.Controls;

namespace Exam.Desktop.Shared.Controls;

/// <summary>组件样例页（M1 验收 / C 评审用）。</summary>
public partial class ComponentShowcasePage : UserControl
{
    public ComponentShowcasePage()
    {
        InitializeComponent();
    }
}

/// <summary>样例表格行数据（仅样例页演示用）。</summary>
public sealed class SampleRow
{
    public string Rank { get; init; } = string.Empty;
    public string AdmissionNo { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ClassText { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
}
