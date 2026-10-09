using System.Text.RegularExpressions;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 考生计分说明的显示分段：正文用黑体、数字用 Times New Roman。
/// 分段只决定字体，不参与任何计分计算——说明文字永远由结构化配置生成，不从预览文字反推配置。
/// 数字分段规则与学生端 <c>InstructionTextRenderer</c> 保持一致，保证考务预览与考生所见相同。
/// </summary>
public static partial class InstructionText
{
    public const string BodyFont = "SimHei";
    public const string NumberFont = "Times New Roman";

    public static IReadOnlyList<InstructionSegment> Segment(string? line)
    {
        var text = line ?? string.Empty;
        var segments = new List<InstructionSegment>();
        var offset = 0;
        foreach (Match match in NumberPattern().Matches(text))
        {
            if (match.Index > offset) segments.Add(new InstructionSegment(text[offset..match.Index], BodyFont));
            segments.Add(new InstructionSegment(match.Value, NumberFont));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) segments.Add(new InstructionSegment(text[offset..], BodyFont));
        if (segments.Count == 0) segments.Add(new InstructionSegment(text, BodyFont));
        return segments;
    }

    /// <summary>按行拆分说明文本，去掉空行与首尾空白；行内空白保留。</summary>
    public static IReadOnlyList<string> SplitLines(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0)
            .ToArray();

    [GeneratedRegex("[0-9]+(?:\\.[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}

/// <summary>一段说明文字及其字体；字体取 <see cref="InstructionText"/> 的两个常量之一。</summary>
public sealed record InstructionSegment(string Text, string FontFamily);

/// <summary>预览中的一行说明。</summary>
public sealed record InstructionLineViewModel(string Text, IReadOnlyList<InstructionSegment> Segments)
{
    public static InstructionLineViewModel From(string line)
    {
        line = line.Replace("本组允许选择“不作答”", "本题允许选择“不作答”", StringComparison.Ordinal);
        return new(line, InstructionText.Segment(line));
    }
}
