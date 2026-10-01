using System.Windows;

namespace Exam.Desktop.Shared.Controls;

/// <summary>
/// 侧栏导航外壳（Nav.TabControl）的附加插槽：TopBar 放在内容区顶部，
/// 用于连接状态等跨页面常驻内容；切换导航项时保持不变。
/// </summary>
public static class NavShell
{
    public static readonly DependencyProperty TopBarProperty =
        DependencyProperty.RegisterAttached("TopBar", typeof(object), typeof(NavShell),
            new FrameworkPropertyMetadata(null));

    public static object? GetTopBar(DependencyObject element) => element.GetValue(TopBarProperty);

    public static void SetTopBar(DependencyObject element, object? value) => element.SetValue(TopBarProperty, value);
}
