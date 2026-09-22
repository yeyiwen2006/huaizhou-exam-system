using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 针对错误样包未覆盖的规则做定向校验，并核对防御性规则与满分标志的行为。
/// 错误样包本身由 SamplePackageTests 逐用例覆盖。
/// </summary>
public sealed class ManifestValidatorTests
{
    private static readonly ExamManifestValidator Validator = new();

    [Fact]
    public void Validate_DefaultManifest_IsValid()
    {
        var report = Validator.Validate(Manifest(), AnswerKey());

        Assert.True(report.IsValid, string.Join(" | ", report.Issues.Select(issue => issue.Message)));
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Validate_WithoutAnswerKey_StillAcceptsManifestAndFlagsScoreCheck()
    {
        var report = Validator.Validate(Manifest());

        Assert.True(report.IsValid);
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    [Fact]
    public void Validate_WithAnswerKey_DoesNotFlagScoreCheck()
    {
        var report = Validator.Validate(Manifest(), AnswerKey());

        Assert.False(report.RequiresAnswerKeyArithmetic);
    }

    [Fact]
    public void Validate_BlankExamId_ReportsRequiredMissing()
    {
        var report = Validator.Validate(Manifest() with { ExamId = " " });

        Assert.Contains(ManifestRuleIds.RequiredMissing, report.Rules);
        Assert.Contains("/examId", report.Issues.Select(issue => issue.Location));
    }

    [Fact]
    public void Validate_DuplicateSubjectId_IsRejected()
    {
        var report = Validator.Validate(Manifest(subjects: [Subject(), Subject()]));

        Assert.Contains(ManifestRuleIds.SubjectDuplicateId, report.Rules);
    }

    [Fact]
    public void Validate_DuplicateGroupId_IsRejected()
    {
        var report = Validator.Validate(Manifest(groups: [Group(), Group()]));

        Assert.Contains(ManifestRuleIds.GroupDuplicateId, report.Rules);
    }

    [Fact]
    public void Validate_BlankOptionIdentifier_IsRejected()
    {
        var report = Validator.Validate(Manifest(questions: [Question(options: ["A", " "])]));

        Assert.Contains(ManifestRuleIds.QuestionOptionsInvalid, report.Rules);
    }

    [Fact]
    public void Validate_UnknownAnchorScheme_IsRejected()
    {
        var question = Question(display: new ManifestDisplayMetadata("pageBottomNormalizedV2", null, null));

        var report = Validator.Validate(Manifest(questions: [question]));

        Assert.Contains(ManifestRuleIds.QuestionDisplayAnchorScheme, report.Rules);
    }

    [Fact]
    public void Validate_KnownAnchorScheme_IsAccepted()
    {
        var question = Question(display: new ManifestDisplayMetadata(ExamManifestValidator.AnchorScheme, null, null));

        var report = Validator.Validate(Manifest(questions: [question]));

        Assert.True(report.IsValid);
    }

    [Fact]
    public void Validate_OptionCaptionsNotMatchingOptions_IsRejected()
    {
        var question = Question(display: new ManifestDisplayMetadata(null, ["A.", "B."], null));

        var report = Validator.Validate(Manifest(questions: [question]));

        Assert.Contains(ManifestRuleIds.QuestionDisplayCaptions, report.Rules);
    }

    [Fact]
    public void Validate_GroupMaxScoreMismatch_IsRejected()
    {
        // 单题满分 1，题组却声明 2。
        var report = Validator.Validate(Manifest(groups: [Group(maxScore: "2")]), AnswerKey());

        Assert.Contains(ManifestRuleIds.GroupMaxScoreMismatch, report.Rules);
    }

    [Fact]
    public void Validate_SubjectMaxScoreNotConstant_IsRejected()
    {
        var report = Validator.Validate(Manifest(subjects: [Subject(maxScore: "{正确选项总数}")]));

        Assert.Contains(ManifestRuleIds.SubjectMaxScoreConstantRequired, report.Rules);
    }

    [Fact]
    public void Validate_VariableScoreRule_ComputesFullScoreFromAnswerKey()
    {
        // 每题满分 = {正确选项总数}；两题的正确选项数分别为 2 和 3，科目满分必须等于 5。
        var groups = new[]
        {
            new ManifestGroup(
                "group-1",
                "subject-1",
                "一、不定项选择题",
                "indeterminate",
                1,
                2,
                false,
                "5",
                new ManifestGroupScoring(
                    new ManifestOneCorrect("1", "0", "0"),
                    new ManifestMultipleCorrect("{正确选项总数}", "{选择的正确选项数}", "0", "0")))
        };

        var questions = new[]
        {
            Question(id: "q-001", no: 1),
            Question(id: "q-002", no: 2, source: "questions/合成校验考试_2.tex")
        };

        var answerKey = new AnswerKeyFile(
            1,
            "synthetic-validator",
            [
                new AnswerKeyEntry("q-001", 1, ["A", "B"]),
                new AnswerKeyEntry("q-002", 2, ["A", "B", "C"])
            ]);

        var report = Validator.Validate(
            Manifest(subjects: [Subject(maxScore: "5")], groups: groups, questions: questions),
            answerKey);

        Assert.True(report.IsValid, string.Join(" | ", report.Issues.Select(issue => issue.Message)));
        Assert.False(report.RequiresAnswerKeyArithmetic);
    }

    [Fact]
    public void Validate_VariableScoreRuleWithWrongSubjectMaxScore_IsRejected()
    {
        var groups = new[]
        {
            new ManifestGroup(
                "group-1",
                "subject-1",
                "一、不定项选择题",
                "indeterminate",
                1,
                1,
                false,
                null,
                new ManifestGroupScoring(
                    new ManifestOneCorrect("1", "0", "0"),
                    new ManifestMultipleCorrect("{正确选项总数}", "{选择的正确选项数}", "0", "0")))
        };

        var answerKey = new AnswerKeyFile(1, "synthetic-validator", [new AnswerKeyEntry("q-001", 1, ["A", "B"])]);

        // 该题正确选项数为 2，逐题满分 2，科目却声明 3。
        var report = Validator.Validate(
            Manifest(subjects: [Subject(maxScore: "3")], groups: groups, questions: [Question()]),
            answerKey);

        Assert.Contains(ManifestRuleIds.SubjectMaxScoreMismatch, report.Rules);
    }

    [Fact]
    public void Validate_AnswerKeySchemaVersionUnsupported_IsRejected()
    {
        var answerKey = AnswerKey() with { SchemaVersion = 2 };

        var report = Validator.Validate(Manifest(), answerKey);

        Assert.Contains(ManifestRuleIds.AnswerKeySchemaVersion, report.Rules);
    }

    [Fact]
    public void ValidateJson_NotJson_ReportsInvalidJson()
    {
        var report = Validator.ValidateJson("{ 这不是 JSON");

        Assert.Contains(ManifestRuleIds.InvalidJson, report.Rules);
    }

    [Fact]
    public void ValidateJson_EmptyContent_ReportsInvalidJson()
    {
        var report = Validator.ValidateJson("   ");

        Assert.Contains(ManifestRuleIds.InvalidJson, report.Rules);
    }

    [Fact]
    public void ValidateJson_WrongFieldType_ReportsInvalidJsonWithLocation()
    {
        var json = ValidSampleJson().Replace("\"questionCount\": 1", "\"questionCount\": \"一\"", StringComparison.Ordinal);

        var report = Validator.ValidateJson(json);

        Assert.Contains(ManifestRuleIds.InvalidJson, report.Rules);
        Assert.Contains(
            report.Issues,
            issue => issue.Rule == ManifestRuleIds.InvalidJson && issue.Location.Contains("questionCount", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateJson_ValidSample_IsAccepted()
    {
        var report = Validator.ValidateJson(ValidSampleJson(), ValidSampleAnswerKeyJson());

        Assert.True(report.IsValid, string.Join(" | ", report.Issues.Select(issue => issue.Message)));
    }

    private static string ValidSampleJson() =>
        """
        {
          "schemaVersion": 1,
          "examId": "synthetic-validator",
          "examName": "合成校验考试",
          "subjects": [{ "subjectId": "subject-1", "name": "科目甲", "maxScore": "1" }],
          "groups": [
            {
              "groupId": "group-1",
              "subjectId": "subject-1",
              "name": "一、单项选择题",
              "type": "single",
              "order": 1,
              "questionCount": 1,
              "maxScore": "1",
              "scoring": { "oneCorrect": { "correct": "1", "blank": "0", "wrong": "0" } }
            }
          ],
          "questions": [
            {
              "questionId": "q-001",
              "questionNo": 1,
              "subjectId": "subject-1",
              "groupId": "group-1",
              "source": "questions/合成校验考试_1.tex",
              "options": ["A", "B", "C", "D"]
            }
          ],
          "resourceRoots": []
        }
        """;

    private static string ValidSampleAnswerKeyJson() =>
        """
        {
          "schemaVersion": 1,
          "examId": "synthetic-validator",
          "answers": [{ "questionId": "q-001", "questionNo": 1, "correctOptionIds": ["A"] }]
        }
        """;

    private static ExamManifest Manifest(
        IReadOnlyList<ManifestSubject>? subjects = null,
        IReadOnlyList<ManifestGroup>? groups = null,
        IReadOnlyList<ManifestQuestion>? questions = null,
        string examName = "合成校验考试",
        int schemaVersion = 1) =>
        new(
            schemaVersion,
            "synthetic-validator",
            examName,
            subjects ?? [Subject()],
            groups ?? [Group()],
            questions ?? [Question()],
            []);

    private static ManifestSubject Subject(string id = "subject-1", string maxScore = "1") =>
        new(id, "科目甲", maxScore);

    private static ManifestGroup Group(
        string id = "group-1",
        string? type = "single",
        int questionCount = 1,
        string? maxScore = "1",
        string subjectId = "subject-1") =>
        new(
            id,
            subjectId,
            "一、单项选择题",
            type,
            1,
            questionCount,
            false,
            maxScore,
            new ManifestGroupScoring(new ManifestOneCorrect("1", "0", "0"), null));

    private static ManifestQuestion Question(
        string id = "q-001",
        int no = 1,
        string groupId = "group-1",
        string subjectId = "subject-1",
        string? source = null,
        IReadOnlyList<string>? options = null,
        ManifestDisplayMetadata? display = null) =>
        new(
            id,
            no,
            subjectId,
            groupId,
            source ?? $"questions/合成校验考试_{no}.tex",
            options ?? ["A", "B", "C", "D"],
            display);

    private static AnswerKeyFile AnswerKey() =>
        new(1, "synthetic-validator", [new AnswerKeyEntry("q-001", 1, ["A"])]);
}
