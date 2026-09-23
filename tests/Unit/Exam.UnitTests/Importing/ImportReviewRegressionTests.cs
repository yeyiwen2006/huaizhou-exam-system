using System.Text.Json;
using System.Text.Json.Nodes;

using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Importing;

/// <summary>PR #44 审查回归：从公开 schema 生成坏输入，不只复述实现的常量。</summary>
public sealed class ImportReviewRegressionTests
{
    private static readonly ExamManifestValidator Validator = new();

    private const string ManifestJson = """
        {
          "schemaVersion": 1, "examId": "review", "examName": "审查合成考试",
          "subjects": [{"subjectId": "s", "name": "合成科目", "maxScore": "1"}],
          "groups": [{
            "groupId": "g", "subjectId": "s", "name": "合成题组", "type": "single",
            "order": 1, "questionCount": 1, "allowAbstain": false, "maxScore": "1",
            "scoring": {"oneCorrect": {"correct": "1", "blank": "0", "wrong": "0"}}
          }],
          "questions": [{
            "questionId": "q", "questionNo": 1, "subjectId": "s", "groupId": "g",
            "source": "questions/审查合成考试_1.tex", "options": ["A", "B", "C", "D"],
            "displayMetadata": {
              "anchorScheme": "pageTopLeftNormalizedV1", "pageCount": 1,
              "optionCaptions": ["A.", "B.", "C.", "D."]
            }
          }],
          "resourceRoots": ["styles"]
        }
        """;

    private const string AnswerJson = """
        {"schemaVersion": 1, "examId": "review",
         "answers": [{"questionId": "q", "questionNo": 1, "correctOptionIds": ["A"]}]}
        """;

