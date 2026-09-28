using System.Text.Json;
using System.Text.RegularExpressions;

using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Importing;

/// <summary>
/// schema 与 C# 契约的对齐核对：字段名、题型枚举、变量白名单、坐标规范常量，以及所有对象 schema 是否
/// 禁止未定义字段。仓库不引入 JSON Schema 校验库，schema 是对外契约、校验器是实现权威，这些断言守住
/// 两者不漂移。
/// 单独发布的 schema 与清单内嵌定义是否逐项一致，由同目录的 SchemaConsistencyTests 负责，此处不重复。
/// </summary>
public sealed class ContractSchemaAlignmentTests
{
    private static readonly JsonDocument ManifestDocument = ReadSchema("exam-manifest.schema.json");
    private static readonly JsonDocument AnswerKeyDocument = ReadSchema("answer-key.schema.json");
    private static readonly JsonDocument ScoringDocument = ReadSchema("scoring-rules.schema.json");
    private static readonly JsonDocument DisplayDocument = ReadSchema("display-metadata.schema.json");
    private static readonly JsonDocument OptionRegionDocument = ReadSchema("option-regions.schema.json");

    private static JsonElement ManifestSchema => ManifestDocument.RootElement;

    [Fact]
    public void RootRequiredFields_MatchManifestContract()
    {
        Assert.Equal(
            PropertyNamesOf<ExamManifest>(),
            RequiredOf(ManifestSchema));
    }

    [Fact]
    public void SubjectShape_MatchesManifestContract()
    {
        // 科目在清单里是内联形状（题组与题目用 $defs 引用），因此直接读 subjects.items。
        Assert.Equal(
            PropertyNamesOf<ManifestSubject>(),
            PropertyNamesOf(ManifestSchema.GetProperty("properties").GetProperty("subjects").GetProperty("items")));
    }

    [Fact]
    public void GroupShape_MatchesManifestContract()
    {
        Assert.Equal(
            PropertyNamesOf<ManifestGroup>(),
            PropertyNamesOf(ManifestSchema.GetProperty("$defs").GetProperty("group")));
    }

    [Fact]
    public void QuestionShape_MatchesManifestContract()
    {
        Assert.Equal(
            PropertyNamesOf<ManifestQuestion>(),
            PropertyNamesOf(ManifestSchema.GetProperty("$defs").GetProperty("question")));
    }

    [Fact]
    public void ScoringBranchShapes_MatchManifestContracts()
    {
        var definitions = ManifestSchema.GetProperty("$defs");

        Assert.Equal(
            PropertyNamesOf<ManifestOneCorrect>(),
            PropertyNamesOf(definitions.GetProperty("oneCorrect")));
        Assert.Equal(
            PropertyNamesOf<ManifestMultipleCorrect>(),
            PropertyNamesOf(definitions.GetProperty("multipleCorrect")));
    }

    [Fact]
    public void AnswerKeyShapes_MatchContracts()
    {
        Assert.Equal(
            PropertyNamesOf<AnswerKeyFile>(),
            PropertyNamesOf(AnswerKeyDocument.RootElement));
        Assert.Equal(
            PropertyNamesOf<AnswerKeyEntry>(),
            PropertyNamesOf(AnswerKeyDocument.RootElement.GetProperty("properties").GetProperty("answers").GetProperty("items")));
    }

    [Fact]
    public void QuestionTypeEnum_MatchesQuestionTypeKind()
    {
        var declared = ManifestSchema.GetProperty("$defs").GetProperty("group")
            .GetProperty("properties").GetProperty("type")
            .GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToList();

        var expected = Enum.GetValues<QuestionTypeKind>()
            .Select(value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()))
            .ToList();

