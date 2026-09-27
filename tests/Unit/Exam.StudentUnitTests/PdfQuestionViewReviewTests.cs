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
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
        var expected = crop ? 120d / 220 : 160d / 300;
        if (rotation is 90 or 270) expected = 1 / expected;
        Assert.InRange((double)bitmap.PixelWidth / bitmap.PixelHeight, expected - .01, expected + .01);
        Assert.Equal((double)bitmap.PixelWidth / bitmap.PixelHeight, image.Width / image.Height, 5);
        var overlay = Assert.IsType<Canvas>(host.Children[1]);
        var button = Assert.IsType<ToggleButton>(Assert.Single(overlay.Children.Cast<UIElement>()));
        Assert.Equal(x * image.Width, Canvas.GetLeft(button) + button.Width / 2, 5);
        Assert.Equal(y * image.Height, Canvas.GetTop(button) + button.Height / 2, 5);
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var color = new byte[4];
        pixels.CopyPixels(new Int32Rect((int)(x * pixels.PixelWidth), (int)(y * pixels.PixelHeight), 1, 1), color, 4, 0);
        Assert.True(color[0] + color[1] + color[2] < 60, "圆心必须命中合成原 PDF 中的黑色标签，不是留白或错误页面坐标。");
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

    private static byte[] Pdf(int rotation, bool crop)
    {
        const string content = "0 0 0 rg 44 116 12 18 re f\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 160 300] /Rotate {rotation} " +
                (crop ? "/CropBox [20 40 140 260] " : "") + "/Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
