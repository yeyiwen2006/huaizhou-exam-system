using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Exam.Student.Views;

/// <summary>
/// 原版题目显示：Windows.Data.Pdf 渲染逐题 PDF（保留多页），保持传入 LaTeX 的
/// 字体、公式、图片、表格与选项排列，以 PDF 原始尺寸为基准固定放大到 125%，允许滚动。
/// 上下按内容裁边；左右固定、对称裁去 20 毫米空白，多页题采用一致规则。
/// 选项字母处按 option-regions.json 的归一化矩形中心叠加圆形选择控件；
/// “不作答”位于原选项之后。控件状态由页面按 AnswerSession 快照驱动。
/// </summary>
public partial class PdfQuestionView : UserControl
{
    /// <summary>渲染基准：WinRT 页面尺寸单位为 DIP（1/96 英寸）×2（约 192 DPI），各题按其实际页面尺寸等比出图。</summary>
    private const double RenderPointsScale = 2.0;
    /// <summary>固定左右裁边，只去掉这段宽度内的空白，不按每题内容宽度收紧。</summary>
    private const double HorizontalCropDip = 20 * 96 / 25.4;
    /// <summary>上下裁边后保留的余白（DIP），保证原始内容和选项圆圈不贴边。</summary>
    private const double VerticalPaddingDip = 18;

