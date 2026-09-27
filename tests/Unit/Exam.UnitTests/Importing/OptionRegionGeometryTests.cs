using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 选项区域归一化的纯几何验证。旋转与裁剪各组合见 docs/d03-option-region-contract.md 第 4.3 节的换算表；
/// 用例中的期望值按该表手工推算，不复用被测代码的公式。
/// </summary>
public sealed class OptionRegionGeometryTests
{
    // 非正方形显示框，便于区分旋转方向；原点非零，覆盖 CropBox 带偏移的情形。
    private static readonly PageDisplayBox OffsetBox = new(50, 100, 200, 100);

    // 在 OffsetBox 内：u = (70-50)/200 = 0.1，v = (130-100)/100 = 0.3，右上角 u = 0.3，v = 0.5。
    private static readonly RawRect Sample = new(70, 130, 110, 150);

    [Theory]
    [InlineData(0, 0.1, 0.5, 0.2, 0.2)]
    [InlineData(90, 0.3, 0.1, 0.2, 0.2)]
    [InlineData(180, 0.7, 0.3, 0.2, 0.2)]
    [InlineData(270, 0.5, 0.7, 0.2, 0.2)]
    public void Normalize_AppliesContractRotationTable(int rotation, double x, double y, double width, double height)
    {
        var result = OptionRegionGeometry.Normalize(Sample, OffsetBox, rotation);

        Assert.True(result.Succeeded, result.Message);
        var region = result.Region!.Value;
        Assert.Equal(x, region.X, 9);
        Assert.Equal(y, region.Y, 9);
        Assert.Equal(width, region.Width, 9);
        Assert.Equal(height, region.Height, 9);
    }

    [Fact]
    public void Normalize_RectCoveringWholeBox_MapsToUnitSquare()
    {
        var box = new PageDisplayBox(0, 0, 200, 100);

        var result = OptionRegionGeometry.Normalize(new RawRect(0, 0, 200, 100), box, 0);

        Assert.True(result.Succeeded, result.Message);
        var region = result.Region!.Value;
        Assert.Equal(0, region.X, 9);
        Assert.Equal(0, region.Y, 9);
        Assert.Equal(1, region.Width, 9);
        Assert.Equal(1, region.Height, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Normalize_IsIndependentOfCornerOrderInRawRect(int rotation)
    {
        var canonical = OptionRegionGeometry.Normalize(Sample, OffsetBox, rotation);
        // 同一矩形写成另一种角序：PdfPig 的报告顺序在旋转页面上会退化，四个数不能假定先后。
        var shuffled = OptionRegionGeometry.Normalize(new RawRect(110, 150, 70, 130), OffsetBox, rotation);

        Assert.True(canonical.Succeeded && shuffled.Succeeded);
        Assert.Equal(canonical.Region!.Value, shuffled.Region!.Value);
    }

    [Fact]
    public void Normalize_LabelOutsideDisplayBox_Fails()
    {
        // 有效框 x 到 250 为止；该矩形右边界 300 越界。
        var result = OptionRegionGeometry.Normalize(new RawRect(200, 130, 300, 150), OffsetBox, 0);

        Assert.False(result.Succeeded);
        Assert.Equal(OptionRegionIssueCode.RegionOutOfBounds, result.Issue);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Normalize_NonFiniteRect_Fails(double value)
    {
        var result = OptionRegionGeometry.Normalize(new RawRect(70, 130, value, 150), OffsetBox, 0);

        Assert.False(result.Succeeded);
        Assert.Equal(OptionRegionIssueCode.RegionOutOfBounds, result.Issue);
    }

    [Fact]
    public void Normalize_DegenerateRect_Fails()
    {
        // 宽高为正是契约要求；零面积区域不能当作有效标签。
        var result = OptionRegionGeometry.Normalize(new RawRect(70, 130, 70, 130), OffsetBox, 0);

        Assert.False(result.Succeeded);
        Assert.Equal(OptionRegionIssueCode.RegionOutOfBounds, result.Issue);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    public void Normalize_UnsupportedRotation_Fails(int rotation)
    {
        var result = OptionRegionGeometry.Normalize(Sample, OffsetBox, rotation);

        Assert.False(result.Succeeded);
        Assert.Equal(OptionRegionIssueCode.PdfUnreadable, result.Issue);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(200, 0)]
    public void Normalize_NonPositiveDisplayBox_Fails(double width, double height)
    {
        var result = OptionRegionGeometry.Normalize(Sample, new PageDisplayBox(50, 100, width, height), 0);

        Assert.False(result.Succeeded);
        Assert.Equal(OptionRegionIssueCode.PdfUnreadable, result.Issue);
    }

    [Fact]
    public void TryMergeConnected_OverlappingFragments_MergesToSingleRegion()
    {
        RawRect[] fragments = [new(10, 10, 30, 20), new(25, 15, 40, 25)];

        Assert.True(OptionRegionGeometry.TryMergeConnected(fragments, out var union));
        Assert.Equal(new RawRect(10, 10, 40, 25), union);
    }

    [Fact]
    public void TryMergeConnected_TouchingFragments_MergesToSingleRegion()
    {
        // 边界相触也算连通：多行标签逐行断开时仍需合成一个选项区域。
        RawRect[] fragments = [new(10, 10, 30, 20), new(30, 10, 50, 20)];

        Assert.True(OptionRegionGeometry.TryMergeConnected(fragments, out var union));
        Assert.Equal(new RawRect(10, 10, 50, 20), union);
    }

    [Fact]
    public void TryMergeConnected_SeparatedFragments_ReportsNotConnected()
    {
        // 同一目标名的两处标记彼此分离，属重复标记，不能取并集掩盖。
        RawRect[] fragments = [new(10, 10, 30, 20), new(100, 100, 120, 110)];

        Assert.False(OptionRegionGeometry.TryMergeConnected(fragments, out var union));
        Assert.Equal(default(RawRect), union);
    }

    [Fact]
    public void TryMergeConnected_ChainedFragments_MergesTransitively()
    {
        RawRect[] fragments = [new(10, 10, 20, 20), new(100, 10, 110, 20), new(15, 15, 105, 25)];

        Assert.True(OptionRegionGeometry.TryMergeConnected(fragments, out var union));
        Assert.Equal(new RawRect(10, 10, 110, 25), union);
    }

    [Fact]
    public void TryMergeConnected_EmptyInput_ReportsNotConnected()
    {
        Assert.False(OptionRegionGeometry.TryMergeConnected([], out _));
    }

    [Fact]
    public void RawRect_FromNumbers_TakesMinMaxPerAxis()
    {
        var rect = RawRect.FromNumbers([30, 20, 10, 40]);

        Assert.Equal(10, rect.MinX);
        Assert.Equal(20, rect.MinY);
        Assert.Equal(30, rect.MaxX);
        Assert.Equal(40, rect.MaxY);
    }

    [Fact]
    public void RawRect_FromNumbers_RejectsWrongArity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RawRect.FromNumbers([1, 2, 3]));
    }
}
