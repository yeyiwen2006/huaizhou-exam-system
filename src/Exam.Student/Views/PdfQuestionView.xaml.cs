using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Exam.Student.Views;

/// <summary>
/// B03 原版题目显示：Windows.Data.Pdf 渲染逐题 PDF（保留多页），保持传入 LaTeX 的
/// 字体、公式、图片、表格与选项排列；仅整体等比缩放与滚动。
/// 选项字母处按 option-regions.json 的归一化矩形中心叠加圆形选择控件；
/// “不作答”位于原选项之后。控件状态由页面按 AnswerSession 快照驱动。
/// </summary>
public partial class PdfQuestionView : UserControl
{
    private const double MinZoom = 0.4;
    private const double MaxZoom = 3.0;
    private const double ZoomStep = 1.25;
    /// <summary>渲染基准：WinRT 页面尺寸单位为 DIP（1/96 英寸）×2（约 192 DPI），各题按其实际页面尺寸等比出图。</summary>
    private const double RenderPointsScale = 2.0;

    private readonly Dictionary<string, ToggleButton> _optionButtons = new(StringComparer.Ordinal);
    private readonly List<Canvas> _pageOverlays = new();
    private ToggleButton? _abstainButton;
    private string? _questionId;
    private double _naturalWidth = 1;
    private double _zoom = 1;
    private bool _fitOnResize;
    private int _loadGeneration;

    /// <summary>点击普通选项；参数为选项标识（圆钮按清单选项标识绑定，不按坐标顺序）。</summary>
    public event Action<string>? OptionClicked;

    /// <summary>点击“不作答”。</summary>
    public event Action? AbstainClicked;

    /// <summary>坐标与该题 PDF 页数不符、PDF 无法渲染等整包失败情形；参数为面向用户的错误说明。</summary>
    public event Action<string>? RegionsInvalid;

