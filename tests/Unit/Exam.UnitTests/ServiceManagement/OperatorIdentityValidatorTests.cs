using System.Text.Json;
using Exam.Application.Security;

namespace Exam.UnitTests.ServiceManagement;

public sealed class OperatorIdentityValidatorTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "operator")]
    [InlineData("short", "operator")]
    [InlineData("12345678901234567890123456789012", " ")]
    public void MissingOrInvalidConfigurationNeverAuthenticates(string? token, string? actor) =>
        Assert.False(OperatorIdentityValidator.Authenticate("Bearer " + token, token, actor));

    [Theory]
    [InlineData("{\"actorId\":\"fake\"}")]
    [InlineData("{\"ActorId\":\"fake\"}")]
    [InlineData("{\"nested\":{\"operatorId\":\"fake\"}}")]
    [InlineData("{\"actorId\":\"operator\",\"actorId\":\"fake\"}")]
    public void ForgedAndDuplicateActorsAreRejected(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.False(OperatorIdentityValidator.HasMatchingActor(document.RootElement, "operator"));
    }

    [Fact]
    public void ValidIdentityIsIndependentOfDatabase()
    {
        var token = new string('x', 32);
        Assert.True(OperatorIdentityValidator.Authenticate("Bearer " + token, token, "operator"));
        Assert.False(OperatorIdentityValidator.Authenticate("Bearer " + new string('y', 32), token, "operator"));
        using var document = JsonDocument.Parse("{\"actorId\":\"operator\"}");
        Assert.True(OperatorIdentityValidator.HasMatchingActor(document.RootElement, "operator"));
    }
}
