using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.UnitTests.Common;

public sealed class ContractSerializationTests
{
    [Fact]
    public void Envelope_UsesStableCamelCaseFields_AndStringEnums()
    {
        var envelope = new ApiEnvelope<CommandEventType>(
            "server-1",
            "node-a",
            9,
            "corr-1",
            CommandEventType.PaperUpdated,
            null);

        var json = JsonSerializer.Serialize(envelope, JsonDefaults.Create());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("server-1", root.GetProperty("serverId").GetString());
        Assert.Equal("node-a", root.GetProperty("nodeId").GetString());
        Assert.Equal(9, root.GetProperty("serverEpoch").GetInt64());
        Assert.Equal("paperUpdated", root.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        Assert.False(root.TryGetProperty("isSuccess", out _));
    }

    [Fact]
    public void JsonDefaults_RejectsUnexpectedPropertyCasing()
    {
        const string json = """
            {"ServerId":"server-1","nodeId":"node-a","serverEpoch":1,"correlationId":"c","result":"start","error":null}
            """;

        var result = JsonSerializer.Deserialize<ApiEnvelope<CommandEventType>>(json, JsonDefaults.Create());

        Assert.NotNull(result);
        Assert.Null(result.ServerId);
    }
}
