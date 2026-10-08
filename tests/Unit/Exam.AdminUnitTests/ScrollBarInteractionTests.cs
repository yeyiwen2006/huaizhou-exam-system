using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Exam.AdminUnitTests;

[Collection("Admin WPF")]
[Trait("Category", "Wpf")]
public sealed class ScrollBarInteractionTests
{
    [Theory]
    [InlineData(Orientation.Vertical)]
    [InlineData(Orientation.Horizontal)]
    public async Task ThumbEdgesCanStartDragging_AndDragMovesContentInBothDirections(Orientation orientation) => await Sta(() =>
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Height = 20000, Width = 20000, Background = Brushes.White }
        };
        var window = new Window
        {
            Content = scroll, Width = 500, Height = 350, Left = -5000, Top = -5000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml")
        });
        try
        {
            window.Show(); Drain(); window.UpdateLayout();
            var bar = Descendants<ScrollBar>(scroll).Single(item => item.Orientation == orientation && item.IsVisible);
            var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
            var thumb = track.Thumb;
            foreach (var point in new[]
            {
                new Point(0.5, thumb.ActualHeight / 2),
                new Point(thumb.ActualWidth - 0.5, thumb.ActualHeight / 2),
                new Point(thumb.ActualWidth / 2, 0.5),
                new Point(thumb.ActualWidth / 2, thumb.ActualHeight - 0.5)
            })
            {
                var hit = scroll.InputHitTest(thumb.TransformToAncestor(scroll).Transform(point)) as DependencyObject;
                Assert.True(hit is not null && (ReferenceEquals(hit, thumb) || thumb.IsAncestorOf(hit)),
                    $"The {orientation} thumb edge {point} does not accept a drag.");
            }
            Assert.True((orientation == Orientation.Vertical ? thumb.ActualHeight : thumb.ActualWidth) >= 24);
            Assert.True((orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight) >=
                (orientation == Orientation.Vertical ? SystemParameters.VerticalScrollBarWidth : SystemParameters.HorizontalScrollBarHeight));

            thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
            thumb.RaiseEvent(new DragDeltaEventArgs(orientation == Orientation.Horizontal ? 40 : 0,
                orientation == Orientation.Vertical ? 40 : 0));
            Drain(); window.UpdateLayout();
            var forward = orientation == Orientation.Vertical ? scroll.VerticalOffset : scroll.HorizontalOffset;
            Assert.True(forward > 0);
            Assert.Same(thumb, track.Thumb);
            thumb.RaiseEvent(new DragDeltaEventArgs(orientation == Orientation.Horizontal ? -20 : 0,
                orientation == Orientation.Vertical ? -20 : 0));
            Drain(); window.UpdateLayout();
            Assert.True((orientation == Orientation.Vertical ? scroll.VerticalOffset : scroll.HorizontalOffset) < forward);
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        }
        finally { window.Close(); }
    });

    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
