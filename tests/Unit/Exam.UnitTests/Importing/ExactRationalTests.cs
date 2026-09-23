using System.Numerics;

using Exam.Domain.Scoring;

namespace Exam.UnitTests.Importing;

public sealed class ExactRationalTests
{
    [Theory]
    [InlineData(3, 2, "1.5")]
    [InlineData(9, 1, "9")]
    [InlineData(-3, 4, "-0.75")]
    [InlineData(0, 1, "0")]
    [InlineData(1, 8, "0.125")]
    [InlineData(100, 1, "100")]
    [InlineData(1, 4, "0.25")]
    public void TryFormatFiniteDecimal_ReturnsExactText(int numerator, int denominator, string expected)
    {
        var value = ExactRational.Create(numerator, denominator);

        Assert.True(value.TryFormatFiniteDecimal(out var text));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(1, 6, false)]
    [InlineData(10, 3, false)]
    [InlineData(1, 8, true)]
    [InlineData(3, 10, true)]
    [InlineData(5, 1, true)]
    public void IsFiniteDecimal_DependsOnlyOnTwoAndFiveFactors(int numerator, int denominator, bool expected)
    {
        var value = ExactRational.Create(numerator, denominator);

        Assert.Equal(expected, value.IsFiniteDecimal);
        Assert.Equal(expected, value.TryFormatFiniteDecimal(out _));
    }

    [Fact]
    public void TryFormatFiniteDecimal_ReturnsFalseForNonFiniteValue()
    {
        var value = ExactRational.Create(1, 3);

        Assert.False(value.TryFormatFiniteDecimal(out var text));
        Assert.Equal(string.Empty, text);
    }

    [Theory]
    [InlineData(2, 4, 1, 2)]
    [InlineData(-1, -2, 1, 2)]
    [InlineData(1, -2, -1, 2)]
    [InlineData(0, 5, 0, 1)]
    public void Create_NormalizesSignAndReduces(int numerator, int denominator, int expectedNumerator, int expectedDenominator)
    {
        var value = ExactRational.Create(numerator, denominator);

        Assert.Equal(new BigInteger(expectedNumerator), value.Numerator);
        Assert.Equal(new BigInteger(expectedDenominator), value.Denominator);
    }

    [Fact]
    public void Create_ZeroDenominator_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => ExactRational.Create(1, 0));
    }

    [Fact]
    public void Divide_ByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() => ExactRational.One / ExactRational.Zero);
    }

    [Fact]
    public void Addition_KeepsExactValue()
    {
        // 0.1+0.2 用 double 会得到 0.30000000000000004；精确有理数必须得到 0.3。
        var sum = ExactRational.Create(1, 10) + ExactRational.Create(2, 10);

        Assert.Equal(ExactRational.Create(3, 10), sum);
        Assert.Equal("0.3", sum.ToString());
    }

    [Fact]
    public void EqualValues_ShareSameNormalizedForm()
    {
        Assert.Equal(ExactRational.Create(2, 4), ExactRational.Create(1, 2));
    }
}
