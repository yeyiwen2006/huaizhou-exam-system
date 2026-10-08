using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Exam.Desktop.Shared.Controls;

/// <summary>Lets the nearest viewer that can still scroll handle a nested mouse wheel event.</summary>
public static class MouseWheelScrollChaining
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MouseWheelScrollChaining), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ScrollViewer viewer) return;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        else viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;

        ScrollViewer? nearest = null;
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = Parent(current))
        {
            if (current is not ScrollViewer viewer) continue;
            nearest ??= viewer;
            if (!CanScroll(viewer, e.Delta)) continue;

            // Leave native scrolling alone while the innermost viewer has room. WPF otherwise
            // consumes the wheel even for empty tables, input hosts and viewers at their edge.
            if (ReferenceEquals(viewer, nearest)) return;

            e.Handled = true;
            // Forward once to the ancestor's native handler, preserving system wheel settings
            // and IScrollInfo item/pixel scrolling instead of inventing a fixed pixel distance.
            viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent
            });
            return;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        viewer.IsEnabled && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
        (delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight : viewer.VerticalOffset > 0);

    private static DependencyObject? Parent(DependencyObject element) => element switch
    {
        // Stop at a popup's visual root; its wheel must not scroll the page behind the popup.
        Visual or Visual3D => VisualTreeHelper.GetParent(element),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        _ => null
    };
}
