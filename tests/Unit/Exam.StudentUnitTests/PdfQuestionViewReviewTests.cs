using Exam.Contracts.Submissions;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Student.Core;
using Exam.Student.Views;

namespace Exam.StudentUnitTests;

public sealed class PdfQuestionViewReviewTests
{
    [Theory]
    [Trait("Category", "Wpf")]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public Task B03Review_RealControlPreservesVisibleCropRotationAndAnchorAlignment(int rotation, bool crop) => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var (x, y) = Anchor(rotation, crop);
        var regions = new Dictionary<string, OptionRegion> { ["A"] = new(0, x - .01, y - .01, .02, .02) };
        await view.LoadAsync("synthetic-q", Pdf(rotation, crop), regions, true, false);
        var pages = (StackPanel)view.FindName("PagesHost");
        var host = Assert.IsType<Grid>(Assert.Single(pages.Children.Cast<UIElement>()));
        var image = Assert.IsType<Image>(host.Children[0]);
        // 显示的是裁去四周空白后的位图；原页面位图仍须保留 PDF CropBox 与旋转后的宽高比。
        var shown = Assert.IsType<CroppedBitmap>(image.Source);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(shown.Source);
        var expected = crop ? 120d / 220 : 160d / 300;
        if (rotation is 90 or 270) expected = 1 / expected;
        Assert.InRange((double)bitmap.PixelWidth / bitmap.PixelHeight, expected - .01, expected + .01);
        Assert.Equal(shown.SourceRect.Width, image.Width, 5);
        Assert.Equal(shown.SourceRect.Height, image.Height, 5);
        Assert.True(image.Width < bitmap.PixelWidth || image.Height < bitmap.PixelHeight, "四周空白应被裁去。");
        var overlay = Assert.IsType<Canvas>(host.Children[1]);
        var button = Assert.IsType<ToggleButton>(Assert.Single(overlay.Children.Cast<UIElement>()));
        // 圆心加上裁边偏移，必须回到原页面上的同一归一化位置。
        Assert.Equal(x * bitmap.PixelWidth, Canvas.GetLeft(button) + button.Width / 2 + shown.SourceRect.X, 5);
        Assert.Equal(y * bitmap.PixelHeight, Canvas.GetTop(button) + button.Height / 2 + shown.SourceRect.Y, 5);
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var color = new byte[4];
        pixels.CopyPixels(new Int32Rect((int)(x * pixels.PixelWidth), (int)(y * pixels.PixelHeight), 1, 1), color, 4, 0);
        Assert.True(color[0] + color[1] + color[2] < 60, "圆心必须命中合成原 PDF 中的黑色标签，不是留白或错误页面坐标。");
        // InputHitTest 需要真实 PresentationSource；使用屏幕外合成窗口，不创建全局 Application。
        var window = new Window
        {
            Content = view, Width = 900, Height = 700, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Title = "B03 synthetic review"
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            ((Button)view.FindName("ZoomInButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            view.UpdateLayout();
            var zoom = Assert.IsType<ScaleTransform>(pages.LayoutTransform);
            Assert.Equal(zoom.ScaleX, zoom.ScaleY);
            Assert.InRange(zoom.ScaleX, .4, 3);
            var point = button.TransformToAncestor(view).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var hit = view.InputHitTest(point) as DependencyObject;
            while (hit is not null && hit is not ToggleButton) hit = VisualTreeHelper.GetParent(hit);
            Assert.Same(button, hit);
        }
        finally { window.Content = null; window.Close(); }
        view.ClearContent();
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task B03Review_DisableDuringRenderMustApplyToControlsCreatedLater() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var task = view.LoadAsync("synthetic-q", Pdf(0, false),
            new Dictionary<string, OptionRegion> { ["A"] = new(0, .2, .2, .1, .1) }, true, true);
        view.SetInteractionEnabled(false);
        await task;
        var pages = (StackPanel)view.FindName("PagesHost");
        var host = Assert.IsType<Grid>(pages.Children[0]);
        var option = Assert.IsType<ToggleButton>(Assert.Single(((Canvas)host.Children[1]).Children.Cast<UIElement>()));
        Assert.False(option.IsEnabled);
        Assert.False(Assert.IsType<ToggleButton>(pages.Children[1]).IsEnabled);
        view.ClearContent();
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task B03Review_InvalidPageCountMustNotCreatePartialAnswerControls() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var failures = 0;
        view.RegionsInvalid += _ => { failures++; view.SetInteractionEnabled(false); };
        await view.LoadAsync("synthetic-q", Pdf(0, false), new Dictionary<string, OptionRegion>
        {
            ["A"] = new(0, .2, .2, .1, .1), ["B"] = new(9, .2, .3, .1, .1)
        }, true, true);
        Assert.Equal(1, failures);
        Assert.Empty(((StackPanel)view.FindName("PagesHost")).Children);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task B03Review_ClearDuringRenderCannotResurrectTheQuestion() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var task = view.LoadAsync("synthetic-q", Pdf(0, false), null, false, false);
        view.ClearContent();
        await task;
        Assert.False(view.HasQuestion);
        Assert.Empty(((StackPanel)view.FindName("PagesHost")).Children);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task B03Review_MissingCoordinatesCannotEnableAbstainEvenAfterAStateRefresh() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        await view.LoadAsync("synthetic-q", Pdf(0, false), null, true, true);
        view.SetInteractionEnabled(true);
        var pages = (StackPanel)view.FindName("PagesHost");
        Assert.False(Assert.IsType<ToggleButton>(pages.Children[1]).IsEnabled);
        view.ClearContent();
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task B03Review_CancelledRenderLeavesNoQuestionOrPartialControls() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        using var cancellation = new CancellationTokenSource();
        var task = view.LoadAsync("synthetic-q", Pdf(0, false), null, false, false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(view.HasQuestion);
        Assert.Empty(((StackPanel)view.FindName("PagesHost")).Children);
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task B03Review_CallerMutationDuringRenderCannotChangeTheValidatedAnchorOrClickIdentity() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var regions = new Dictionary<string, OptionRegion> { ["A"] = new(0, .2, .2, .1, .1) };
        var task = view.LoadAsync("synthetic-q", Pdf(0, false), regions, true, false);
        regions.Clear();
        regions["untrusted"] = new(99, 0, 0, 1, 1);
        await task;
        var host = (Grid)((StackPanel)view.FindName("PagesHost")).Children[0];
        var overlay = (Canvas)host.Children[1];
        var button = Assert.IsType<ToggleButton>(Assert.Single(overlay.Children.Cast<UIElement>()));
        Assert.Equal("A", button.Tag);
        string? clicked = null;
        view.OptionClicked += value => clicked = value;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("A", clicked);
        view.ClearContent();
    });

    [Theory]
    [Trait("Category", "Wpf")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task B03Review_MultiPageScrollingAndZoomRetainTheSecondPagesOptionIdentity(int zoomSteps) => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var (x, y) = Anchor(0, true);
        await view.LoadAsync("synthetic-multipage", Pdf(0, true, 2, tall: true), new Dictionary<string, OptionRegion>
        {
            ["A"] = new(0, x - .01, y - .01, .02, .02),
            ["B"] = new(1, x - .01, y - .01, .02, .02)
        }, true, false);
        var pages = (StackPanel)view.FindName("PagesHost");
        Assert.Equal(2, pages.Children.Count);
        var second = (Grid)pages.Children[1];
        var button = Assert.IsType<ToggleButton>(Assert.Single(((Canvas)second.Children[1]).Children.Cast<UIElement>()));
        var window = new Window
        {
            Content = view, Width = 600, Height = 300, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Title = "B03 synthetic multipage review"
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            for (var i = 0; i < zoomSteps; i++)
                ((Button)view.FindName("ZoomInButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            view.UpdateLayout();
            button.BringIntoView();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            view.UpdateLayout();
            Assert.True(((ScrollViewer)view.FindName("PdfScroll")).VerticalOffset > 0);
            var point = button.TransformToAncestor(view).Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var hit = view.InputHitTest(point) as DependencyObject;
            while (hit is not null && hit is not ToggleButton) hit = VisualTreeHelper.GetParent(hit);
            Assert.Same(button, hit);
            string? clicked = null;
            view.OptionClicked += value => clicked = value;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("B", clicked);
            view.UpdateAnswer(AnswerValue.Selected("B"));
            Assert.True(button.IsChecked);
            var firstButton = (ToggleButton)((Canvas)((Grid)pages.Children[0]).Children[1]).Children[0];
            Assert.False(firstButton.IsChecked);
        }
        finally { window.Content = null; window.Close(); view.ClearContent(); }
    });

    [Fact]
    [Trait("Category", "Wpf")]
    public Task Display_OptionCircleKeepsReadableScreenSizeAndQuestionIsCentred() => OnStaAsync(async () =>
    {
        var view = new PdfQuestionView();
        var (x, y) = Anchor(0, false);
        await view.LoadAsync("synthetic-q", Pdf(0, false), new Dictionary<string, OptionRegion>
            { ["A"] = new(0, x - .01, y - .01, .02, .02) }, true, false);
        var window = new Window
        {
            Content = view, Width = 1000, Height = 700, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, Title = "synthetic display review"
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            ((Button)view.FindName("ZoomResetButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            view.UpdateLayout();
            var pages = (StackPanel)view.FindName("PagesHost");
            var scroll = (ScrollViewer)view.FindName("PdfScroll");
            var button = (ToggleButton)((Canvas)((Grid)pages.Children[0]).Children[1]).Children[0];
            foreach (var steps in new[] { 0, 3, 6 })
            {
                for (var i = 0; i < steps; i++)
                    ((Button)view.FindName(steps == 6 ? "ZoomInButton" : "ZoomOutButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                view.UpdateLayout();
                var box = button.TransformToAncestor(view).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                Assert.InRange(box.Width, 29, 45);
                Assert.InRange(box.Height, 29, 45);
            }
            ((Button)view.FindName("ZoomResetButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            view.UpdateLayout();
            // 裁边后的短题完整显示，不需要下拉，并在可视区域居中。
            Assert.Equal(0, scroll.ScrollableHeight, 1);
            var bounds = pages.TransformToAncestor(scroll).TransformBounds(new Rect(pages.RenderSize));
            Assert.Equal(scroll.ViewportWidth / 2, bounds.Left + bounds.Width / 2, 0);
            Assert.Equal(scroll.ViewportHeight / 2, bounds.Top + bounds.Height / 2, 0);
        }
        finally { window.Content = null; window.Close(); view.ClearContent(); }
    });

    private static async Task OnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke((Action)(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static (double X, double Y) Anchor(int rotation, bool crop)
    {
        var u = crop ? (50d - 20) / 120 : 50d / 160;
        var v = crop ? (125d - 40) / 220 : 125d / 300;
        return rotation switch { 90 => (v, u), 180 => (1 - u, v), 270 => (1 - v, 1 - u), _ => (u, 1 - v) };
    }

    private static byte[] Pdf(int rotation, bool crop, int pageCount = 1, bool tall = false)
    {
        // tall：在页面底部再画一块，使裁去空白后的页面仍保持较高，用于检验下拉。
        var content = "0 0 0 rg 44 116 12 18 re f\n" + (tall ? "0 0 0 rg 100 44 12 12 re f\n" : "");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + 2 * i} 0 R"))}] /Count {pageCount} >>"
        };
        for (var i = 0; i < pageCount; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 160 300] /Rotate {rotation} " +
                (crop ? "/CropBox [20 40 140 260] " : "") + $"/Resources << >> /Contents {4 + 2 * i} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
