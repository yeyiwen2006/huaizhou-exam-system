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
/// 显示前裁去页面四周空白（只按实际印刷内容的外框裁边，不裁任何内容；多页题左右裁边一致），
/// 题目在可视区域居中并默认按宽度放大，超过一屏时下拉。
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
    /// <summary>适应宽度时的上限：避免短题被放得过大（相对渲染位图）。</summary>
    private const double FitMaxZoom = 0.9;
    /// <summary>选项圆圈的屏幕直径范围（像素），不随页面缩放变化。</summary>
    private const double OptionMinDiameter = 30;
    private const double OptionMaxDiameter = 44;
    /// <summary>裁边后保留的余白（位图像素），保证圆圈不贴边。</summary>
    private const int CropPadding = 36;

    private readonly Dictionary<string, ToggleButton> _optionButtons = new(StringComparer.Ordinal);
    private readonly List<(ToggleButton Button, double CenterX, double CenterY, double Size)> _optionGeometry = new();
    private readonly List<Canvas> _pageOverlays = new();
    private ToggleButton? _abstainButton;
    private string? _questionId;
    private double _naturalWidth = 1;
    private double _zoom = 1;
    private bool _fitOnResize;
    private int _loadGeneration;
    private bool _interactionEnabled;
    private bool _regionsValidated;

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
        // 纵向滚动条出现或消失会改变可视宽度；适应宽度模式下随之重算，避免出现横向滚动条。
        PdfScroll.ScrollChanged += (_, e) => { if (_fitOnResize && e.ViewportWidthChange != 0) { ApplyFitWidth(); } };
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
        _interactionEnabled = interactionEnabled;
        // 隔离调用方随后修改字典；坐标记录本身为不可变值。
        regions = regions?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        RenderedPage[] rendered;
        try
        {
            rendered = await Task.Run(() => RenderPdfAsync(pdf, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _loadGeneration) ClearCore();
            throw;
        }
        catch (Exception)
        {
            rendered = [];
        }
        if (cancellationToken.IsCancellationRequested)
        {
            if (generation == _loadGeneration) ClearCore();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (generation != _loadGeneration) { return; }
        if (rendered.Length == 0)
        {
            ClearCore();
            RegionsInvalid?.Invoke("题目 PDF 无法渲染，原版显示不可用。");
            return;
        }

        // 契约 4.3：pageIndex 相对逐题 PDF 从 0 起；页码越界即整包失败。
        if (regions is not null)
        {
            var invalid = OptionRegionMap.ValidatePageCount(regions, rendered.Length, questionId);
            if (invalid is not null)
            {
                ClearCore();
                RegionsInvalid?.Invoke(invalid);
                return;
            }
        }

        _regionsValidated = regions is { Count: > 0 };
        var crops = CropRects(rendered, regions);
        for (var index = 0; index < rendered.Length; index++)
        {
            var page = rendered[index];
            var crop = crops[index];
            var source = new CroppedBitmap(page.Bitmap, crop);
            source.Freeze();
            var image = new Image
            {
                Source = source,
                Width = crop.Width,
                Height = crop.Height,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true,
            };
            var overlay = new Canvas { Width = crop.Width, Height = crop.Height };
            var host = new Grid { Width = crop.Width, Height = crop.Height, Margin = new Thickness(0, 0, 0, 12) };
            host.Children.Add(image);
            host.Children.Add(overlay);
            PagesHost.Children.Add(host);
            _pageOverlays.Add(overlay);
            _naturalWidth = Math.Max(_naturalWidth, crop.Width);
        }

        if (regions is not null)
        {
            // 圆心取归一化矩形中心（契约 4.3），换算到原页面位图后减去裁边偏移。
            // 圆圈尺寸按屏幕像素设定并抵消页面缩放，见 UpdateOptionGeometry。
            foreach (var (optionId, region) in regions)
            {
                if (region.PageIndex < 0 || region.PageIndex >= _pageOverlays.Count) { continue; }
                var page = rendered[region.PageIndex];
                var crop = crops[region.PageIndex];
                var button = new ToggleButton
                {
                    Style = (Style)Resources["OptionCircleStyle"],
                    IsEnabled = _interactionEnabled && _regionsValidated,
                    Tag = optionId,
                };
                button.Click += (_, _) => OptionClicked?.Invoke(optionId);
                _pageOverlays[region.PageIndex].Children.Add(button);
                _optionButtons[optionId] = button;
                _optionGeometry.Add((button, region.CenterX * page.Width - crop.X, region.CenterY * page.Height - crop.Y,
                    Math.Max(region.Width * page.Width, region.Height * page.Height)));
            }
        }

        if (allowAbstain)
        {
            _abstainButton = new ToggleButton
            {
                Style = (Style)Resources["AbstainButtonStyle"],
                IsEnabled = _interactionEnabled && _regionsValidated,
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
        _interactionEnabled = enabled;
        var effective = enabled && _regionsValidated;
        foreach (var button in _optionButtons.Values) { button.IsEnabled = effective; }
        if (_abstainButton is not null) { _abstainButton.IsEnabled = effective; }
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
        _optionGeometry.Clear();
        _abstainButton = null;
        _questionId = null;
        _interactionEnabled = false;
        _regionsValidated = false;
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
        // 首次加载时可能尚未布局，可用宽度退回控件自身宽度；按宽度铺满，长题允许下拉。
        var viewport = PdfScroll.ViewportWidth > 0 ? PdfScroll.ViewportWidth : ActualWidth;
        var available = Math.Max(1, viewport - 36);
        ApplyZoom(Math.Min(FitMaxZoom, available / _naturalWidth), keepOnResize: false);
    }

    private void ApplyZoom(double zoom, bool keepOnResize)
    {
        _fitOnResize = !keepOnResize;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomResetButton.Content = $"{(int)Math.Round(_zoom * 100)}%";
        UpdateOptionGeometry();
    }

    /// <summary>圆圈直径按屏幕像素取选项区域的 1.6 倍并限定在 30–44 像素，再以 1/zoom 抵消页面缩放；圆心不变。</summary>
    private void UpdateOptionGeometry()
    {
        foreach (var (button, centerX, centerY, size) in _optionGeometry)
        {
            var diameter = Math.Clamp(size * _zoom * 1.6, OptionMinDiameter, OptionMaxDiameter);
            button.Width = diameter;
            button.Height = diameter;
            button.RenderTransform = new ScaleTransform(1 / _zoom, 1 / _zoom);
            Canvas.SetLeft(button, centerX - diameter / 2);
            Canvas.SetTop(button, centerY - diameter / 2);
        }
        if (_abstainButton is not null)
            _abstainButton.LayoutTransform = new ScaleTransform(1 / _zoom, 1 / _zoom);
    }

    /// <summary>
    /// 按实际印刷内容的外框裁去四周空白：多页题取统一的左右边界，上下按页计算；
    /// 选项圆心始终留在裁剪范围内。空白页保持原尺寸。
    /// </summary>
    private static Int32Rect[] CropRects(RenderedPage[] pages, IReadOnlyDictionary<string, OptionRegion>? regions)
    {
        int left = int.MaxValue, right = int.MinValue;
        var vertical = new (int Top, int Bottom)?[pages.Length];
        for (var index = 0; index < pages.Length; index++)
        {
            var top = int.MaxValue;
            var bottom = int.MinValue;
            if (pages[index].Content is { } content)
            {
                left = Math.Min(left, content.X);
                right = Math.Max(right, content.X + content.Width);
                top = content.Y;
                bottom = content.Y + content.Height;
            }
            foreach (var region in regions?.Values ?? [])
            {
                if (region.PageIndex != index) continue;
                var x = (int)Math.Round(region.CenterX * pages[index].Width);
                var y = (int)Math.Round(region.CenterY * pages[index].Height);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
            if (top <= bottom) vertical[index] = (top, bottom);
        }
        var rects = new Int32Rect[pages.Length];
        for (var index = 0; index < pages.Length; index++)
        {
            var width = (int)pages[index].Width;
            var height = (int)pages[index].Height;
            if (left > right || vertical[index] is not { } span)
            {
                rects[index] = new Int32Rect(0, 0, width, height);
                continue;
            }
            var x0 = Math.Max(0, left - CropPadding);
            var x1 = Math.Min(width, right + CropPadding);
            var y0 = Math.Max(0, span.Top - CropPadding);
            var y1 = Math.Min(height, span.Bottom + CropPadding);
            rects[index] = x1 > x0 && y1 > y0
                ? new Int32Rect(x0, y0, x1 - x0, y1 - y0)
                : new Int32Rect(0, 0, width, height);
        }
        return rects;
    }

    /// <summary>页面位图中非白色像素的外框；整页空白时返回 null。</summary>
    private static Int32Rect? ContentBounds(BitmapSource bitmap)
    {
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = pixels.PixelWidth, height = pixels.PixelHeight, stride = width * 4;
        var data = new byte[stride * height];
        pixels.CopyPixels(data, stride, 0);
        int left = width, right = -1, top = height, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                // 透明或接近白色视为留白；阈值容许抗锯齿的浅灰边缘。
                if (data[i + 3] < 16 || Math.Min(data[i], Math.Min(data[i + 1], data[i + 2])) >= 240) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        Array.Clear(data);
        return right < 0 ? null : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private sealed record RenderedPage(BitmapSource Bitmap, double Width, double Height, Int32Rect? Content);

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
            // WinRT 的实际显示尺寸已经包含 CropBox 和 Rotate；MediaBox 不能作为叠加层尺寸。
            var size = page.Size;
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                throw new InvalidDataException("PDF 页面尺寸无效。");
            var scale = Math.Min(RenderPointsScale, 2200 / Math.Max(size.Width, size.Height));
            var width = Math.Max(1, Math.Round(size.Width * scale));
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
            try
            {
                var bitmap = CreateImage(bytes);
                // 整数像素舍入与 PNG DPI 不再造成图像/Canvas 比例差；两者统一使用实际渲染位图尺寸。
                pages[index] = new RenderedPage(bitmap, bitmap.PixelWidth, bitmap.PixelHeight, ContentBounds(bitmap));
            }
            finally { Array.Clear(bytes); }
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
