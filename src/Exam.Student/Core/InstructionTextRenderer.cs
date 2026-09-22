using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Exam.Student.Core;

/// <summary>仅改变考试说明的字体，不从说明文字反推计分规则。</summary>
public static partial class InstructionTextRenderer
{
    public static TextBlock Create(string line, Style? style = null)
    {
        var block = new TextBlock
        {
            Style = style,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("SimHei"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var offset = 0;
        foreach (Match match in NumberPattern().Matches(line))
        {
            if (match.Index > offset)
            {
                block.Inlines.Add(new Run(line[offset..match.Index]));
            }

            block.Inlines.Add(new Run(match.Value) { FontFamily = new FontFamily("Times New Roman") });
            offset = match.Index + match.Length;
        }

        if (offset < line.Length)
        {
            block.Inlines.Add(new Run(line[offset..]));
        }

        return block;
    }

    [GeneratedRegex("[0-9]+(?:\\.[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