    public PdfQuestionView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { if (_fitOnResize) { ApplyFitWidth(); } };
    }

    public bool HasQuestion => _questionId is not null;

    /// <summary>
    /// 渲染一道题。pdf 为该题逐题 PDF 明文副本（调用方在返回后负责清零）；
    /// regions 为该题选项坐标（null 表示该包未提供坐标，仅原版显示、不叠加圆钮）。
    /// </summary>
    public async Task LoadAsync(string questionId, byte[] pdf,
        IReadOnlyDictionary<string, OptionRegion>? regions, bool interactionEnabled, bool allowAbstain,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentNullException.ThrowIfNull(pdf);

        var generation = ++_loadGeneration;
        ClearCore();
        _questionId = questionId;

        RenderedPage[] rendered;
        try
        {
            rendered = await Task.Run(() => RenderPdfAsync(pdf, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            rendered = [];
        }
        if (generation != _loadGeneration) { return; }
        if (rendered.Length == 0)
        {
            RegionsInvalid?.Invoke("题目 PDF 无法渲染，原版显示不可用。");
            return;
        }

        // 契约 4.3：pageIndex 相对逐题 PDF 从 0 起；页码越界即整包失败。
        if (regions is not null)
        {
            var invalid = OptionRegionMap.ValidatePageCount(regions, rendered.Length, questionId);
            if (invalid is not null)
            {
                RegionsInvalid?.Invoke(invalid);
            }
        }

        foreach (var page in rendered)
        {
            var image = new Image
            {
                Source = page.Bitmap,
                Width = page.Width,
                Height = page.Height,
                Stretch = Stretch.None,
                SnapsToDevicePixels = true,
            };
            var overlay = new Canvas { Width = page.Width, Height = page.Height };
            var host = new Grid { Width = page.Width, Height = page.Height, Margin = new Thickness(0, 0, 0, 12) };
            host.Children.Add(image);
            host.Children.Add(overlay);
            PagesHost.Children.Add(host);
            _pageOverlays.Add(overlay);
            _naturalWidth = Math.Max(_naturalWidth, page.Width);
        }

        if (regions is not null)
        {
            // 圆心默认取归一化矩形中心（契约 4.3）；控件随页面同比例缩放，命中范围 36px。
            foreach (var (optionId, region) in regions)
            {
                if (region.PageIndex < 0 || region.PageIndex >= _pageOverlays.Count) { continue; }
                var button = new ToggleButton
                {
                    Style = (Style)Resources["OptionCircleStyle"],
                    IsEnabled = interactionEnabled,
                    Tag = optionId,
                };
                button.Click += (_, _) => OptionClicked?.Invoke(optionId);
                var overlay = _pageOverlays[region.PageIndex];
                Canvas.SetLeft(button, region.CenterX * overlay.Width - button.Width / 2);
                Canvas.SetTop(button, region.CenterY * overlay.Height - button.Height / 2);
                overlay.Children.Add(button);
                _optionButtons[optionId] = button;
            }
        }

        if (allowAbstain)
        {
            _abstainButton = new ToggleButton
            {
                Style = (Style)Resources["AbstainButtonStyle"],
                IsEnabled = interactionEnabled,
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _abstainButton.Click += (_, _) => AbstainClicked?.Invoke();
            PagesHost.Children.Add(_abstainButton);
        }

        _fitOnResize = true;
        ApplyFitWidth();
    }

    /// <summary>按答案快照刷新圆钮与“不作答”状态；固定/冻结时由页面先禁用交互。</summary>
    public void UpdateAnswer(AnswerValue answer)
    {
        var selected = answer.Kind == AnswerKind.Selected
            ? answer.OptionIds.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var (optionId, button) in _optionButtons)
        {
            button.IsChecked = selected.Contains(optionId);
        }
        if (_abstainButton is not null)
        {
            _abstainButton.IsChecked = answer.Kind == AnswerKind.Abstain;
        }
    }

    public void SetInteractionEnabled(bool enabled)
    {
        foreach (var button in _optionButtons.Values) { button.IsEnabled = enabled; }
        if (_abstainButton is not null) { _abstainButton.IsEnabled = enabled; }
    }

    public void ClearContent()
    {
        _loadGeneration++;
        ClearCore();
    }

    private void ClearCore()
    {
        PagesHost.Children.Clear();
        _pageOverlays.Clear();
        _optionButtons.Clear();
        _abstainButton = null;
        _questionId = null;
        _naturalWidth = 1;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ApplyZoom(_zoom * ZoomStep, keepOnResize: true);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ApplyZoom(_zoom / ZoomStep, keepOnResize: true);

    private void OnZoomFit(object sender, RoutedEventArgs e) => ApplyFitWidth();

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers is not ModifierKeys.Control) { return; }
        ApplyZoom(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), keepOnResize: true);
        e.Handled = true;
    }

    private void ApplyFitWidth()
    {
        var available = Math.Max(1, PdfScroll.ViewportWidth - 36);
        ApplyZoom(available / _naturalWidth, keepOnResize: false);
    }

    private void ApplyZoom(double zoom, bool keepOnResize)
    {
        _fitOnResize = !keepOnResize;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomResetButton.Content = $"{(int)Math.Round(_zoom * 100)}%";
    }

    private sealed record RenderedPage(BitmapSource Bitmap, double Width, double Height);

    private static async Task<RenderedPage[]> RenderPdfAsync(byte[] pdf, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source))
        {
            writer.WriteBytes(pdf);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        source.Seek(0);

        var document = await PdfDocument.LoadFromStreamAsync(source);
        var pages = new RenderedPage[document.PageCount];
        for (var index = 0; index < document.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = document.GetPage((uint)index);
            var media = page.Dimensions.MediaBox;
            var width = Math.Clamp(media.Width * RenderPointsScale, 200, 2200);
            var height = width * (media.Height / Math.Max(1, media.Width));
            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Round(width),
            });
            var bytes = new byte[checked((int)output.Size)];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)bytes.Length);
                reader.ReadBytes(bytes);
            }
            pages[index] = new RenderedPage(CreateImage(bytes), width, height);
        }
        return pages;
    }

    private static BitmapSource CreateImage(byte[] pngBytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(pngBytes, writable: false);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
