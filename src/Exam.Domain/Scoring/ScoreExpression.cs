using System.Numerics;

namespace Exam.Domain.Scoring;

/// <summary>计分表达式的语法树节点。</summary>
public abstract record ScoreNode;

public sealed record ScoreConstant(ExactRational Value) : ScoreNode;

public sealed record ScoreVariable(string Name) : ScoreNode;

public sealed record ScoreNegation(ScoreNode Operand) : ScoreNode;

public enum ScoreOperator
{
    Add,
    Subtract,
    Multiply,
    Divide
}

public sealed record ScoreBinary(ScoreOperator Operator, ScoreNode Left, ScoreNode Right) : ScoreNode;

public enum ScoreExpressionStatus
{
    Valid,

    /// <summary>文法错误：非法字符、括号不配、缺操作数、超长或嵌套过深。</summary>
    Invalid,

    /// <summary>使用了两个中文变量名之外的变量。</summary>
    UnknownVariable,

    /// <summary>存在常量分母为零的除法。</summary>
    DivisionByZero,

    /// <summary>整条表达式为常量，但精确值不是有限小数。</summary>
    NonTerminatingDecimal
}

/// <summary>
/// 表达式静态检查结果。Position 为出错字符在表达式中的偏移，仅文法与变量错误会给出；
/// 依赖常量折叠的判定没有字符位置，靠调用方给出的字段定位。
/// </summary>
public sealed record ScoreExpressionCheck(
    ScoreExpressionStatus Status,
    int Position,
    string Message,
    ScoreNode? Root,
    ExactRational? ConstantValue)
{
    public bool IsValid => Status == ScoreExpressionStatus.Valid;

    /// <summary>整条表达式是否为常量（不含变量），是则 ConstantValue 给出精确值。</summary>
    public bool IsWholeConstant => ConstantValue is not null;
}

/// <summary>
/// 专用计分表达式解析器。文法见 docs/import-package-spec.md：
/// 有限十进制常量、两个中文变量、加减乘除与括号，左结合、乘除优先、一元负号只作用于最小项。
/// 只做解析与常量部分的精确判定，**不执行通用代码**；含变量的求值由 D04 在具体作答情形下完成。
/// </summary>
public static class ScoreExpressionParser
{
    public const string CorrectOptionCountVariable = "正确选项总数";
    public const string SelectedCorrectCountVariable = "选择的正确选项数";
    public const int MaxLength = 200;
    public const int MaxDepth = 32;

    public static IReadOnlyList<string> Variables { get; } =
        [CorrectOptionCountVariable, SelectedCorrectCountVariable];

    public static ScoreExpressionCheck Check(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Invalid(0, "计分表达式不能为空。");
        }

        if (text.Length > MaxLength)
        {
            return Invalid(MaxLength, $"计分表达式长度不能超过 {MaxLength} 个字符。");
        }

        var parser = new Parser(text);
        var root = parser.ParseExpression(out var error);
        if (root is null)
        {
            return error!;
        }

        parser.SkipSpaces();
        if (!parser.AtEnd)
        {
            return Invalid(parser.Position, $"表达式在位置 {parser.Position} 处有多余内容：{text[parser.Position]}。");
        }

        // 常量分母为零：只对能静态确定的除法报错，分母含变量时留给 D04 判定。
        var divisor = FindConstantZeroDivisor(root);
        if (divisor is not null)
        {
            return new ScoreExpressionCheck(
                ScoreExpressionStatus.DivisionByZero,
                0,
                "计分表达式存在常量除零：分母的精确值为 0。",
                root,
                null);
        }

        if (TryEvaluateConstant(root, out var value))
        {
            if (!value.IsFiniteDecimal)
            {
                return new ScoreExpressionCheck(
                    ScoreExpressionStatus.NonTerminatingDecimal,
                    0,
                    $"计分表达式的精确值为 {value}，不是有限小数；发布前最终逐题分必须为有限小数。",
                    root,
                    value);
            }

            return new ScoreExpressionCheck(ScoreExpressionStatus.Valid, 0, "计分表达式合法。", root, value);
        }

