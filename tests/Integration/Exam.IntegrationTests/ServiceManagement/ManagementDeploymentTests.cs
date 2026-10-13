using System.Net;
using Exam.Management;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class ManagementDeploymentTests
{
    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("192.168.10.20", "192.168.10.20")]
    [InlineData("::", "::")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData(null, "127.0.0.1")]
    public void ManagementBindingAcceptsExplicitServerInterfaces(string? input, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), ManagementDeployment.ParseBindAddress(input));

    [Fact]
    public void BindingDoesNotMistakeAnAdvertisedUrlForAnInterface() =>
        Assert.Throws<InvalidOperationException>(() => ManagementDeployment.ParseBindAddress("https://exam.example.com"));
}
