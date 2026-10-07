using Exam.Domain.Scoring;

namespace Exam.UnitTests.Importing;

public sealed class ScoreExpressionTests
{
    [Theory]
    [InlineData("3", "3")]
    [InlineData("-0.75", "-0.75")]
    [InlineData("1+1", "2")]
    [InlineData("2*3", "6")]
    [InlineData("(1+2)/4", "0.75")]
    [InlineData("2*-3", "-6")]
    [InlineData("1--2", "3")]
    [InlineData("1/8", "0.125")]
    [InlineData("15/2", "7.5")]
    [InlineData(" 1 + 2 ", "3")]
    [InlineData("0.1+0.2", "0.3")]
    public void Check_ConstantExpression_IsValidAndExact(string expression, string expected)
    {
        // 0.1+0.2 必须精确为 0.3（S15 要求），不允许浮点误差。
        var check = ScoreExpressionParser.Check(expression);

        Assert.True(check.IsValid, check.Message);
        Assert.True(check.IsWholeConstant);
        Assert.NotNull(check.ConstantValue);
        Assert.True(check.ConstantValue!.TryFormatFiniteDecimal(out var text));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("{正确选项总数}")]
    [InlineData("{选择的正确选项数}")]
    [InlineData("3*{正确选项总数}")]
    [InlineData("{选择的正确选项数}+1")]
    [InlineData("(2+{正确选项总数})/2")]
    public void Check_VariableExpression_IsValidAndNotWholeConstant(string expression)
    {
        var check = ScoreExpressionParser.Check(expression);

        Assert.True(check.IsValid, check.Message);
        Assert.NotNull(check.Root);
        Assert.False(check.IsWholeConstant);
    }

    [Theory]
    [InlineData("{总题数}", 0)]
    [InlineData("{N}", 0)]
    [InlineData("{选择的正确选项}", 0)]
    [InlineData("1+{总题数}", 2)]
    public void Check_UnknownVariable_IsRejectedWithPosition(string expression, int position)
    {
        var check = ScoreExpressionParser.Check(expression);

        Assert.Equal(ScoreExpressionStatus.UnknownVariable, check.Status);
        Assert.Equal(position, check.Position);
        Assert.Contains("未知变量", check.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1 +")]
    [InlineData("(1+2")]
    [InlineData("1)")]
    [InlineData("*3")]
    [InlineData("1;2")]
    [InlineData("1e3")]
    [InlineData("1.2.3")]
    [InlineData("1.")]
    [InlineData("{正确选项总数")]
    [InlineData("1 2")]
    [InlineData("　1")]
    [InlineData("１+１")]
    public void Check_InvalidSyntax_IsRejected(string expression)
    {
        var check = ScoreExpressionParser.Check(expression);

        Assert.Equal(ScoreExpressionStatus.Invalid, check.Status);
        Assert.NotNull(check.Message);
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("1/(3-3)")]
    [InlineData("5/(2-2)")]
    [InlineData("1+{正确选项总数}/(4-4)")]
    public void Check_ConstantDivisorIsZero_IsRejected(string expression)
    {
        var check = ScoreExpressionParser.Check(expression);

        Assert.Equal(ScoreExpressionStatus.DivisionByZero, check.Status);
        Assert.Contains("除零", check.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1/3")]
    [InlineData("10/3")]
    [InlineData("1/7")]
    [InlineData("(1+0)/3")]
    public void Check_ConstantExpressionIsNotFiniteDecimal_IsRejected(string expression)
    {
        // 发布前最终逐题分必须为有限小数，1÷3 这类分值要拒绝。
        var check = ScoreExpressionParser.Check(expression);

        Assert.Equal(ScoreExpressionStatus.NonTerminatingDecimal, check.Status);
    }

    [Fact]
    public void Check_DivideByVariable_IsAcceptedBecauseItNeedsAnswerKey()
    {
        // 分母含变量时静态无法判定，留给 精确计分 在具体作答情形下判定。
        var check = ScoreExpressionParser.Check("1/{正确选项总数}");

        Assert.True(check.IsValid, check.Message);
        Assert.False(check.IsWholeConstant);
    }

    [Fact]
    public void Check_WholeExpressionIsFinite_IsAcceptedEvenIfSubexpressionIsNot()
    {
        // 只对整条表达式判定有限小数，避免 (1/3)*3 这类整体有限的情况被误判。
        var check = ScoreExpressionParser.Check("(1/3)*3");

        Assert.True(check.IsValid, check.Message);
        Assert.Equal("1", check.ConstantValue!.ToString());
    }

    [Fact]
    public void Check_ExpressionLongerThanLimit_IsRejected()
    {
        var expression = string.Join('+', Enumerable.Repeat("1", ScoreExpressionParser.MaxLength));

        var check = ScoreExpressionParser.Check(expression);

        Assert.Equal(ScoreExpressionStatus.Invalid, check.Status);
        Assert.Equal(ScoreExpressionParser.MaxLength, check.Position);
    }

    [Fact]
    public void Check_NestingDeeperThanLimit_IsRejected()
    {
        var expression = new string('(', ScoreExpressionParser.MaxDepth + 4)
                         + "1"
                         + new string(')', ScoreExpressionParser.MaxDepth + 4);

        var check = ScoreExpressionParser.Check(expression);

        Assert.Equal(ScoreExpressionStatus.Invalid, check.Status);
    }

    [Fact]
    public void Evaluate_WithVariableValues_ReturnsExactScore()
    {
        var check = ScoreExpressionParser.Check("{正确选项总数}*{选择的正确选项数}");
        Assert.NotNull(check.Root);

        var value = ScoreExpressionParser.Evaluate(check.Root!, correctOptionCount: 4, selectedCorrectCount: 3);

        Assert.Equal(ExactRational.FromInteger(12), value);
    }

    [Fact]
    public void Evaluate_NegativePartialScore_KeepsExactValue()
    {
        // 交大式规则：错扣 0.75 分，原始分值允许为负，不做舍入。
        var check = ScoreExpressionParser.Check("-0.75");
        Assert.NotNull(check.Root);

        var value = ScoreExpressionParser.Evaluate(check.Root!, 1, 0);

        Assert.True(value.IsNegative);
        Assert.Equal("-0.75", value.ToString());
    }
}
