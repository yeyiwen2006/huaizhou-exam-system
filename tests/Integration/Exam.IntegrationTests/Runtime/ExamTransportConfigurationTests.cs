using System.Text.Json;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Exam.IntegrationTests.Runtime;

public sealed class ExamTransportConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpAndHubUseCaseSensitiveIdentityFieldsAndUtcPlusEight(bool hub)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExamServerTransport();
        using var provider = services.BuildServiceProvider();
        var options = hub
            ? provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions
            : provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        Assert.False(options.PropertyNameCaseInsensitive);
        Assert.Equal("own-device", JsonSerializer.Deserialize<DeviceIdentity>("{\"deviceId\":\"own-device\"}", options)!.DeviceId);
        Assert.Null(JsonSerializer.Deserialize<DeviceIdentity>("{\"DeviceId\":\"other-device\"}", options)!.DeviceId);
        using var timestamp = JsonDocument.Parse(JsonSerializer.Serialize(DateTimeOffset.UnixEpoch, options));
        Assert.Equal(TimeSpan.FromHours(8), DateTimeOffset.Parse(timestamp.RootElement.GetString()!,
            System.Globalization.CultureInfo.InvariantCulture).Offset);
    }

    public sealed record DeviceIdentity(string? DeviceId);
}
