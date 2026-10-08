using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Exam.AdminUnitTests;

[Collection("Admin WPF")]
[Trait("Category", "Wpf")]
public sealed class MouseWheelRoutingTests
{
    [Theory]
    [InlineData(0, 0.8)]
    [InlineData(1, 0.8)]
    [InlineData(1, 0.3)]
    [InlineData(1, 0.1)]
    public Task ShortTable_BlankAreaRowAndHeader_ScrollThePage(int rowCount, double y) => Sta(() =>
    {
        var table = Table(rowCount);
        WithPage(table, page =>
        {
            var inner = Descendants<ScrollViewer>(table).Single();
            Assert.Equal(0, inner.ScrollableHeight);
            var source = Hit(table, 0.5, y);
            AssertScrollsBothWays(page, source);
            Assert.Equal(0, inner.VerticalOffset);
        });
    });

    [Theory]
    [InlineData("empty")]
    [InlineData("filled")]
    [InlineData("password")]
    public Task SingleLineInput_ScrollsThePage(string kind) => Sta(() =>
    {
        FrameworkElement input = kind == "password"
            ? new PasswordBox()
            : new TextBox { Text = kind == "empty" ? "" : "Synthetic input" };
        WithPage(input, page =>
        {
            foreach (var point in new[] { new Point(0.5, 0.5), new Point(0.05, 0.5), new Point(0.95, 0.5), new Point(0.5, 0.2), new Point(0.5, 0.8) })
                AssertScrollsBothWays(page, Hit(input, point.X, point.Y));
        });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LongTable_ScrollsItself_ThenChainsAtBothEdges(bool logicalScrolling) => Sta(() =>
    {
        var table = Table(60);
        VirtualizingPanel.SetScrollUnit(table, logicalScrolling ? ScrollUnit.Item : ScrollUnit.Pixel);
        WithPage(table, page =>
        {
            var inner = Descendants<ScrollViewer>(table).Single();
            Assert.True(inner.ScrollableHeight > 0);
            var pageOffset = page.VerticalOffset;
            Wheel(Hit(table), -120);
            Assert.True(inner.VerticalOffset > 0);
            Assert.Equal(pageOffset, page.VerticalOffset);

            inner.ScrollToBottom(); Drain();
            Wheel(Hit(table), -120);
            Assert.True(page.VerticalOffset > pageOffset);
            Assert.Equal(inner.ScrollableHeight, inner.VerticalOffset);

            page.ScrollToVerticalOffset(pageOffset); inner.ScrollToTop(); Drain();
            Wheel(Hit(table), 120);
            Assert.True(page.VerticalOffset < pageOffset);
            Assert.Equal(0, inner.VerticalOffset);
        });
    });

    [Fact]
    public Task NestedViewer_SkipsAnExhaustedMiddleViewer_WithoutDoubleScrolling() => Sta(() =>
    {
        var inner = new ScrollViewer
        {
            Height = 100, Content = new Border { Height = 400, Background = Brushes.White },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var middle = new ScrollViewer { Height = 180, Content = inner };
        WithPage(middle, page =>
        {
            Assert.Equal(0, middle.ScrollableHeight);
            inner.ScrollToBottom(); Drain();
            var start = page.VerticalOffset;
            Wheel(Hit(inner), -120);
            var chainedDistance = page.VerticalOffset - start;
            Assert.True(chainedDistance > 0);
            page.ScrollToVerticalOffset(start); Drain();
            Wheel(page, -120);
            Assert.Equal(chainedDistance, page.VerticalOffset - start);
        });
    });

    [Fact]
    public Task NestedViewer_UsesTheMiddleViewerWhileItCanScroll() => Sta(() =>
    {
        var inner = new ScrollViewer
        {
            Height = 100, Content = new Border { Height = 400, Background = Brushes.White },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var body = new StackPanel();
        body.Children.Add(inner);
        body.Children.Add(new Border { Height = 600 });
        var middle = new ScrollViewer { Height = 180, Content = body };
        WithPage(middle, page =>
        {
            inner.ScrollToBottom(); Drain();
            var pageOffset = page.VerticalOffset;
            Wheel(Hit(inner), -120);
            Assert.True(middle.VerticalOffset > 0);
            Assert.Equal(pageOffset, page.VerticalOffset);
            Assert.Equal(inner.ScrollableHeight, inner.VerticalOffset);
        });
    });

    [Fact]
    public Task MultilineInput_ScrollsItsTextBeforeThePage() => Sta(() =>
    {
        var input = new TextBox
        {
            Height = 120, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join("\n", Enumerable.Repeat("Synthetic line", 60))
        };
        WithPage(input, page =>
        {
            var inner = Descendants<ScrollViewer>(input).Single();
            Assert.True(inner.ScrollableHeight > 0);
            var start = page.VerticalOffset;
            Wheel(Hit(input), -120);
            Assert.True(inner.VerticalOffset > 0);
            Assert.Equal(start, page.VerticalOffset);
            inner.ScrollToBottom(); Drain();
            Wheel(Hit(input), -120);
            Assert.True(page.VerticalOffset > start);
        });
    });

    [Fact]
    public Task AlreadyHandledAndZeroDelta_DoNotMoveThePage() => Sta(() =>
    {
        var table = Table(0);
        WithPage(table, page =>
        {
            var start = page.VerticalOffset;
            Wheel(Hit(table), -120, handled: true);
            Assert.Equal(start, page.VerticalOffset);
            Wheel(Hit(table), 0);
            Assert.Equal(start, page.VerticalOffset);
        });
    });

    private static DataGrid Table(int count)
    {
        var table = new DataGrid
        {
            Height = 180,
            ItemsSource = Enumerable.Range(0, count).Select(i => new SyntheticRow($"Synthetic row {i}")),
            IsReadOnly = true, CanUserAddRows = false, AutoGenerateColumns = false
        };
        table.Columns.Add(new DataGridTextColumn { Header = "Synthetic column", Binding = new Binding("Label"), Width = 420 });
        return table;
    }

    private static void WithPage(FrameworkElement child, Action<ScrollViewer> assertion)
    {
        var body = new StackPanel();
        body.Children.Add(new Border { Height = 200, Background = Brushes.White });
        body.Children.Add(child);
        body.Children.Add(new Border { Height = 1200, Background = Brushes.White });
        var page = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // A separate native source remains usable even after the App startup smoke test
        // has shut down WPF's process-wide Application. Do not depend on test class order.
        using var surface = new WpfTestSurface(page, 600, 500);
        Drain(); surface.UpdateLayout();
        page.ScrollToVerticalOffset(100); Drain();
        Assert.True(page.ScrollableHeight > page.VerticalOffset);
        assertion(page);
    }

    internal static void AssertScrollsBothWays(ScrollViewer page, UIElement source)
    {
        for (var index = 0; index < 3; index++)
        {
            var start = page.VerticalOffset;
            Wheel(source, -120);
            Assert.True(page.VerticalOffset > start, "Wheel over content must scroll the surrounding page down.");
            var down = page.VerticalOffset;
            Wheel(source, 120);
            Assert.True(page.VerticalOffset < down, "Wheel over content must scroll the surrounding page up.");
        }
    }

    internal static UIElement Hit(FrameworkElement element, double x = 0.5, double y = 0.5)
    {
        var hit = element.InputHitTest(new Point(element.ActualWidth * x, element.ActualHeight * y));
        return Assert.IsAssignableFrom<UIElement>(hit);
    }

    private static void Wheel(UIElement source, int delta, bool handled = false)
    {
        // WPF promotes unhandled preview input to a bubbling event using the same event data.
        // Hit-tested content is the source; calling ScrollTo* here would miss the regression.
        var input = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent, Handled = handled
        };
        source.RaiseEvent(input);
        if (!input.Handled)
        {
            input.RoutedEvent = Mouse.MouseWheelEvent;
            source.RaiseEvent(input);
        }
        Drain();
    }

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
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
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

    private sealed record SyntheticRow(string Label);
}
