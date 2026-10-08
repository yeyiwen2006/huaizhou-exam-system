using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Admin.Reporting;

namespace Exam.AdminUnitTests.Reporting;

[CollectionDefinition("Admin WPF", DisableParallelization = true)]
public sealed class AdminWpfCollection;

[Collection("Admin WPF")]
[Trait("Category", "Wpf")]
public sealed partial class ReportViewReviewTests
{
    [Fact]
    public async Task Review_ReportControlsCanRenderOnSuccessiveUiThreads()
    {
        for (var i = 0; i < 2; i++)
            await OnUiThread(() =>
            {
                var view = new ReportTableView { Table = SyntheticTable() };
                var window = WindowFor(view, 900, 550);
                try
                {
                    window.Show(); Drain(); window.UpdateLayout();
                    Assert.NotEmpty(Descendants<DataGridCell>(view));
                    Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "合成答案");
                }
                finally { window.Close(); }
            });
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    public async Task Review_LastColumnVisibleAndFooterFixedDuringScrolling(double scale) => await OnUiThread(() =>
    {
        var table = SyntheticTable();
        var view = new ReportTableView { Table = table };
        var window = WindowFor(view, 1366 / scale, 768 / scale);
        try
        {
            window.Show(); Drain(); window.UpdateLayout();
            var grid = (DataGrid)view.FindName("RowsGrid");
            var outer = (ScrollViewer)view.FindName("HorizontalScroller");
            var footer = (Grid)view.FindName("FooterGrid");
            var inner = Descendants<ScrollViewer>(grid).First();
            var bar = Descendants<ScrollBar>(grid).Single(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var last = Descendants<DataGridCell>(grid).First(c => c.Column == grid.Columns[^1]);
            var lastRight = last.TransformToAncestor(grid).Transform(new Point(last.ActualWidth, 0)).X;
            var barLeft = bar.TransformToAncestor(grid).Transform(new Point(0, 0)).X;
            foreach (var header in Descendants<DataGridColumnHeader>(grid).Where(h => h.Column is not null))
            {
                var label = Assert.Single(Descendants<TextBlock>(header));
                var unwrapped = new TextBlock
                {
                    Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize,
                    FontWeight = label.FontWeight, TextWrapping = TextWrapping.NoWrap
                };
                unwrapped.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.Equal(TextWrapping.NoWrap, label.TextWrapping);
                Assert.True(label.ActualWidth >= unwrapped.DesiredSize.Width - 0.5,
                    $"Header '{label.Text}' needs {unwrapped.DesiredSize.Width}, has {label.ActualWidth}.");
                Assert.Equal(grid.Columns[header.Column.DisplayIndex].ActualWidth,
                    footer.ColumnDefinitions[header.Column.DisplayIndex].ActualWidth, 2);
            }
            Assert.True(lastRight <= barLeft + 0.5, $"Last column ends at {lastRight}, but scrollbar starts at {barLeft}; scores are clipped.");
            Assert.True(outer.ScrollableWidth > 0);
            var before = footer.TransformToAncestor(view).Transform(new Point(0, 0)).Y;
            inner.ScrollToEnd(); outer.ScrollToRightEnd(); Drain(); window.UpdateLayout();
            Assert.Equal(before, footer.TransformToAncestor(view).Transform(new Point(0, 0)).Y, 2);
            Assert.Equal(outer.ScrollableWidth, outer.HorizontalOffset, 2);
            Assert.Contains(footer.Children.Cast<Border>(), child => Grid.GetColumnSpan(child) == 4);
            Assert.NotEmpty(grid.Items);
            var directory = Environment.GetEnvironmentVariable("EXAM_REPORT_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                var image = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale),
                    (int)Math.Ceiling(view.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                image.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, $"report-layout-{(int)(scale * 100)}.png"));
                encoder.Save(output);
            }
            view.Table = null;
            Drain(); window.UpdateLayout();
            Assert.Empty(grid.Items);
            Assert.Empty(grid.Columns);
            Assert.Empty(footer.Children);
            Assert.Equal(Visibility.Collapsed, outer.Visibility);
        }
        finally { window.Close(); }
    });

    private static ReportTable SyntheticTable()
    {
        var columns = Enumerable.Range(0, 40).Select(i => new ReportTableColumn(i < 4 ? new[] { "排名", "准考证号", "姓名", "班级" }[i]
            : i == 4 ? "成绩（满分 100）" : $"第 {i + 26} 题（满分 3）", 120)).ToArray();
        var rows = Enumerable.Range(0, 60).Select(row => new ReportTableRow(Enumerable.Range(0, 40)
            .Select(col => new ReportTableCell(col == 2 ? "合成答案" : col < 4 ? (row + 1).ToString() : "12.5", col >= 4 && row % 2 == 1)).ToArray())).ToArray();
        return new(ReportTableKind.QuestionScores, "合成报表（非真实考生）", "synthetic", columns, rows,
            Enumerable.Range(0, 40).Select(i => i < 4 ? string.Empty : "12.50").ToArray());
    }

    private static Window WindowFor(ReportTableView view, double width, double height)
    {
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml")
        });
        return new Window
        {
            Content = view, Width = width, Height = height, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
    }

    private static async Task OnUiThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
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
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
