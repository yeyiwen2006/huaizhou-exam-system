using System.Globalization;
using System.Numerics;

namespace Exam.Domain.Scoring;

/// <summary>
/// 精确有理数，用于计分的中间值与最终值。依据 agent-implementation-guide.md「精确计分用 BigInteger
/// 分子分母，最终有限小数以字符串保存、传输」，不使用 double 或固定小数位。
/// 始终以既约形式保存且分母为正，因此相等值必然有相同的分子分母。
/// </summary>
public sealed record ExactRational
{
    private ExactRational(BigInteger numerator, BigInteger denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    public BigInteger Numerator { get; }

    public BigInteger Denominator { get; }

    public static ExactRational Zero { get; } = new(BigInteger.Zero, BigInteger.One);

    public static ExactRational One { get; } = new(BigInteger.One, BigInteger.One);

    public static ExactRational Create(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new DivideByZeroException("有理数的分母不能为零。");
        }

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        return divisor > BigInteger.One
            ? new ExactRational(numerator / divisor, denominator / divisor)
            : new ExactRational(numerator, denominator);
    }

    public static ExactRational FromInteger(BigInteger value) => Create(value, BigInteger.One);

    public bool IsZero => Numerator.IsZero;

    public bool IsNegative => Numerator.Sign < 0;

    public static ExactRational operator +(ExactRational left, ExactRational right) =>
        Create(
            (left.Numerator * right.Denominator) + (right.Numerator * left.Denominator),
            left.Denominator * right.Denominator);

    public static ExactRational operator -(ExactRational left, ExactRational right) =>
        Create(
            (left.Numerator * right.Denominator) - (right.Numerator * left.Denominator),
            left.Denominator * right.Denominator);

    public static ExactRational operator *(ExactRational left, ExactRational right) =>
        Create(left.Numerator * right.Numerator, left.Denominator * right.Denominator);

    public static ExactRational operator /(ExactRational left, ExactRational right)
    {
        if (right.IsZero)
        {
            throw new DivideByZeroException("有理数的除数不能为零。");
        }

        return Create(left.Numerator * right.Denominator, left.Denominator * right.Numerator);
    }

    public static ExactRational operator -(ExactRational value) => Create(-value.Numerator, value.Denominator);

    /// <summary>
    /// 是否可以写成有限小数：既约分母只含 2 与 5 两种质因子。
    /// </summary>
    public bool IsFiniteDecimal => !TryReducePowerFactors(Denominator, out _);

    /// <summary>
    /// 转成需求要求的字符串形式：精确、不舍入，整数不带小数点、不带多余尾零。无法用有限小数表示时返回 false。
    /// </summary>
    public bool TryFormatFiniteDecimal(out string text)
    {
        if (TryReducePowerFactors(Denominator, out var scale))
        {
            text = string.Empty;
            return false;
        }

        var scaled = Numerator * BigInteger.Pow(10, scale) / Denominator;
        var negative = scaled.Sign < 0;
        var digits = BigInteger.Abs(scaled).ToString(CultureInfo.InvariantCulture);

        string formatted;
        if (scale == 0)
        {
            formatted = digits;
        }
        else if (digits.Length <= scale)
        {
            formatted = "0." + new string('0', scale - digits.Length) + digits;
        }
        else
        {
            formatted = digits[..^scale] + "." + digits[^scale..];
        }

        text = negative ? "-" + formatted : formatted;
        return true;
    }

    public override string ToString() =>
        TryFormatFiniteDecimal(out var text) ? text : $"{Numerator}/{Denominator}";

    /// <summary>
    /// 把分母写成分母 = 2^a * 5^b 的形式。含其他质因子时返回 true（即不是有限小数）。
    /// scale 取 a、b 中的较大者，用于换算到 10 的幂。
    /// </summary>
    private static bool TryReducePowerFactors(BigInteger denominator, out int scale)
    {
        var twos = 0;
        var fives = 0;
        while (denominator % 2 == 0)
        {
            denominator /= 2;
            twos++;
        }

        while (denominator % 5 == 0)
        {
            denominator /= 5;
            fives++;
        }

        scale = Math.Max(twos, fives);
        return denominator != BigInteger.One;
    }
}
