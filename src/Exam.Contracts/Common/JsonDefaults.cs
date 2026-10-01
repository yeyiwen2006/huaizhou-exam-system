using System.Text.Json;
using System.Text.Json.Serialization;

namespace Exam.Contracts.Common;

public static class JsonDefaults
{
    /// <summary>
    /// 面向学生端、考务端的 HTTP / SignalR 时间统一以 UTC+8 写出。
    /// 只用于传输边界；内部文件与数据库相关序列化继续使用 Create。
    /// </summary>
    public static JsonSerializerOptions CreateServerTransport()
    {
        var options = Create();
        options.Converters.Add(new ServerTimeJsonConverter());
        return options;
    }

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class ServerTimeJsonConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToOffset(TimeSpan.FromHours(8)));
    }
}
