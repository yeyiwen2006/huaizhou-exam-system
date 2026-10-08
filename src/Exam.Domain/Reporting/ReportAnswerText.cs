using System.Text.RegularExpressions;

namespace Exam.Domain.Reporting;

public static partial class ReportAnswerText
{
    // Printed option captions may contain separators (A.B.); report answers use AB.
    // Limit normalization to option letters so other answer text keeps its meaning.
    public static string Format(string text) => OptionLetters().IsMatch(text)
        ? text.Replace(".", string.Empty).Replace("．", string.Empty)
        : text;

    [GeneratedRegex(@"\A(?:[A-Z][.．]?)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionLetters();
}
