using System.Globalization;
using System.Numerics;

using Exam.Domain.Scoring;

namespace Exam.Domain.Importing;

/// <summary>
/// 科目与题组满分的取值规则：无变量的有限十进制常量。
/// 依据 requirements.md「题组与科目满分逐题求和」——需求中只有「每小题…分」使用变量，满分本身始终是常量。
/// 与 schemas/exam-manifest.schema.json 的 scoreConstant 正则等价，等价性由单元测试核对。
/// </summary>
public static class ScoreConstantRules
{
    public const int MaxLength = 32;

    public static bool IsValid(string? value) => value is not null && Parse(value, out _);

    public static bool TryParse(string? value, out ExactRational exact)
    {
        if (value is null)
        {
            exact = ExactRational.Zero;
            return false;
        }

        return Parse(value, out exact);
    }

    private static bool Parse(string value, out ExactRational exact)
    {
        exact = ExactRational.Zero;

        if (value.Length == 0 || value.Length > MaxLength)
        {
            return false;
        }

        var index = value[0] is '+' or '-' ? 1 : 0;
        var integerDigits = 0;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            index++;
            integerDigits++;
        }

        if (integerDigits == 0)
        {
            return false;
        }

        var fractionDigits = 0;
        if (index < value.Length)
        {
            if (value[index] != '.')
            {
                return false;
            }

            index++;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
            {
                index++;
                fractionDigits++;
            }

            if (fractionDigits == 0 || index != value.Length)
            {
                return false;
            }
        }

        var negative = value[0] == '-';
        var text = negative || value[0] == '+' ? value[1..] : value;
        var dot = text.IndexOf('.');
        var fraction = dot < 0 ? string.Empty : text[(dot + 1)..];
        var digits = dot < 0 ? text : text[..dot] + fraction;

        var numerator = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        var denominator = fraction.Length == 0 ? BigInteger.One : BigInteger.Pow(10, fraction.Length);

        exact = ExactRational.Create(negative ? -numerator : numerator, denominator);
        return true;
    }
}
