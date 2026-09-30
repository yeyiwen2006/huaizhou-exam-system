using System.Text;
using Exam.Infrastructure.HighAvailability;

namespace Exam.UnitTests.HighAvailability;

public sealed class TimelineHistoryCoverageTests
{
    [Theory]
    [InlineData("1\t0/100\tno recovery target specified\n", 2, 1, "0/100", true)]
    [InlineData("1 0/100 after recovery\n2 0/200 next recovery\n", 3, 1, "0/101", false)]
    [InlineData("# history\n1 0/100 reason\n2 0/200 reason\n", 3, 2, "0/150", true)]
    [InlineData("1 0/100 reason\n", 3, 2, "0/50", false)]
    [InlineData("1 0/100 reason\n1 0/200 duplicate\n", 3, 1, "0/50", false)]
    [InlineData("1 0/200 reason\n2 0/100 earlier\n", 3, 1, "0/50", false)]
    [InlineData("2 0/200 same timeline\n", 2, 1, "0/50", false)]
    [InlineData("1 invalid reason\n", 2, 1, "0/50", false)]
    [InlineData("1 0/100 reason\nmalformed suffix\n", 2, 1, "0/50", false)]
    public void RejectsEarlyForkUnrelatedTimelineAndMalformedHistory(string text, int timeline, int predecessor, string lsn, bool expected)
        => Assert.Equal(expected, TimelineHistoryCoverage.Covers(Encoding.UTF8.GetBytes(text), timeline, predecessor, FileConfirmationStore.Lsn(lsn)));

    [Fact]
    public void OversizedAndInvalidEncodingAreNotEvidence()
    {
        Assert.False(TimelineHistoryCoverage.Covers(new byte[16385], 2, 1, 1));
        Assert.False(TimelineHistoryCoverage.Covers([0xff], 2, 1, 1));
    }
}
