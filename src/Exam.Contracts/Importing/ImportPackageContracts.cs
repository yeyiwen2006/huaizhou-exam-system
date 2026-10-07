using System.Globalization;
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
/// 逐题显示元数据。PageCount 是作者期望值，依赖与编译检查 编译后核对并写回；选项区域与题包 的选项定位区域不写入清单。
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

/// <summary>独立答案文件。与规则发布信息分开保存，见 docs/import-package-spec.md。</summary>
public sealed record AnswerKeyFile(int SchemaVersion, string ExamId, IReadOnlyList<AnswerKeyEntry> Answers);

public enum ImportIssueSeverity
{
    Warning,
    Error
}

/// <summary>
/// 一条静态校验问题。Rule 是稳定规则标识（不随文案变化），Location 是 RFC 6901 JSON Pointer
/// （清单与独立答案分别相对其 JSON 根，根指针为空字符串；未来包内文件问题使用 file:&lt;包内相对路径&gt;）。
/// </summary>
public sealed record ImportIssue(ImportIssueSeverity Severity, string Rule, string Location, string Message)
{
    public static ImportIssue Error(string rule, string location, string message) =>
        new(ImportIssueSeverity.Error, rule, location, message);
}

/// <summary>
/// 静态校验结果。RequiresAnswerKeyArithmetic 表示逐题满分核对尚未全部完成（如答案未齐或无效）。
/// 调用方必须先检查 IsValid；该标志为 false 也不等于已通过 精确计分 的发布前全部作答情形校验。
/// </summary>
public sealed record ImportValidationReport(IReadOnlyList<ImportIssue> Issues)
{
    public bool IsValid => !Issues.Any(issue => issue.Severity == ImportIssueSeverity.Error);

    public bool RequiresAnswerKeyArithmetic { get; init; }

    public IReadOnlyList<string> Rules => Issues.Select(issue => issue.Rule).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// 稳定规则标识。覆盖 samples/synthetic/errors/expected-errors.json 的 26 个错误样包；
/// 其余为校验器相对错误样包多出的防御性规则，见 docs/import-package-spec.md。
/// </summary>
public static class ManifestRuleIds
{
    public const string SchemaVersion = "manifest.schema.version";
    public const string SchemaConstraint = "manifest.schema.constraint";
    public const string DuplicateField = "manifest.duplicateField";
    public const string ResourceRootInvalid = "manifest.resourceRoots.invalid";
    public const string ResourceRootDuplicate = "manifest.resourceRoots.duplicate";
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

    public const string AnswerKeyExamIdMismatch = "rule.answerKey.examIdMismatch";
    public const string AnswerKeyDuplicateQuestion = "rule.answerKey.duplicateQuestion";
    public const string AnswerKeyOptionsDuplicate = "rule.answerKey.optionsDuplicate";
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
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new ExactInt32Converter());
        options.Converters.Add(new WellFormedStringConverter());
        // 可选字段的 null 表示未声明，序列化时省略；输入中的显式 null 由结构扫描拒绝。
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }

    // 防止直接构造 DTO 时，编码器把非法 UTF-16 悄悄替换成 U+FFFD，改变标识或让标识发生碰撞。
    private sealed class WellFormedStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                return reader.GetString();
            }
            catch (InvalidOperationException exception)
            {
                throw new JsonException("字段必须是有效的 Unicode 字符串。", exception);
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                        throw new JsonException("字符串含不成对的 Unicode 高代理项。");
                    i++;
                }
                else if (char.IsLowSurrogate(value[i]))
                {
                    throw new JsonException("字符串含不成对的 Unicode 低代理项。");
                }
            }
            writer.WriteStringValue(value);
        }
    }

    /// <summary>
    /// JSON Schema 的 integer 按数值判定：1、1.0、1e0 都是整数，而 "1" 不是。
    /// 不经过 double/decimal 舍入，避免把极接近整数的非整数误判为整数。
    /// </summary>
    private sealed class ExactInt32Converter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException("字段必须是 JSON 整数值，不接受带引号的数字。");
            if (reader.TryGetInt32(out var value))
                return value;

            using var document = JsonDocument.ParseValue(ref reader);
            if (TryReadInteger(document.RootElement.GetRawText(), out value))
                return value;
            throw new JsonException("字段必须是精确的 32 位整数值。");
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);

        private static bool TryReadInteger(string raw, out int value)
        {
            value = 0;
            var exponentIndex = raw.IndexOfAny(['e', 'E']);
            var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
            var negative = mantissa[0] == '-';
            var dot = mantissa.IndexOf('.');
            var fractionLength = dot < 0 ? 0 : mantissa.Length - dot - 1;
            var digits = mantissa.TrimStart('-').Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
            if (digits.Length == 0)
                return true; // 零乘任意十次幂仍为零，不能因指数很大而误拒。

            var exponent = 0;
            if (exponentIndex >= 0 && !int.TryParse(raw.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                return false;
            var shift = (long)exponent - fractionLength;
            if (shift < 0)
            {
                if (-shift >= digits.Length)
                    return false;
                var keep = digits.Length + (int)shift;
                for (var i = keep; i < digits.Length; i++)
                    if (digits[i] != '0') return false;
                digits = digits[..keep];
                shift = 0;
            }
            if (digits.Length + shift > 10)
                return false;
            var integer = (negative ? "-" : string.Empty) + digits + new string('0', (int)shift);
            return int.TryParse(integer, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
    }
}
