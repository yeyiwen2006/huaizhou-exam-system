using System.Text.Json;
using System.Text.Json.Serialization;

using Exam.Contracts.Common;

namespace Exam.Contracts.Importing;

/// <summary>题组题型，取值与 schemas/exam-manifest.schema.json 的 type 枚举一致。</summary>
public enum QuestionTypeKind
{
    Single,
    Multiple,
    Indeterminate
}

/// <summary>逐题导入包中的科目条目。</summary>
public sealed record ManifestSubject(string SubjectId, string Name, string MaxScore);

/// <summary>「一个正确选项」的计分分支，多项选择题组不使用。</summary>
public sealed record ManifestOneCorrect(string Correct, string Blank, string Wrong);

/// <summary>「多个正确选项」的计分分支，单项选择题组不使用。</summary>
public sealed record ManifestMultipleCorrect(string AllCorrect, string PartialCorrect, string Blank, string Wrong);

/// <summary>题组计分分支。单项选择题组只用 OneCorrect，多项选择题组只用 MultipleCorrect，不定项两者都要。</summary>
public sealed record ManifestGroupScoring(ManifestOneCorrect? OneCorrect, ManifestMultipleCorrect? MultipleCorrect);

/// <summary>
/// 题组条目。Type 保留为字符串而不是枚举，使非法题型能定位到具体字段而不是在反序列化时丢失位置；
/// 题型判定由校验器完成。
/// </summary>
public sealed record ManifestGroup(
    string GroupId,
    string SubjectId,
    string Name,
    string? Type,
    int Order,
    int QuestionCount,
    bool AllowAbstain,
    string? MaxScore,
    ManifestGroupScoring? Scoring);

/// <summary>
/// 逐题显示元数据。只含源侧字段：编译产物（每题页数由 D02 写回、选项定位区域由 D03 发布）不写入清单。
/// </summary>
public sealed record ManifestDisplayMetadata(
    string? AnchorScheme,
    IReadOnlyList<string>? OptionCaptions,
    int? PageCount);

/// <summary>逐题条目。</summary>
public sealed record ManifestQuestion(
    string QuestionId,
    int QuestionNo,
    string SubjectId,
    string GroupId,
    string Source,
    IReadOnlyList<string>? Options,
    ManifestDisplayMetadata? DisplayMetadata);

/// <summary>考试清单，对应 schemas/exam-manifest.schema.json。</summary>
public sealed record ExamManifest(
    int SchemaVersion,
    string ExamId,
    string ExamName,
    IReadOnlyList<ManifestSubject> Subjects,
    IReadOnlyList<ManifestGroup> Groups,
    IReadOnlyList<ManifestQuestion> Questions,
    IReadOnlyList<string> ResourceRoots);

/// <summary>独立答案的一条，对应 schemas/answer-key.schema.json。仅在服务器使用。</summary>
public sealed record AnswerKeyEntry(string QuestionId, int QuestionNo, IReadOnlyList<string>? CorrectOptionIds);

/// <summary>独立答案文件。规则版本字段由 D05 追加，见 docs/import-package-spec.md。</summary>
public sealed record AnswerKeyFile(int SchemaVersion, string ExamId, IReadOnlyList<AnswerKeyEntry> Answers);

public enum ImportIssueSeverity
{
    Warning,
    Error
}

/// <summary>
/// 一条静态校验问题。Rule 是稳定规则标识（不随文案变化），Location 是 RFC 6901 JSON Pointer
/// （清单内问题指向清单，包内文件问题指向 file:&lt;包内相对路径&gt;）。
/// </summary>
public sealed record ImportIssue(ImportIssueSeverity Severity, string Rule, string Location, string Message)
{
    public static ImportIssue Error(string rule, string location, string message) =>
        new(ImportIssueSeverity.Error, rule, location, message);
}

