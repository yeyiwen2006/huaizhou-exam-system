using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Exam.Contracts.Common;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;

namespace Exam.Domain.Reporting;

public static class ReportJson
{
    public static JsonSerializerOptions Create()
    {
        var options = JsonDefaults.Create();
        options.Converters.Add(new RationalConverter());
        return options;
    }
    public static ReportCalculationResult Read(JsonElement result)
    {
        var value = result.Deserialize<ReportCalculationResult>(Create()) ??
            throw new ArgumentException("报表缺少完整结果。");
        ReportResultShapeValidator.Validate(value);
        return value;
    }
    public static ExactRational Parse(string text)
    {
        if (ScoreConstantRules.TryParse(text, out var value)) return value;
        var parts = text.Split('/');
        if (parts.Length == 2 && BigInteger.TryParse(parts[0], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var numerator) &&
            BigInteger.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
            return ExactRational.Create(numerator, denominator);
        throw new ArgumentException("报表精确分数格式无效。");
    }
    private sealed class RationalConverter : JsonConverter<ExactRational>
    {
        public override ExactRational Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("精确分数必须为字符串。");
            try { return Parse(reader.GetString()!); }
            catch (ArgumentException) { throw new JsonException("精确分数无效。"); }
        }
        public override void Write(Utf8JsonWriter writer, ExactRational value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.TryFormatFiniteDecimal(out var text) ? text :
                value.Numerator.ToString(CultureInfo.InvariantCulture) + "/" + value.Denominator.ToString(CultureInfo.InvariantCulture));
    }
}
