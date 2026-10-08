using System.Globalization;

namespace Exam.Student.Core;

public static class ExamCountdown
{
    public static string FormatRemaining(TimeSpan remaining)
    {
        var ticks = Math.Max(0, remaining.Ticks);
        var minutes = ticks / TimeSpan.TicksPerMinute;
        if (ticks % TimeSpan.TicksPerMinute != 0) minutes++;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:00}:{minutes % 60:00}");
    }
}
