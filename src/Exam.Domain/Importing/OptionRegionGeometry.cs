using Exam.Contracts.Importing;

namespace Exam.Domain.Importing;

/// <summary>
/// 一页的有效显示框（PDF 原始单位，左下角原点）：CropBox 与 MediaBox 的交集，
/// 缺少 CropBox 时取 MediaBox。宽高必须为正。
/// </summary>
public readonly record struct PageDisplayBox(double Left, double Bottom, double Width, double Height);

/// <summary>
/// 一条注解的原始 <c>/Rect</c>，四个数已按横纵取过 min/max。
/// 只表示轴对齐范围，不假设原字典里的角序。
/// </summary>
public readonly record struct RawRect(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>从原始 <c>/Rect</c> 的四个数构造，内部先取横纵 min/max。</summary>
    public static RawRect FromNumbers(ReadOnlySpan<double> four)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(four.Length, 4);
        return new RawRect(
            Math.Min(four[0], four[2]),
            Math.Min(four[1], four[3]),
            Math.Max(four[0], four[2]),
            Math.Max(four[1], four[3]));
    }

    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    /// <summary>与另一矩形重叠或相接（边界相触也算连通）。</summary>
    public bool TouchesOrOverlaps(RawRect other, double tolerance) =>
        MaxX >= other.MinX - tolerance && other.MaxX >= MinX - tolerance &&
        MaxY >= other.MinY - tolerance && other.MaxY >= MinY - tolerance;

    public RawRect Union(RawRect other) => new(
        Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
}

/// <summary>归一化后的矩形，原点在显示页面左上角，取值落在 <c>[0,1]</c>。</summary>
public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height);

/// <summary>归一化结果。失败时 <see cref="Region"/> 为空并给出稳定的问题类别。</summary>
public sealed record OptionRegionNormalizationResult(
    NormalizedRegion? Region,
    OptionRegionIssueCode Issue,
    string Message)
{
    public bool Succeeded => Region is not null;
}

/// <summary>
/// 选项标签矩形从 PDF 原始坐标到 <c>pageTopLeftNormalizedV1</c> 的纯几何变换，
/// 以及同名碎片的连通合并判定。与 PDF 读取分离，便于对旋转与裁剪的各组合穷举验证。
/// 约定见 docs/d03-option-region-contract.md 第 4.3 节。
/// </summary>
public static class OptionRegionGeometry
{
    /// <summary>
    /// 边界与相接判定的浮点容差。仅用于吸收除法、旋转换位与相邻判定中的表示误差，
    /// 不用于放宽契约的有效区域校验；超出该容差的区域一律失败，不做裁掉或钳位。
    /// </summary>
    public const double BoundaryTolerance = 1e-9;

    public static bool IsSupportedRotation(int degrees) => degrees is 0 or 90 or 180 or 270;

    /// <summary>
    /// 把一条注解的原始 <c>/Rect</c> 变换成归一化矩形。
    /// <paramref name="rawRect"/> 必须来自原始字典，**不能**使用 PdfPig 已应用初始矩阵的
    /// <c>Annotation.Rectangle</c>。
    /// </summary>
    public static OptionRegionNormalizationResult Normalize(
        RawRect rawRect,
        PageDisplayBox displayBox,
        int rotationDegrees)
    {
        if (!double.IsFinite(rawRect.MinX) || !double.IsFinite(rawRect.MinY) ||
            !double.IsFinite(rawRect.MaxX) || !double.IsFinite(rawRect.MaxY))
            return Fail(OptionRegionIssueCode.RegionOutOfBounds, "注解 /Rect 必须是四个有限数");

        if (!IsSupportedRotation(rotationDegrees))
            return Fail(OptionRegionIssueCode.PdfUnreadable, $"页面 Rotate 只能是 0/90/180/270，实际为 {rotationDegrees}");

        if (!double.IsFinite(displayBox.Left) || !double.IsFinite(displayBox.Bottom) ||
            !double.IsFinite(displayBox.Width) || !double.IsFinite(displayBox.Height) ||
            !(displayBox.Width > 0) || !(displayBox.Height > 0))
            return Fail(OptionRegionIssueCode.PdfUnreadable, "页面有效显示框的宽高必须为正的有限数");

        Span<double> xs = stackalloc double[4];
        Span<double> ys = stackalloc double[4];
        var index = 0;
        foreach (var (px, py) in new[]
                 {
                     (rawRect.MinX, rawRect.MinY), (rawRect.MaxX, rawRect.MinY),
                     (rawRect.MinX, rawRect.MaxY), (rawRect.MaxX, rawRect.MaxY)
                 })
        {
            var u = (px - displayBox.Left) / displayBox.Width;
            var v = (py - displayBox.Bottom) / displayBox.Height;
            (xs[index], ys[index]) = rotationDegrees switch
            {
                0 => (u, 1 - v),
                90 => (v, u),
                180 => (1 - u, v),
                _ => (1 - v, 1 - u)
            };
            index++;
        }

        var left = xs.ToArray().Min();
        var top = ys.ToArray().Min();
        var width = xs.ToArray().Max() - left;
        var height = ys.ToArray().Max() - top;

        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            !(width > 0) || !(height > 0) ||
            left < -BoundaryTolerance || top < -BoundaryTolerance ||
            left + width > 1 + BoundaryTolerance || top + height > 1 + BoundaryTolerance)
            return Fail(OptionRegionIssueCode.RegionOutOfBounds, "归一化区域越出 [0,1] 或宽高非正");

        // 容差内把表示误差归到边界，使产物不出现 >1 或 <0 的数值；真正的越界已由上一判定拒绝。
        return new OptionRegionNormalizationResult(
            new NormalizedRegion(Bound(left), Bound(top), Bound(width), Bound(height)),
            OptionRegionIssueCode.None,
            string.Empty);
    }

    /// <summary>
    /// 合并相互重叠或相接的碎片。只有**连通**的碎片才允许取矩形并集：
    /// 合并后仍有多个分组说明碎片彼此分离，必须按重复标记处理，返回 false。
    /// </summary>
    public static bool TryMergeConnected(IReadOnlyList<RawRect> fragments, out RawRect union)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        if (fragments.Count == 0) { union = default; return false; }

        var groups = new List<RawRect>(fragments);
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < groups.Count && !merged; i++)
            {
                for (var j = i + 1; j < groups.Count; j++)
                {
                    if (!groups[i].TouchesOrOverlaps(groups[j], BoundaryTolerance)) continue;
                    groups[i] = groups[i].Union(groups[j]);
                    groups.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }

        // 未连通时不给出可用的并集：调用方必须按重复标记处理，不能用整体外接矩形掩盖分离。
        if (groups.Count != 1)
        {
            union = default;
            return false;
        }

        union = groups[0];
        return true;
    }

    private static double Bound(double value) => Math.Clamp(value, 0d, 1d);

    private static OptionRegionNormalizationResult Fail(OptionRegionIssueCode issue, string message) =>
        new(null, issue, message);
}
