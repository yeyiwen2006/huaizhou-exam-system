using Exam.Contracts.Importing;
using Exam.Infrastructure.Compilation;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 坐标产物 option-regions.json 的读写契约。字段名、顺序、精度与换行在此钉死，
/// B03 按同一份格式解析；契约见 docs/d03-option-region-contract.md 第 4.4 节。
/// </summary>
public sealed class OptionRegionArtifactJsonTests
{
    [Fact]
    public void Serialize_WritesContractLayoutExactly()
    {
        // 逐行给出，避免把测试文件自身的行尾带进期望值。
        var expected = string.Join('\n', [
            "{",
            "  \"artifactVersion\": 1,",
            "  \"anchorScheme\": \"pageTopLeftNormalizedV1\",",
            "  \"examId\": \"synthetic-contract\",",
            "  \"paperVersion\": 3,",
            "  \"questions\": [",
            "    {",
            "      \"questionId\": \"q-001\",",
            "      \"options\": [",
            "        {",
            "          \"optionId\": \"A\",",
            "          \"pageIndex\": 0,",
            "          \"x\": 0.223934,",
            "          \"y\": 0.189964,",
            "          \"width\": 0.01969,",
            "          \"height\": 0.0213",
            "        },",
            "        {",
            "          \"optionId\": \"B\",",
            "          \"pageIndex\": 0,",
            "          \"x\": 0.224,",
            "          \"y\": 0.22,",
            "          \"width\": 0.019,",
            "          \"height\": 0.0212",
            "        }",
            "      ]",
            "    }",
            "  ]",
            "}",
            ""
        ]);

        var json = OptionRegionArtifactJson.Serialize(Artifact());

        Assert.Equal(expected, json);
    }

    [Fact]
    public void Serialize_UsesLineFeedOnly_SoArtifactsArePlatformIndependent()
    {
        // 同一份试卷在不同操作系统上必须产出字节相同的文件，否则无法按版本比对。
        var json = OptionRegionArtifactJson.Serialize(Artifact());

        Assert.DoesNotContain('\r', json);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsEveryField()
    {
        // 集合成员是引用类型，逐字段比较用再序列化一次的结果，等价于结构比较。
        var json = OptionRegionArtifactJson.Serialize(Artifact());

        var result = OptionRegionArtifactJson.Deserialize(json);

        Assert.True(result.Succeeded);
        Assert.Equal(json, OptionRegionArtifactJson.Serialize(result.Artifact!));
    }

    [Fact]
    public void Serialize_RoundsTowardZero_KeepingEveryRegionInsideUnitSquare()
    {
        // 向零舍入不会放大任何数值，因此 x+width 与 y+height 仍不超过 1；
        // 宽度小于精度时保留原值，不能被舍成 0 而破坏「宽高为正」。
        var artifact = Artifact() with
        {
            Questions = [new OptionRegionQuestion("q-001", [
                new OptionRegion("A", 0, 0.999999999, 0.999999999, 0.0000004, 0.0000004)
            ])]
        };

        var json = OptionRegionArtifactJson.Serialize(artifact);
        var result = OptionRegionArtifactJson.Deserialize(json);

        Assert.True(result.Succeeded);
        var option = result.Artifact!.Questions[0].Options[0];
        Assert.Equal(0.999999, option.X, 9);
        Assert.True(option.Width > 0);
        Assert.True(option.X + option.Width <= 1);
        Assert.True(option.Y + option.Height <= 1);
    }

    [Fact]
    public void Deserialize_MissingQuestions_IsRejected()
    {
        var document = ValidDocument().Replace("\"questions\"", "\"questionList\"", StringComparison.Ordinal);

        AssertRejected(document, OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_WrongArtifactVersion_IsRejected()
    {
        AssertRejected(ValidDocument().Replace("\"artifactVersion\": 1", "\"artifactVersion\": 2", StringComparison.Ordinal),
            OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_WrongAnchorScheme_IsRejected()
    {
        AssertRejected(
            ValidDocument().Replace("pageTopLeftNormalizedV1", "pageTopLeftV2", StringComparison.Ordinal),
            OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_UnknownField_IsRejected()
    {
        AssertRejected(
            ValidDocument().Replace("\"examId\": \"synthetic-contract\",",
                "\"examId\": \"synthetic-contract\", \"note\": \"x\",", StringComparison.Ordinal),
            OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_DuplicateJsonProperty_IsRejected()
    {
        // JsonDocument 会静默保留后者，必须在解析前拦下。
        AssertRejected(
            ValidDocument().Replace("\"examId\": \"synthetic-contract\",",
                "\"examId\": \"synthetic-contract\", \"examId\": \"other\",", StringComparison.Ordinal),
            OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_DuplicateOptionId_IsRejected()
    {
        var option = "{\"optionId\": \"A\", \"pageIndex\": 0, \"x\": 0.1, \"y\": 0.1, \"width\": 0.1, \"height\": 0.1}";
        AssertRejected(
            ValidDocument().Replace(option, option + ", " + option, StringComparison.Ordinal),
            OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_NegativePageIndex_IsRejected()
    {
        AssertRejected(ValidDocument().Replace("\"pageIndex\": 0", "\"pageIndex\": -1", StringComparison.Ordinal),
            OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Deserialize_RegionOutsideUnitSquare_IsRejected()
    {
        AssertRejected(ValidDocument().Replace("\"width\": 0.1", "\"width\": 1.5", StringComparison.Ordinal),
            OptionRegionIssueCode.RegionOutOfBounds);
    }

    [Fact]
    public void Deserialize_NonPositiveSize_IsRejected()
    {
        AssertRejected(ValidDocument().Replace("\"height\": 0.1", "\"height\": 0", StringComparison.Ordinal),
            OptionRegionIssueCode.RegionOutOfBounds);
    }

    [Fact]
    public void Deserialize_EmptyInput_IsRejected()
    {
        AssertRejected(string.Empty, OptionRegionIssueCode.RequestInvalid);
    }

    private static void AssertRejected(string document, OptionRegionIssueCode expected)
    {
        var result = OptionRegionArtifactJson.Deserialize(document);

        Assert.False(result.Succeeded);
        Assert.Null(result.Artifact);
        Assert.Contains(result.Issues, issue => issue.Code == expected);
    }

    private static string ValidDocument() => """
        {
          "artifactVersion": 1,
          "anchorScheme": "pageTopLeftNormalizedV1",
          "examId": "synthetic-contract",
          "paperVersion": 1,
          "questions": [
            {
              "questionId": "q-001",
              "options": [
                {"optionId": "A", "pageIndex": 0, "x": 0.1, "y": 0.1, "width": 0.1, "height": 0.1}
              ]
            }
          ]
        }
        """;

    private static OptionRegionArtifact Artifact() => new(
        OptionRegionArtifact.CurrentArtifactVersion,
        OptionRegionArtifact.AnchorSchemeName,
        "synthetic-contract",
        3,
        [
            new OptionRegionQuestion("q-001", [
                new OptionRegion("A", 0, 0.223934, 0.189964, 0.019690, 0.021300),
                new OptionRegion("B", 0, 0.224000, 0.220000, 0.019000, 0.021200)
            ])
        ]);
}