    private readonly Dictionary<string, ToggleButton> _optionButtons = new(StringComparer.Ordinal);
    private readonly List<(ToggleButton Button, double CenterX, double CenterY, double CoverDiameter)> _optionGeometry = new();
    private readonly List<Canvas> _pageOverlays = new();
    private ToggleButton? _abstainButton;
    private string? _questionId;
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
    }

    public bool HasQuestion => _questionId is not null;

    /// <summary>
    /// 渲染一道题。pdf 为该题逐题 PDF 明文副本（调用方在返回后负责清零）；
    /// regions 为该题选项坐标（null 表示该包未提供坐标，仅原版显示、不叠加圆钮）。
    /// </summary>
    public async Task LoadAsync(string questionId, byte[] pdf,
        IReadOnlyDictionary<string, OptionRegion>? regions, bool interactionEnabled, bool allowAbstain,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? optionCaptions = null)
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
            var displayWidth = crop.Width * page.DisplayWidth / page.Width;
            var displayHeight = crop.Height * page.DisplayHeight / page.Height;
            var image = new Image
            {
                Source = source,
                Width = displayWidth,
                Height = displayHeight,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true,
            };
            var overlay = new Canvas { Width = displayWidth, Height = displayHeight };
            var host = new Grid { Width = displayWidth, Height = displayHeight, Margin = new Thickness(0, 0, 0, 12) };
            host.Children.Add(image);
            host.Children.Add(overlay);
            PagesHost.Children.Add(host);
            _pageOverlays.Add(overlay);
        }

        if (regions is not null)
        {
            // 图像和 Canvas 都使用原始 PDF 的 DIP 尺寸，位图高分辨率只用于清晰度。
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
                    Content = region.Overlay?.Text ?? DisplayLetter(optionCaptions?.GetValueOrDefault(optionId) ?? optionId),
                };
                button.Click += (_, _) => OptionClicked?.Invoke(optionId);
                _pageOverlays[region.PageIndex].Children.Add(button);
                _optionButtons[optionId] = button;
                var cover = region.Overlay;
                var halfWidth = Math.Max(Math.Abs(region.CenterX - (cover?.X ?? region.X)),
                    Math.Abs((cover is null ? region.X + region.Width : cover.X + cover.Width) - region.CenterX)) * page.DisplayWidth;
                var halfHeight = Math.Max(Math.Abs(region.CenterY - (cover?.Y ?? region.Y)),
                    Math.Abs((cover is null ? region.Y + region.Height : cover.Y + cover.Height) - region.CenterY)) * page.DisplayHeight;
                // 以字母中心为圆心，半径覆盖原标签最远角；不能仅按宽高最大值取半径，点号可能在右下角。
                // 直径至少为字母可见高度的两倍，避免无点标签的圆钮明显缩小。
                var coverDiameter = Math.Max(2 * Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight),
                    2 * region.Height * page.DisplayHeight);
                _optionGeometry.Add((button, (region.CenterX * page.Width - crop.X) * page.DisplayWidth / page.Width,
                    (region.CenterY * page.Height - crop.Y) * page.DisplayHeight / page.Height, coverDiameter));
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

        UpdateOptionGeometry();
    }

    private static string DisplayLetter(string caption)
    {
        var letters = string.Concat(caption.Where(char.IsLetterOrDigit));
        return letters.Length == 0 ? caption : letters;
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
    }

    /// <summary>以原始字形中心定位实心按钮，同时遮住完整原标签。</summary>
    private void UpdateOptionGeometry()
    {
        // 同题所有选项共用最大圆径，额外留 0.5 DIP 半径吸收抗锯齿边缘。
        var diameter = _optionGeometry.Count == 0 ? 0 : _optionGeometry.Max(option => option.CoverDiameter) + 1;
        foreach (var (button, centerX, centerY, _) in _optionGeometry)
        {
            button.Width = diameter;
            button.Height = diameter;
            button.FontSize = diameter * .55;
            Canvas.SetLeft(button, centerX - diameter / 2);
            Canvas.SetTop(button, centerY - diameter / 2);
        }
        if (_abstainButton is not null && _optionGeometry.Count > 0)
        {
            var first = _optionGeometry.FirstOrDefault(option => Equals(option.Button.Content, "A")).Button
                ?? _optionGeometry[0].Button;
            var canvas = (Canvas)first.Parent;
            // 多页宽度可能不同；页面在 PagesHost 中居中，先补上该页相对最宽页的偏移。
            var pageOffset = (_pageOverlays.Max(page => page.Width) - canvas.Width) / 2;
            var left = pageOffset + Canvas.GetLeft(first);
            _abstainButton.FontSize = first.FontSize + .5 * 96 / 72; // 比普通选项按钮增加 0.5 磅。
            var last = _optionGeometry.FirstOrDefault(option => Equals(option.Button.Content, "D")).Button;
            if (_optionGeometry.Count == 4 && Equals(first.Content, "A") && last is not null &&
                _optionGeometry.All(option => ReferenceEquals(option.Button.Parent, canvas)) &&
                _optionGeometry.Max(option => option.CenterY) - _optionGeometry.Min(option => option.CenterY) < diameter / 2)
            {
                // 只有四选项同排时居中；竖排、两行和跨页仍与 A 的圆圈左端对齐。
                _abstainButton.Margin = new Thickness(0);
                _abstainButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                left = pageOffset + (Canvas.GetLeft(first) + Canvas.GetLeft(last) + diameter) / 2
                    - _abstainButton.DesiredSize.Width / 2;
            }
            _abstainButton.Margin = new Thickness(Math.Max(0, left), 4, 0, 4);
        }
    }

    /// <summary>
    /// 上下按实际内容裁边；左右固定各 20mm，不随题干长短变化。
    /// 非标准页边距有印刷内容进入左右裁边区时，整道题统一保留原页宽，避免截断内容。
    /// </summary>
    private static Int32Rect[] CropRects(RenderedPage[] pages, IReadOnlyDictionary<string, OptionRegion>? regions)
    {
        var canCropHorizontally = pages.All(page => page.DisplayWidth > 2 * HorizontalCropDip &&
            (page.Content is not { } content ||
             content.X * page.DisplayWidth / page.Width >= HorizontalCropDip &&
             (content.X + content.Width) * page.DisplayWidth / page.Width <= page.DisplayWidth - HorizontalCropDip));
        var vertical = new (int Top, int Bottom)?[pages.Length];
        for (var index = 0; index < pages.Length; index++)
        {
            var top = int.MaxValue;
            var bottom = int.MinValue;
            if (pages[index].Content is { } content)
            {
                top = content.Y;
                bottom = content.Y + content.Height;
            }
            foreach (var region in regions?.Values ?? [])
            {
                if (region.PageIndex != index) continue;
                var y = (int)Math.Round(region.CenterY * pages[index].Height);
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
            if (vertical[index] is not { } span)
            {
                rects[index] = new Int32Rect(0, 0, width, height);
                continue;
            }
            var x0 = canCropHorizontally ? (int)Math.Floor(HorizontalCropDip * width / pages[index].DisplayWidth) : 0;
            var x1 = width - x0;
            var padding = (int)Math.Ceiling(VerticalPaddingDip * height / pages[index].DisplayHeight);
            var y0 = Math.Max(0, span.Top - padding);
            var y1 = Math.Min(height, span.Bottom + padding);
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

    private sealed record RenderedPage(BitmapSource Bitmap, double Width, double Height,
        double DisplayWidth, double DisplayHeight, Int32Rect? Content);

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
                // 保留页面本来的 DIP 宽高；渲染分辨率、整数像素舍入和 PNG DPI 都不改变字号或物理比例。
                pages[index] = new RenderedPage(bitmap, bitmap.PixelWidth, bitmap.PixelHeight,
                    size.Width, size.Height, ContentBounds(bitmap));
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