/// <summary>静态校验结果。RequiresAnswerKeyArithmetic 表示含变量的满分求和只能拿到答案后才能核对。</summary>
public sealed record ImportValidationReport(IReadOnlyList<ImportIssue> Issues)
{
    public bool IsValid => !Issues.Any(issue => issue.Severity == ImportIssueSeverity.Error);

    public bool RequiresAnswerKeyArithmetic { get; init; }

    public IReadOnlyList<string> Rules => Issues.Select(issue => issue.Rule).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// 稳定规则标识。前 26 项与 samples/synthetic/errors/expected-errors.json 逐一对应；
/// 其余为校验器相对错误样包多出的防御性规则，见 docs/import-package-spec.md。
/// </summary>
public static class ManifestRuleIds
{
    public const string SchemaVersion = "manifest.schema.version";
    public const string UnknownField = "manifest.unknownField";
    public const string InvalidJson = "manifest.invalidJson";
    public const string RequiredMissing = "manifest.required.missing";
    public const string ExamNameInvalidFileName = "manifest.examName.invalidFileName";

    public const string SubjectDuplicateId = "subjects.duplicateId";
    public const string SubjectMaxScoreConstantRequired = "subjects.maxScore.constantRequired";
    public const string SubjectMaxScoreMismatch = "subjects.maxScore.mismatch";

    public const string GroupDuplicateId = "groups.duplicateId";
    public const string GroupUnknownSubject = "groups.unknownSubject";
    public const string GroupTypeInvalid = "groups.type.invalid";
    public const string GroupQuestionCountMismatch = "groups.questionCount.mismatch";
    public const string GroupMaxScoreConstantRequired = "groups.maxScore.constantRequired";
    public const string GroupMaxScoreMismatch = "groups.maxScore.mismatch";
    public const string GroupScoringBranchMismatch = "groups.scoring.branchMismatch";
    public const string GroupScoringBranchMissing = "groups.scoring.branchMissing";

    public const string QuestionUnknownGroup = "questions.unknownGroup";
    public const string QuestionGroupSubjectMismatch = "questions.groupSubjectMismatch";
    public const string QuestionDuplicateId = "questions.duplicateId";
    public const string QuestionDuplicateNumber = "questions.duplicateNumber";
    public const string QuestionOptionsTooFew = "questions.options.tooFew";
    public const string QuestionOptionsInvalid = "questions.options.invalid";
    public const string QuestionOptionsDuplicate = "questions.options.duplicate";
    public const string QuestionSourcePathEscapes = "questions.source.pathEscapes";
    public const string QuestionSourceFileName = "questions.source.fileName";
    public const string QuestionDisplayAnchorScheme = "questions.displayMetadata.anchorScheme";
    public const string QuestionDisplayCaptions = "questions.displayMetadata.optionCaptions";

    public const string ScoreExpressionInvalid = "rule.score.expression.invalid";
    public const string ScoreExpressionUnknownVariable = "rule.score.expression.unknownVariable";
    public const string ScoreExpressionDivisionByZero = "rule.score.expression.divisionByZero";
    public const string ScoreExpressionNonTerminatingDecimal = "rule.score.expression.nonTerminatingDecimal";

    public const string AnswerKeySchemaVersion = "answerKey.schema.version";
    public const string AnswerKeyUnknownQuestion = "rule.answerKey.unknownQuestion";
    public const string AnswerKeyQuestionNoMismatch = "rule.answerKey.questionNoMismatch";
    public const string AnswerKeyOptionNotDeclared = "rule.answerKey.optionNotDeclared";
    public const string AnswerKeyCorrectCount = "rule.answerKey.correctCount";
}

/// <summary>
/// 清单与答案的严格反序列化设置：沿用 JsonDefaults 的小驼峰与字符串枚举，并拒绝未定义字段，
/// 与 schema 的 additionalProperties:false 对齐。
/// </summary>
public static class ImportJson
{
    public static JsonSerializerOptions Create()
    {
        var options = JsonDefaults.Create();
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
