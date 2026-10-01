using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.UnitTests;

public sealed class ServerTimeSerializationTests
{
    [Fact]
    public void TransportWritesUtc8AndReadsUtcWithoutChangingTheInstant()
    {
        var instant = new DateTimeOffset(2026, 10, 1, 20, 30, 45, TimeSpan.Zero).AddTicks(1234);
        var value = new Schedule(instant, instant.AddHours(2).ToOffset(TimeSpan.FromHours(-5)), null);
        var options = JsonDefaults.CreateServerTransport();
        var text = JsonSerializer.Serialize(value, options);
        using var document = JsonDocument.Parse(text);
        var start = document.RootElement.GetProperty("startsAt").GetDateTimeOffset();
        var end = document.RootElement.GetProperty("endsAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromHours(8), start.Offset);
        Assert.Equal(TimeSpan.FromHours(8), end.Offset);
        Assert.Equal(2, start.Day);
        Assert.Equal(4, start.Hour);
        Assert.Equal(value.StartsAt, start);
        Assert.Equal(value.EndsAt, end);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("changedAt").ValueKind);
        var read = JsonSerializer.Deserialize<Schedule>(text, options)!;
        Assert.Equal(TimeSpan.Zero, read.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, read.EndsAt.Offset);
        Assert.Equal(value, read);

        // 内部持久化仍用原序列化器，不能因显示时区改动重写事件、文件或签名的时间表示。
        var stored = JsonSerializer.Serialize(value, JsonDefaults.Create());
        using var storedDocument = JsonDocument.Parse(stored);
        Assert.Equal(TimeSpan.Zero, storedDocument.RootElement.GetProperty("startsAt").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.FromHours(-5), storedDocument.RootElement.GetProperty("endsAt").GetDateTimeOffset().Offset);
    }

    private sealed record Schedule(DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTimeOffset? ChangedAt);
}
