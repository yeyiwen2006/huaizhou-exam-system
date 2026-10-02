using Exam.Admin.ViewModels;
using Exam.Contracts.Activation;

namespace Exam.AdminUnitTests;

public sealed class ActivationDisplayTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" abc123 ", "ABC123")]
    [InlineData("synthetic-request-ab12cd34", "AB12CD34")]
    public void BothClientsUseTheSameNonSecretRequestLocator(string? requestId, string expected)
    {
        Assert.Equal(expected, ActivationDisplay.RequestCode(requestId));
        if (requestId is not null)
            Assert.Equal(expected, new ActivationPendingRow(requestId, "synthetic-device", "10:00:00").VerificationCode);
    }

    [Fact]
    public void BusinessConflictPreservesReadableReasonWithoutRenderingTechnicalDetails()
    {
        Assert.Contains("空试场", Exam.Admin.Services.FriendlyErrors.Describe("VERSION_CONFLICT", "该试场仍有设备，只有空试场可以删除。"), StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", Exam.Admin.Services.FriendlyErrors.Describe("VERSION_CONFLICT", "SQL Exception at synthetic-stack"), StringComparison.Ordinal);
    }
}