        return new ScoreExpressionCheck(ScoreExpressionStatus.Valid, 0, "计分表达式合法。", root, null);
    }

    /// <summary>整棵子树是否为常量；是则给出精确值。</summary>
    public static bool TryEvaluateConstant(ScoreNode node, out ExactRational value)
    {
        switch (node)
        {
            case ScoreConstant constant:
                value = constant.Value;
                return true;
            case ScoreVariable:
                value = ExactRational.Zero;
                return false;
            case ScoreNegation negation when TryEvaluateConstant(negation.Operand, out var operand):
                value = -operand;
                return true;
            case ScoreBinary binary when TryEvaluateConstant(binary.Left, out var left)
                                         && TryEvaluateConstant(binary.Right, out var right):
                if (binary.Operator == ScoreOperator.Divide && right.IsZero)
                {
                    value = ExactRational.Zero;
                    return false;
                }

                value = binary.Operator switch
                {
                    ScoreOperator.Add => left + right,
                    ScoreOperator.Subtract => left - right,
                    ScoreOperator.Multiply => left * right,
                    _ => left / right
                };
                return true;
            default:
                value = ExactRational.Zero;
                return false;
        }
    }

    /// <summary>把两个中文变量替换为给定取值后精确求值。供 D04 在具体作答情形下复用。</summary>
    public static ExactRational Evaluate(ScoreNode node, BigInteger correctOptionCount, BigInteger selectedCorrectCount) =>
        node switch
        {
            ScoreConstant constant => constant.Value,
            ScoreVariable variable when variable.Name == CorrectOptionCountVariable =>
                ExactRational.FromInteger(correctOptionCount),
            ScoreVariable variable when variable.Name == SelectedCorrectCountVariable =>
                ExactRational.FromInteger(selectedCorrectCount),
            ScoreVariable variable => throw new ArgumentException($"未知变量：{variable.Name}。", nameof(node)),
            ScoreNegation negation => -Evaluate(negation.Operand, correctOptionCount, selectedCorrectCount),
            ScoreBinary binary => binary.Operator switch
            {
                ScoreOperator.Add => Evaluate(binary.Left, correctOptionCount, selectedCorrectCount)
                                     + Evaluate(binary.Right, correctOptionCount, selectedCorrectCount),
                ScoreOperator.Subtract => Evaluate(binary.Left, correctOptionCount, selectedCorrectCount)
                                          - Evaluate(binary.Right, correctOptionCount, selectedCorrectCount),
                ScoreOperator.Multiply => Evaluate(binary.Left, correctOptionCount, selectedCorrectCount)
                                          * Evaluate(binary.Right, correctOptionCount, selectedCorrectCount),
                _ => Evaluate(binary.Left, correctOptionCount, selectedCorrectCount)
                     / Evaluate(binary.Right, correctOptionCount, selectedCorrectCount)
            },
            _ => throw new ArgumentException($"无法求值的节点：{node.GetType().Name}。", nameof(node))
        };

    private static ScoreNode? FindConstantZeroDivisor(ScoreNode node)
    {
        switch (node)
        {
            case ScoreBinary binary:
                if (binary.Operator == ScoreOperator.Divide
                    && TryEvaluateConstant(binary.Right, out var divisor)
                    && divisor.IsZero)
                {
                    return node;
                }

                return FindConstantZeroDivisor(binary.Left) ?? FindConstantZeroDivisor(binary.Right);
            case ScoreNegation negation:
                return FindConstantZeroDivisor(negation.Operand);
            default:
                return null;
        }
    }

    private static ScoreExpressionCheck Invalid(int position, string message) =>
        new(ScoreExpressionStatus.Invalid, position, message, null, null);

    private sealed class Parser(string text)
    {
        private int _depth;

        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        public ScoreNode? ParseExpression(out ScoreExpressionCheck? error)
        {
            var left = ParseTerm(out error);
            if (left is null)
            {
                return null;
            }

            while (true)
            {
                SkipSpaces();
                if (AtEnd || (text[Position] != '+' && text[Position] != '-'))
                {
                    return left;
                }

                var op = text[Position] == '+' ? ScoreOperator.Add : ScoreOperator.Subtract;
                Position++;
                var right = ParseTerm(out error);
                if (right is null)
                {
                    return null;
                }

                left = new ScoreBinary(op, left, right);
            }
        }

        public void SkipSpaces()
        {
            while (!AtEnd && (text[Position] == ' ' || text[Position] == '\t'))
            {
                Position++;
            }
        }

        private ScoreNode? ParseTerm(out ScoreExpressionCheck? error)
        {
            var left = ParseFactor(out error);
            if (left is null)
            {
                return null;
            }

            while (true)
            {
                SkipSpaces();
                if (AtEnd || (text[Position] != '*' && text[Position] != '/'))
                {
                    return left;
                }

                var op = text[Position] == '*' ? ScoreOperator.Multiply : ScoreOperator.Divide;
                Position++;
                var right = ParseFactor(out error);
                if (right is null)
                {
                    return null;
                }

                left = new ScoreBinary(op, left, right);
            }
        }

        private ScoreNode? ParseFactor(out ScoreExpressionCheck? error)
        {
            SkipSpaces();
            if (AtEnd)
            {
                error = Invalid(Position, "表达式在应有操作数的位置结束。");
                return null;
            }

            if (text[Position] == '-')
            {
                if (++_depth > MaxDepth)
                {
                    error = Invalid(Position, $"表达式嵌套层数不能超过 {MaxDepth}。");
                    return null;
                }

                Position++;
                var operand = ParseFactor(out error);
                _depth--;
                return operand is null ? null : new ScoreNegation(operand);
            }

            return ParsePrimary(out error);
        }

        private ScoreNode? ParsePrimary(out ScoreExpressionCheck? error)
        {
            SkipSpaces();
            if (AtEnd)
            {
                error = Invalid(Position, "表达式在应有操作数的位置结束。");
                return null;
            }

            var current = text[Position];

            if (char.IsAsciiDigit(current))
            {
                return ParseNumber(out error);
            }

            if (current == '(')
            {
                if (++_depth > MaxDepth)
                {
                    error = Invalid(Position, $"表达式嵌套层数不能超过 {MaxDepth}。");
                    return null;
                }

                Position++;
                var inner = ParseExpression(out error);
                if (inner is null)
                {
                    _depth--;
                    return null;
                }

                SkipSpaces();
                if (AtEnd || text[Position] != ')')
                {
                    _depth--;
                    error = Invalid(Position, "表达式缺少右括号。");
                    return null;
                }

                Position++;
                _depth--;
                return inner;
            }

            if (current == '{')
            {
                return ParseVariable(out error);
            }

            if (current == ')')
            {
                error = Invalid(Position, "表达式出现多余的右括号。");
                return null;
            }

            error = Invalid(Position, $"计分表达式不允许字符“{current}”；只允许数字、四则运算符、括号和两个变量名。");
            return null;
        }

        private ScoreNode? ParseNumber(out ScoreExpressionCheck? error)
        {
            var start = Position;
            while (!AtEnd && char.IsAsciiDigit(text[Position]))
            {
                Position++;
            }

            if (!AtEnd && text[Position] == '.')
            {
                Position++;
                if (AtEnd || !char.IsAsciiDigit(text[Position]))
                {
                    error = Invalid(Position, "小数点后必须至少有一位数字。");
                    return null;
                }

                while (!AtEnd && char.IsAsciiDigit(text[Position]))
                {
                    Position++;
                }
            }

            // 不允许指数、二次小数点或千分位等写法：紧跟的数字或小数点视为非法。
            if (!AtEnd && (text[Position] == '.' || char.IsLetter(text[Position])))
            {
                error = Invalid(Position, $"“{text[Position]}”不能出现在数字之后；计分表达式只支持有限十进制常量。");
                return null;
            }

            var span = text.AsSpan(start, Position - start);
            var dot = span.IndexOf('.');
            BigInteger numerator;
            BigInteger denominator;
            if (dot < 0)
            {
                numerator = BigInteger.Parse(span, System.Globalization.CultureInfo.InvariantCulture);
                denominator = BigInteger.One;
            }
            else
            {
                var integer = span[..dot];
                var fraction = span[(dot + 1)..];
                numerator = BigInteger.Parse(integer, System.Globalization.CultureInfo.InvariantCulture)
                            * BigInteger.Pow(10, fraction.Length)
                            + BigInteger.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture);
                denominator = BigInteger.Pow(10, fraction.Length);
            }

            error = null;
            return new ScoreConstant(ExactRational.Create(numerator, denominator));
        }

        private ScoreNode? ParseVariable(out ScoreExpressionCheck? error)
        {
            var start = Position;
            var close = text.IndexOf('}', start);
            if (close < 0)
            {
                error = Invalid(start, "变量缺少右花括号。");
                return null;
            }

            var name = text[(start + 1)..close];
            Position = close + 1;

            if (Variables.Contains(name, StringComparer.Ordinal))
            {
                error = null;
                return new ScoreVariable(name);
            }

            error = new ScoreExpressionCheck(
                ScoreExpressionStatus.UnknownVariable,
                start,
                $"未知变量“{{{name}}}”；只允许 {{{CorrectOptionCountVariable}}} 和 {{{SelectedCorrectCountVariable}}}。",
                null,
                null);
            return null;
        }

        private ScoreExpressionCheck Invalid(int position, string message) =>
            new(ScoreExpressionStatus.Invalid, position, message, null, null);
    }
}
