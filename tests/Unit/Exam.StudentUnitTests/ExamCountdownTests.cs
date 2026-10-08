using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class ExamCountdownTests
{
    [Theory]
    [InlineData(120, "00:02")]
    [InlineData(119, "00:02")]
    [InlineData(60, "00:01")]
    [InlineData(59, "00:01")]
    [InlineData(1, "00:01")]
    [InlineData(0, "00:00")]
    [InlineData(-1, "00:00")]
    [InlineData(3600, "01:00")]
    [InlineData(3601, "01:01")]
    public void RemainingMinutes_RoundUpUntilDeadline(int seconds, string expected) =>
        Assert.Equal(expected, ExamCountdown.FormatRemaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FractionalSecondBeforeDeadline_StillShowsOneMinute() =>
        Assert.Equal("00:01", ExamCountdown.FormatRemaining(TimeSpan.FromTicks(1)));
}