    public static TheoryData<bool, string, string, int> SchemaMutations
    {
        get
        {
            var data = new TheoryData<bool, string, string, int>();
            foreach (var answer in new[] { false, true })
            {
                var schema = JsonNode.Parse(TestRepositoryPaths.ReadText("schemas",
                    answer ? "answer-key.schema.json" : "exam-manifest.schema.json"))!;
                Walk(schema, schema, JsonNode.Parse(answer ? AnswerJson : ManifestJson)!, "", answer, data);
            }
            var manifestSchema = JsonNode.Parse(TestRepositoryPaths.ReadText("schemas", "exam-manifest.schema.json"))!;
            Walk(manifestSchema, manifestSchema["$defs"]!["multipleCorrect"]!, MultipleBranch(),
                "/groups/0/scoring/multipleCorrect", false, data);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SchemaMutations))]
    public void SchemaDeclaredConstraint_IsEnforcedAtItsPointer(bool answer, string pointer, string mutation, int limit)
    {
        var document = JsonNode.Parse(answer ? AnswerJson : ManifestJson)!;
        if (pointer.StartsWith("/groups/0/scoring/multipleCorrect", StringComparison.Ordinal))
        {
            document["groups"]![0]!["type"] = "indeterminate";
            document["groups"]![0]!["scoring"]!["multipleCorrect"] = MultipleBranch();
        }
        var value = At(document, pointer);
        JsonNode? replacement = mutation switch
        {
            "null" => null,
            "missing" => null,
            "shortString" => JsonValue.Create(""),
            "longString" => JsonValue.Create(new string('x', limit + 1)),
            "lowInteger" => JsonValue.Create(limit - 1),
            "highInteger" => JsonValue.Create(limit + 1),
            "quotedInteger" => JsonValue.Create(value!.ToJsonString()),
            "wrongType" => value is JsonValue v && v.TryGetValue<string>(out _) ? JsonValue.Create(7) : JsonValue.Create("bad"),
            "shortArray" => new JsonArray(),
            "longArray" => new JsonArray(Enumerable.Range(0, limit + 1).Select(i => (JsonNode?)JsonValue.Create($"O{i}")).ToArray()),
            "duplicateItem" => new JsonArray(value!.AsArray().Select(x => x!.DeepClone()).Append(value![0]!.DeepClone()).ToArray()),
            "emptyObject" => new JsonObject(),
            _ => throw new InvalidOperationException(mutation)
        };
        Set(document, pointer, replacement, mutation == "missing");
        var report = answer
            ? Validator.ValidateJson(ManifestJson, document.ToJsonString())
            : Validator.ValidateJson(document.ToJsonString());
        var expectedPointer = pointer == "/resourceRoots" && mutation == "duplicateItem"
            ? pointer + "/" + (replacement!.AsArray().Count - 1)
            : pointer;
        AssertIssue(report, expectedPointer);
    }

    [Theory]
    [InlineData("subjects")]
    [InlineData("groups")]
    [InlineData("questions")]
    public void TypedEntry_NullCollectionOrElement_ReturnsIssueInsteadOfThrowing(string field)
    {
        var manifest = ReadManifest();
        var empty = field switch
        {
            "subjects" => manifest with { Subjects = null! },
            "groups" => manifest with { Groups = null! },
            _ => manifest with { Questions = null! }
        };
        AssertIssue(Validator.Validate(empty), "/" + field);
        var item = field switch
        {
            "subjects" => manifest with { Subjects = [null!] },
            "groups" => manifest with { Groups = [null!] },
            _ => manifest with { Questions = [null!] }
        };
        AssertIssue(Validator.Validate(item), "/" + field + "/0");
    }

    [Fact]
    public void TypedEntry_NullGroupIdentifierAndAnswerCollection_AreRejected()
    {
        var manifest = ReadManifest();
        AssertIssue(Validator.Validate(manifest with { Groups = [manifest.Groups[0] with { GroupId = null! }] }), "/groups/0/groupId");
        AssertIssue(Validator.Validate(manifest, ReadAnswer() with { Answers = null! }), "/answers");
        AssertIssue(Validator.Validate(manifest, ReadAnswer() with { Answers = [null!] }), "/answers/0");
    }

    [Fact]
    public void TypedEntry_OptionalNullsAndMissingAnswer_RemainSupported()
    {
        var manifest = ReadManifest();
        manifest = manifest with
        {
            Groups = [manifest.Groups[0] with { MaxScore = null }],
            Questions = [manifest.Questions[0] with { DisplayMetadata = null }]
        };
        var report = Validator.Validate(manifest);
        Assert.True(report.IsValid, Describe(report));
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    [InlineData("/outside")]
    [InlineData("styles/.. /outside")]
    [InlineData("styles/aux")]
    [InlineData("styles/COM¹")]
    [InlineData("styles/a:stream")]
    [InlineData("styles/a\u0000b")]
    public void ResourceRoot_InvalidPath_IsRejectedWithoutFileAccess(string path)
    {
        var manifest = ReadManifest() with { ResourceRoots = [path] };
        AssertIssue(Validator.Validate(manifest), "/resourceRoots/0");
    }

    [Theory]
    [InlineData("styles")]
    [InlineData("./styles/")]
    [InlineData("STYLES")]
    public void ResourceRoot_DuplicateWindowsPath_IsRejected(string second)
    {
        AssertIssue(Validator.Validate(ReadManifest() with { ResourceRoots = ["styles", second] }), "/resourceRoots/1");
    }

    [Theory]
    [InlineData("aux")]
    [InlineData(".. ")]
    [InlineData("a:stream")]
    [InlineData("bad\u0000dir")]
    public void SourceDirectory_InvalidWindowsSegment_IsRejected(string directory)
    {
        var manifest = ReadManifest();
        var question = manifest.Questions[0] with { Source = $"{directory}/审查合成考试_1.tex" };
        AssertIssue(Validator.Validate(manifest with { Questions = [question] }), "/questions/0/source");
    }

    [Fact]
    public void AnswerKey_WrongExam_IsRejectedAndCannotCompleteArithmetic()
    {
        var report = Validator.Validate(ReadManifest(), ReadAnswer() with { ExamId = "another-exam" });
        AssertIssue(report, "/examId", "rule.answerKey.examIdMismatch");
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void AnswerKey_DuplicateQuestion_IsRejectedInsteadOfFirstWinning(string second)
    {
        var answer = ReadAnswer();
        var report = Validator.Validate(ReadManifest(), answer with
        {
            Answers = [answer.Answers[0], answer.Answers[0] with { CorrectOptionIds = [second] }]
        });
        AssertIssue(report, "/answers/1/questionId", "rule.answerKey.duplicateQuestion");
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    [Fact]
    public void AnswerKey_DuplicateOptions_CannotMasqueradeAsMultipleCorrect()
    {
        var manifest = ReadManifest();
        var group = manifest.Groups[0] with
        {
            Type = "multiple",
            Scoring = new ManifestGroupScoring(null, new ManifestMultipleCorrect("1", "0", "0", "0"))
        };
        var answer = ReadAnswer() with { Answers = [new AnswerKeyEntry("q", 1, ["A", "A"])] };
        AssertIssue(Validator.Validate(manifest with { Groups = [group] }, answer), "/answers/0/correctOptionIds");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void SuppliedEmptyAnswerDocument_IsNotTreatedAsOmitted(string text)
    {
        AssertIssue(Validator.ValidateJson(ManifestJson, text), "", ManifestRuleIds.InvalidJson);
    }

    [Fact]
    public void PartialAnswerKey_IsAllowedButFullScoreRemainsUnverified()
    {
        var manifest = TwoQuestions();
        var report = Validator.Validate(manifest, ReadAnswer());
        Assert.True(report.IsValid, Describe(report));
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    [Fact]
    public void InvalidAnswer_DoesNotProduceMisleadingTotalMismatch()
    {
        var manifest = ReadManifest();
        var group = manifest.Groups[0] with
        {
            Type = "indeterminate",
            Scoring = new ManifestGroupScoring(new ManifestOneCorrect("1", "0", "0"),
                new ManifestMultipleCorrect("5", "0", "0", "0"))
        };
        var answer = ReadAnswer() with { Answers = [new AnswerKeyEntry("q", 1, ["A", "unknown"])] };
        var report = Validator.Validate(manifest with { Groups = [group] }, answer);
        AssertIssue(report, "/answers/0/correctOptionIds", ManifestRuleIds.AnswerKeyOptionNotDeclared);
        Assert.DoesNotContain(ManifestRuleIds.SubjectMaxScoreMismatch, report.Rules);
        Assert.DoesNotContain(ManifestRuleIds.GroupMaxScoreMismatch, report.Rules);
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    [Fact]
    public void FullScore_KnownAnswerDivisionByZero_IsAnErrorNotDeferredSuccess()
    {
        var manifest = ReadManifest();
        var group = manifest.Groups[0] with
        {
            Scoring = new ManifestGroupScoring(new ManifestOneCorrect("1/({正确选项总数}-1)", "0", "0"), null)
        };
        AssertIssue(Validator.Validate(manifest with { Groups = [group] }, ReadAnswer()),
            "/groups/0/scoring/oneCorrect/correct", ManifestRuleIds.ScoreExpressionDivisionByZero);
    }

    [Fact]
    public void FullScore_NonFinitePerQuestion_CannotBeHiddenByFiniteSum()
    {
        var manifest = ReadManifest();
        var group = manifest.Groups[0] with
        {
            QuestionCount = 3,
            Scoring = new ManifestGroupScoring(new ManifestOneCorrect("{正确选项总数}/3", "0", "0"), null)
        };
        var questions = Enumerable.Range(1, 3).Select(i => manifest.Questions[0] with
        {
            QuestionId = $"q{i}", QuestionNo = i, Source = $"questions/审查合成考试_{i}.tex"
        }).ToArray();
        var answer = ReadAnswer() with { Answers = questions.Select(q => new AnswerKeyEntry(q.QuestionId, q.QuestionNo, ["A"])).ToArray() };
        var report = Validator.Validate(manifest with { Groups = [group], Questions = questions }, answer);
        AssertIssue(report, "/groups/0/scoring/oneCorrect/correct", ManifestRuleIds.ScoreExpressionNonTerminatingDecimal);
    }

    [Fact]
    public void FullScore_IntermediateFractionCancellation_RemainsExact()
    {
        var manifest = ReadManifest();
        var group = manifest.Groups[0] with
        {
            Scoring = new ManifestGroupScoring(new ManifestOneCorrect("({正确选项总数}/3)*3", "0", "0"), null)
        };
        var report = Validator.Validate(manifest with { Groups = [group] }, ReadAnswer());
        Assert.True(report.IsValid, Describe(report));
        Assert.False(report.RequiresAnswerKeyArithmetic);
    }

    [Theory]
    [InlineData("\"examId\": \"review\"", "/examId")]
    [InlineData("\"order\": 1", "/groups/0/order")]
    [InlineData("\"correct\": \"1\"", "/groups/0/scoring/oneCorrect/correct")]
    public void DuplicateJsonProperty_IsRejected(string property, string pointer)
    {
        AssertIssue(Validator.ValidateJson(ManifestJson.Replace(property, property + ", " + property, StringComparison.Ordinal)), pointer);
    }

    [Fact]
    public void DuplicateAnswerProperty_IsRejected()
    {
        AssertIssue(Validator.ValidateJson(ManifestJson, AnswerJson.Replace("\"questionNo\": 1", "\"questionNo\": 2, \"questionNo\": 1", StringComparison.Ordinal)), "/answers/0/questionNo");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("0")]
    [InlineData("{invalid")]
    public void RootError_UsesRfc6901EmptyPointer(string json)
    {
        AssertIssue(Validator.ValidateJson(json), "", ManifestRuleIds.InvalidJson);
    }

    [Fact]
    public void UnknownProperty_EscapesPointerTokens()
    {
        var document = JsonNode.Parse(ManifestJson)!;
        document["questions"]![0]!["a~/b"] = 1;
        AssertIssue(Validator.ValidateJson(document.ToJsonString()), "/questions/0/a~0~1b", ManifestRuleIds.UnknownField);
    }

    [Fact]
    public void AnswerVersionPointer_IsRelativeToAnswerDocument()
    {
        AssertIssue(Validator.Validate(ReadManifest(), ReadAnswer() with { SchemaVersion = 2 }), "/schemaVersion", ManifestRuleIds.AnswerKeySchemaVersion);
    }

    [Theory]
    [InlineData("--1")]
    [InlineData("1+--2")]
    [InlineData("- -{正确选项总数}")]
    public void RepeatedUnaryMinus_DoesNotExceedDocumentedGrammar(string expression)
    {
        Assert.Equal(ScoreExpressionStatus.Invalid, ScoreExpressionParser.Check(expression).Status);
    }

    [Fact]
    public void LeadingDotFilename_IsNotAReservedDeviceName()
    {
        Assert.True(WindowsFileNameRules.IsValidFileName(".合成考试"));
        Assert.False(WindowsFileNameRules.IsValidFileName("COM¹"));
        Assert.False(WindowsFileNameRules.IsValidFileName("lpt².txt"));
    }

    [Fact]
    public void JsonStringLength_CountsUnicodeScalarsRatherThanUtf16Units()
    {
        var manifest = ReadManifest();
        manifest = manifest with { Subjects = [manifest.Subjects[0] with { Name = string.Concat(Enumerable.Repeat("𠀀", 64)) }] };
        Assert.True(Validator.Validate(manifest).IsValid);
    }

    [Fact]
    public void ImportSerialization_DoesNotInheritWebQuotedNumberCoercion()
    {
        var json = ManifestJson.Replace("\"order\": 1", "\"order\": \"1\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExamManifest>(json, ImportJson.Create()));
    }

    [Fact]
    public void EscapedDuplicateProperty_IsStillTheSameProperty()
    {
        var json = ManifestJson.Replace("\"examId\": \"review\"",
            "\"examId\": \"review\", \"exa\\u006dId\": \"review\"", StringComparison.Ordinal);
        AssertIssue(Validator.ValidateJson(json), "/examId", ManifestRuleIds.DuplicateField);
    }

    [Theory]
    [InlineData("-(-1)", "1")]
    [InlineData("1--2", "3")]
    public void NegativePrimaryAndBinarySubtraction_StillWork(string expression, string expected)
    {
        var check = ScoreExpressionParser.Check(expression);
        Assert.True(check.IsValid, check.Message);
        Assert.Equal(expected, check.ConstantValue!.ToString());
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("10e-1")]
    [InlineData("0.001e3")]
    [InlineData("1.00000000000000000000000000000000000000")]
    public void JsonSchemaInteger_AcceptsExactIntegralNumberRepresentations(string number)
    {
        var json = ManifestJson.Replace("\"order\": 1", "\"order\": " + number, StringComparison.Ordinal);
        var report = Validator.ValidateJson(json, AnswerJson);
        Assert.True(report.IsValid, Describe(report));
        Assert.False(report.RequiresAnswerKeyArithmetic);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e-1")]
    [InlineData("1.00000000000000000000000000000000000001")]
    [InlineData("0.99999999999999999999999999999999999999")]
    [InlineData("1e99999999999999999999")]
    [InlineData("1e-99999999999999999999")]
    public void JsonSchemaInteger_RejectsNonIntegersWithoutRounding(string number)
    {
        var json = ManifestJson.Replace("\"order\": 1", "\"order\": " + number, StringComparison.Ordinal);
        AssertIssue(Validator.ValidateJson(json), "/groups/0/order", ManifestRuleIds.InvalidJson);
    }

    [Theory]
    [InlineData("-2147483648.0", int.MinValue)]
    [InlineData("2147483647e0", int.MaxValue)]
    [InlineData("0e999999999999999999999", 0)]
    [InlineData("-0.000e-99999999999999999999", 0)]
    public void IntegerConverter_HandlesInt32AndZeroExponentEdgesExactly(string json, int expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<int>(json, ImportJson.Create()));
    }

    [Theory]
    [InlineData("{\"examId\":\"\\uD800\"}")]
    [InlineData("{\"examId\":\"\\uDC00\"}")]
    [InlineData("{\"\\uD800\":1}")]
    public void InvalidUnicodeEscape_ReturnsIssueInsteadOfThrowing(string json)
    {
        var report = Validator.ValidateJson(json);
        Assert.False(report.IsValid);
        Assert.Contains(ManifestRuleIds.InvalidJson, report.Rules);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void TypedEntry_InvalidUnicode_IsNotSilentlyReplaced(int surrogate)
    {
        var manifest = ReadManifest() with { ExamId = new string((char)surrogate, 1) };
        var report = Validator.Validate(manifest);
        Assert.False(report.IsValid);
        Assert.Contains(ManifestRuleIds.InvalidJson, report.Rules);
    }

    private static JsonNode MultipleBranch() => JsonNode.Parse(
        """{"allCorrect":"1","partialCorrect":"0","blank":"0","wrong":"0"}""")!;

    private static void Walk(JsonNode root, JsonNode schema, JsonNode value, string pointer, bool answer,
        TheoryData<bool, string, string, int> data)
    {
        if (schema["$ref"] is { } reference)
            schema = At(root, reference.GetValue<string>()[1..])!;
        if (pointer.Length > 0) data.Add(answer, pointer, "null", 0);
        if (schema["type"]?.GetValue<string>() == "integer") data.Add(answer, pointer, "quotedInteger", 0);
        if (value is JsonValue) data.Add(answer, pointer, "wrongType", 0);
        foreach (var (key, mutation) in new[]
        {
            ("minLength", "shortString"), ("maxLength", "longString"),
            ("minimum", "lowInteger"), ("maximum", "highInteger"),
            ("minItems", "shortArray"), ("maxItems", "longArray"), ("minProperties", "emptyObject")
        })
        {
            if (schema[key] is { } limit && limit.GetValue<int>() > 0)
                data.Add(answer, pointer, mutation, limit.GetValue<int>());
        }
        if (schema["uniqueItems"]?.GetValue<bool>() == true && value is JsonArray { Count: > 0 })
            data.Add(answer, pointer, "duplicateItem", 0);
        if (schema["required"] is JsonArray required)
            foreach (var property in required)
                data.Add(answer, pointer + "/" + property!.GetValue<string>(), "missing", 0);
        if (value is JsonObject obj && schema["properties"] is JsonObject properties)
            foreach (var (name, child) in obj)
                Walk(root, properties[name]!, child!, pointer + "/" + name, answer, data);
        if (value is JsonArray array && schema["items"] is { } itemSchema)
            for (var i = 0; i < array.Count; i++)
                Walk(root, itemSchema, array[i]!, pointer + "/" + i, answer, data);
    }

    private static JsonNode? At(JsonNode document, string pointer)
    {
        JsonNode? node = document;
        foreach (var token in pointer.Split('/').Skip(1))
            node = node is JsonArray array ? array[int.Parse(token)] : node![token];
        return node;
    }

    private static void Set(JsonNode document, string pointer, JsonNode? value, bool remove)
    {
        var slash = pointer.LastIndexOf('/');
        var parent = At(document, pointer[..slash])!;
        var token = pointer[(slash + 1)..];
        if (parent is JsonArray array) array[int.Parse(token)] = value;
        else if (remove) parent.AsObject().Remove(token);
        else parent[token] = value;
    }

    private static ExamManifest ReadManifest() => JsonSerializer.Deserialize<ExamManifest>(ManifestJson, ImportJson.Create())!;
    private static AnswerKeyFile ReadAnswer() => JsonSerializer.Deserialize<AnswerKeyFile>(AnswerJson, ImportJson.Create())!;
    private static ExamManifest TwoQuestions()
    {
        var manifest = ReadManifest();
        return manifest with
        {
            Subjects = [manifest.Subjects[0] with { MaxScore = "2" }],
            Groups = [manifest.Groups[0] with { QuestionCount = 2, MaxScore = "2" }],
            Questions = [manifest.Questions[0], manifest.Questions[0] with { QuestionId = "q2", QuestionNo = 2, Source = "questions/审查合成考试_2.tex" }]
        };
    }
    private static void AssertIssue(ImportValidationReport report, string pointer, string? rule = null)
    {
        Assert.False(report.IsValid, "坏输入被接受");
        Assert.True(report.Issues.Any(x => x.Location == pointer && (rule is null || x.Rule == rule)), $"期望 {rule}@{pointer}；{Describe(report)}");
    }
    private static string Describe(ImportValidationReport report) => string.Join(" | ", report.Issues.Select(x => $"{x.Rule}@{x.Location}: {x.Message}"));
}