        Assert.Equal(expected, declared);
    }

    [Fact]
    public void ScoreExpressionVariables_MatchParserWhitelist()
    {
        var declared = Regex.Matches(ScoreExpressionPattern(), @"\\\{([^\\{}]+)\\\}")
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var expected = ScoreExpressionParser.Variables
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, declared);
    }

    [Fact]
    public void DisplayMetadataShape_MatchesContract()
    {
        var shape = ManifestSchema.GetProperty("$defs").GetProperty("displayMetadata");

        Assert.Equal(PropertyNamesOf<ManifestDisplayMetadata>(), PropertyNamesOf(shape));
        Assert.Equal(
            ExamManifestValidator.AnchorScheme,
            shape.GetProperty("properties").GetProperty("anchorScheme").GetProperty("const").GetString());
    }

    [Fact]
    public void OptionRegionShapes_MatchContracts()
    {
        var root = OptionRegionDocument.RootElement;
        var definitions = root.GetProperty("$defs");

        // 根字段即全部必填字段：产物没有可选字段，缺一不可。
        Assert.Equal(PropertyNamesOf<OptionRegionArtifact>(), RequiredOf(root));
        Assert.Equal(PropertyNamesOf<OptionRegionQuestion>(), PropertyNamesOf(definitions.GetProperty("question")));
        Assert.Equal(PropertyNamesOf<OptionRegion>(), PropertyNamesOf(definitions.GetProperty("option")));
    }

    [Fact]
    public void OptionRegionConstants_MatchContractConstants()
    {
        var root = OptionRegionDocument.RootElement;
        var option = root.GetProperty("$defs").GetProperty("option").GetProperty("properties");

        Assert.Equal(
            OptionRegionArtifact.CurrentArtifactVersion,
            root.GetProperty("properties").GetProperty("artifactVersion").GetProperty("const").GetInt32());
        Assert.Equal(
            OptionRegionArtifact.AnchorSchemeName,
            root.GetProperty("properties").GetProperty("anchorScheme").GetProperty("const").GetString());

        // 页索引为题内从 0 开始；宽高必须为正，坐标取值不超过 1。
        Assert.Equal(0, option.GetProperty("pageIndex").GetProperty("minimum").GetInt32());
        foreach (var name in new[] { "width", "height" })
        {
            var bounds = root.GetProperty("$defs").GetProperty(
                option.GetProperty(name).GetProperty("$ref").GetString()!.Split('/')[^1]);
            Assert.Equal(0, bounds.GetProperty("exclusiveMinimum").GetInt32());
            Assert.Equal(1, bounds.GetProperty("maximum").GetInt32());
        }
    }

    [Fact]
    public void EveryObjectSchema_ForbidsAdditionalProperties()
    {
        var missing = new List<string>();
        Walk(ManifestSchema, "#", missing);
        Walk(ScoringDocument.RootElement, "#", missing);
        Walk(DisplayDocument.RootElement, "#", missing);
        Walk(OptionRegionDocument.RootElement, "#", missing);
        Walk(AnswerKeyDocument.RootElement, "#", missing);

        Assert.True(missing.Count == 0, $"以下对象 schema 未禁止未定义字段：{string.Join(", ", missing)}");
    }

    private static JsonDocument ReadSchema(string fileName) =>
        JsonDocument.Parse(TestRepositoryPaths.ReadText("schemas", fileName));

    private static string ScoreExpressionPattern() =>
        ManifestSchema.GetProperty("$defs").GetProperty("scoreExpression").GetProperty("pattern").GetString()!;

    private static List<string> PropertyNamesOf<T>() =>
        typeof(T).GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static List<string> PropertyNamesOf(JsonElement shape) =>
        shape.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static List<string> RequiredOf(JsonElement shape) =>
        shape.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static void Walk(JsonElement element, string pointer, List<string> missing)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "object"
                    && (!element.TryGetProperty("additionalProperties", out var additional)
                        || additional.ValueKind != JsonValueKind.False))
                {
                    missing.Add(pointer);
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{pointer}/{property.Name}", missing);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{pointer}/{index++}", missing);
                }

                break;
        }
    }
}
