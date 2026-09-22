using System.Text.Json;

using Exam.Contracts.Importing;
using Exam.Domain.Scoring;

namespace Exam.Domain.Importing;

/// <summary>
/// 逐题包清单与独立答案的静态校验。只做纯逻辑判断，不读文件、不联网、不编译；
/// 源文件是否存在、字体宏包图片依赖与编译属 D02，选项定位区域属 D03。
/// 规则标识与 samples/synthetic/errors/expected-errors.json 逐一对应，
/// 定位使用 RFC 6901 JSON Pointer。字段语义见 docs/import-package-spec.md。
/// </summary>
public sealed class ExamManifestValidator
{
    /// <summary>坐标规范名称，与 schemas/display-metadata.schema.json 的 anchorScheme 一致。</summary>
    public const string AnchorScheme = "pageTopLeftNormalizedV1";

    public ImportValidationReport ValidateJson(string? manifestJson, string? answerKeyJson = null)
    {
        var issues = new List<ImportIssue>();

        if (!TryReadManifest(manifestJson, issues, out var manifest))
        {
            return new ImportValidationReport(issues);
        }

        if (!TryReadAnswerKey(answerKeyJson, issues, out var answerKey))
        {
            return new ImportValidationReport(issues);
        }

        return new ImportValidationReport(issues)
        {
            RequiresAnswerKeyArithmetic = ValidateCore(manifest, answerKey, issues)
        };
    }

    public ImportValidationReport Validate(ExamManifest? manifest, AnswerKeyFile? answerKey = null)
    {
        var issues = new List<ImportIssue>();

        if (manifest is null)
        {
            issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, "/", "清单为空。"));
            return new ImportValidationReport(issues);
        }

        return new ImportValidationReport(issues)
        {
            RequiresAnswerKeyArithmetic = ValidateCore(manifest, answerKey, issues)
        };
    }

    private static bool ValidateCore(ExamManifest manifest, AnswerKeyFile? answerKey, List<ImportIssue> issues)
    {
        if (manifest.SchemaVersion != 1)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.SchemaVersion,
                "/schemaVersion",
                $"不支持的清单 schemaVersion：{manifest.SchemaVersion}；当前只支持 1。"));
        }

        ValidateRequiredFields(manifest, issues);
        ValidateExamName(manifest, issues);
        ValidateSubjects(manifest, issues);
        ValidateGroups(manifest, issues);
        ValidateQuestions(manifest, issues);
        var requiresAnswerArithmetic = ValidateFullScores(manifest, answerKey, issues);
        ValidateAnswerKey(manifest, answerKey, issues);
        return requiresAnswerArithmetic;
    }

    private static void ValidateRequiredFields(ExamManifest manifest, List<ImportIssue> issues)
    {
        Required(!string.IsNullOrWhiteSpace(manifest.ExamId), "/examId", "examId", issues);
        Required(!string.IsNullOrWhiteSpace(manifest.ExamName), "/examName", "examName", issues);

        for (var i = 0; i < manifest.Subjects.Count; i++)
        {
            var subject = manifest.Subjects[i];
            Required(!string.IsNullOrWhiteSpace(subject.SubjectId), $"/subjects/{i}/subjectId", "subjectId", issues);
            Required(!string.IsNullOrWhiteSpace(subject.Name), $"/subjects/{i}/name", "name", issues);
            Required(!string.IsNullOrWhiteSpace(subject.MaxScore), $"/subjects/{i}/maxScore", "maxScore", issues);
        }

        for (var i = 0; i < manifest.Groups.Count; i++)
        {
            var group = manifest.Groups[i];
            Required(!string.IsNullOrWhiteSpace(group.GroupId), $"/groups/{i}/groupId", "groupId", issues);
            Required(!string.IsNullOrWhiteSpace(group.SubjectId), $"/groups/{i}/subjectId", "subjectId", issues);
            Required(!string.IsNullOrWhiteSpace(group.Name), $"/groups/{i}/name", "name", issues);
            Required(!string.IsNullOrWhiteSpace(group.Type), $"/groups/{i}/type", "type", issues);
            Required(group.Order >= 1, $"/groups/{i}/order", "order", issues);
            Required(group.QuestionCount >= 1, $"/groups/{i}/questionCount", "questionCount", issues);
            Required(group.Scoring is not null, $"/groups/{i}/scoring", "scoring", issues);
        }

        for (var i = 0; i < manifest.Questions.Count; i++)
        {
            var question = manifest.Questions[i];
            Required(!string.IsNullOrWhiteSpace(question.QuestionId), $"/questions/{i}/questionId", "questionId", issues);
            Required(question.QuestionNo >= 1, $"/questions/{i}/questionNo", "questionNo", issues);
            Required(!string.IsNullOrWhiteSpace(question.SubjectId), $"/questions/{i}/subjectId", "subjectId", issues);
            Required(!string.IsNullOrWhiteSpace(question.GroupId), $"/questions/{i}/groupId", "groupId", issues);
            Required(!string.IsNullOrWhiteSpace(question.Source), $"/questions/{i}/source", "source", issues);
            Required(question.Options is { Count: > 0 }, $"/questions/{i}/options", "options", issues);
        }
    }

    private static void ValidateExamName(ExamManifest manifest, List<ImportIssue> issues)
    {
        var reason = WindowsFileNameRules.Describe(manifest.ExamName);
        if (reason is not null)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.ExamNameInvalidFileName,
                "/examName",
                $"考试名称不能用作 Windows 文件名，逐题文件名以它为前缀：{reason}"));
        }
    }

    private static void ValidateSubjects(ExamManifest manifest, List<ImportIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Subjects.Count; i++)
        {
            var subject = manifest.Subjects[i];
            if (!string.IsNullOrWhiteSpace(subject.SubjectId) && !seen.Add(subject.SubjectId))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.SubjectDuplicateId,
                    $"/subjects/{i}/subjectId",
                    $"科目标识重复：{subject.SubjectId}。"));
            }

            if (!string.IsNullOrWhiteSpace(subject.MaxScore) && !ScoreConstantRules.IsValid(subject.MaxScore))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.SubjectMaxScoreConstantRequired,
                    $"/subjects/{i}/maxScore",
                    $"科目满分必须是有限十进制常量，不能用变量或运算符：{subject.MaxScore}。"));
            }
        }
    }

    private static void ValidateGroups(ExamManifest manifest, List<ImportIssue> issues)
    {
        var subjectIds = manifest.Subjects
            .Select(subject => subject.SubjectId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var seenGroups = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < manifest.Groups.Count; i++)
        {
            var group = manifest.Groups[i];
            var pointer = $"/groups/{i}";

            if (!string.IsNullOrWhiteSpace(group.GroupId) && !seenGroups.Add(group.GroupId))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.GroupDuplicateId, $"{pointer}/groupId", $"题组标识重复：{group.GroupId}。"));
            }

            if (!string.IsNullOrWhiteSpace(group.SubjectId) && !subjectIds.Contains(group.SubjectId))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.GroupUnknownSubject,
                    $"{pointer}/subjectId",
                    $"题组引用了不存在的科目：{group.SubjectId}。"));
            }

            var type = ParseType(group.Type);
            if (type is null)
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.GroupTypeInvalid,
                    $"{pointer}/type",
                    $"题组题型只能是 single、multiple 或 indeterminate，当前为：{group.Type ?? "(缺失)"}。"));
            }
            else
            {
                ValidateScoringBranches(group, type.Value, pointer, issues);
                ValidateScoringExpressions(group, pointer, issues);
            }

            if (!string.IsNullOrWhiteSpace(group.MaxScore) && !ScoreConstantRules.IsValid(group.MaxScore))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.GroupMaxScoreConstantRequired,
                    $"{pointer}/maxScore",
                    $"题组满分必须是有限十进制常量，不能用变量或运算符：{group.MaxScore}。"));
            }
        }
    }

    private static void ValidateScoringBranches(
        ManifestGroup group,
        QuestionTypeKind type,
        string pointer,
        List<ImportIssue> issues)
    {
        var hasOne = group.Scoring?.OneCorrect is not null;
        var hasMultiple = group.Scoring?.MultipleCorrect is not null;
        var scoringPointer = $"{pointer}/scoring";

        var forbiddenPresent = type switch
        {
            QuestionTypeKind.Single => hasMultiple,
            QuestionTypeKind.Multiple => hasOne,
            _ => false
        };

        if (forbiddenPresent)
        {
            var extra = type == QuestionTypeKind.Single ? "multipleCorrect" : "oneCorrect";
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.GroupScoringBranchMismatch,
                scoringPointer,
                type == QuestionTypeKind.Single
                    ? $"单项选择题组不能使用 {extra} 分支。"
                    : $"多项选择题组不能使用 {extra} 分支。"));
            return;
        }

        var missing = type switch
        {
            QuestionTypeKind.Single => hasOne ? null : "oneCorrect",
            QuestionTypeKind.Multiple => hasMultiple ? null : "multipleCorrect",
            _ when !hasOne && !hasMultiple => "oneCorrect 与 multipleCorrect",
            _ when !hasOne => "oneCorrect",
            _ when !hasMultiple => "multipleCorrect",
            _ => null
        };

        if (missing is not null)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.GroupScoringBranchMissing,
                scoringPointer,
                $"题组必须提供 {missing} 分支。"));
        }
    }

    private static void ValidateScoringExpressions(ManifestGroup group, string pointer, List<ImportIssue> issues)
    {
        if (group.Scoring?.OneCorrect is { } one)
        {
            CheckExpression($"{pointer}/scoring/oneCorrect/correct", one.Correct, issues);
            CheckExpression($"{pointer}/scoring/oneCorrect/blank", one.Blank, issues);
            CheckExpression($"{pointer}/scoring/oneCorrect/wrong", one.Wrong, issues);
        }

        if (group.Scoring?.MultipleCorrect is { } multiple)
        {
            CheckExpression($"{pointer}/scoring/multipleCorrect/allCorrect", multiple.AllCorrect, issues);
            CheckExpression($"{pointer}/scoring/multipleCorrect/partialCorrect", multiple.PartialCorrect, issues);
            CheckExpression($"{pointer}/scoring/multipleCorrect/blank", multiple.Blank, issues);
            CheckExpression($"{pointer}/scoring/multipleCorrect/wrong", multiple.Wrong, issues);
        }
    }

    private static void CheckExpression(string pointer, string? expression, List<ImportIssue> issues)
    {
        var check = ScoreExpressionParser.Check(expression);
        if (check.IsValid)
        {
            return;
        }

        var rule = check.Status switch
        {
            ScoreExpressionStatus.UnknownVariable => ManifestRuleIds.ScoreExpressionUnknownVariable,
            ScoreExpressionStatus.DivisionByZero => ManifestRuleIds.ScoreExpressionDivisionByZero,
            ScoreExpressionStatus.NonTerminatingDecimal => ManifestRuleIds.ScoreExpressionNonTerminatingDecimal,
            _ => ManifestRuleIds.ScoreExpressionInvalid
        };

        issues.Add(ImportIssue.Error(rule, pointer, check.Message));
    }

    private static void ValidateQuestions(ExamManifest manifest, List<ImportIssue> issues)
    {
        var groupsById = manifest.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group.GroupId))
            .GroupBy(group => group.GroupId, StringComparer.Ordinal)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First(), StringComparer.Ordinal);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNumbers = new HashSet<int>();
        var countedByGroup = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < manifest.Questions.Count; i++)
        {
            var question = manifest.Questions[i];
            var pointer = $"/questions/{i}";

            if (!string.IsNullOrWhiteSpace(question.QuestionId) && !seenIds.Add(question.QuestionId))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.QuestionDuplicateId,
                    $"{pointer}/questionId",
                    $"题目标识重复：{question.QuestionId}。"));
            }

            if (question.QuestionNo >= 1 && !seenNumbers.Add(question.QuestionNo))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.QuestionDuplicateNumber,
                    $"{pointer}/questionNo",
                    $"全卷题号重复：{question.QuestionNo}。"));
            }

            var group = default(ManifestGroup);
            if (!string.IsNullOrWhiteSpace(question.GroupId))
            {
                if (!groupsById.TryGetValue(question.GroupId, out group))
                {
                    issues.Add(ImportIssue.Error(
                        ManifestRuleIds.QuestionUnknownGroup,
                        $"{pointer}/groupId",
                        $"题目引用了不存在的题组：{question.GroupId}。"));
                }
                else
                {
                    countedByGroup[question.GroupId] = countedByGroup.GetValueOrDefault(question.GroupId) + 1;
                    if (group.SubjectId != question.SubjectId)
                    {
                        issues.Add(ImportIssue.Error(
                            ManifestRuleIds.QuestionGroupSubjectMismatch,
                            $"{pointer}/subjectId",
                            $"题目科目 {question.SubjectId} 与所属题组的科目 {group.SubjectId} 不一致。"));
                    }
                }
            }

            ValidateOptions(question, pointer, issues);
            ValidateSource(manifest, question, pointer, issues);
            ValidateDisplayMetadata(question, pointer, issues);
        }

        for (var i = 0; i < manifest.Groups.Count; i++)
        {
            var group = manifest.Groups[i];
            var actual = countedByGroup.GetValueOrDefault(group.GroupId);
            if (group.QuestionCount != actual)
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.GroupQuestionCountMismatch,
                    $"/groups/{i}/questionCount",
                    $"题组声明的题量 {group.QuestionCount} 与清单中归属该组的题目数 {actual} 不一致。"));
            }
        }
    }

    private static void ValidateOptions(ManifestQuestion question, string pointer, List<ImportIssue> issues)
    {
        var options = question.Options;
        if (options is null || options.Count < 2)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionOptionsTooFew,
                $"{pointer}/options",
                $"题目至少需要两个选项，当前为 {options?.Count ?? 0} 个。"));
            return;
        }

        if (options.Any(string.IsNullOrWhiteSpace))
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionOptionsInvalid,
                $"{pointer}/options",
                "选项标识不能为空或只由空白字符组成。"));
        }

        if (options.Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionOptionsDuplicate,
                $"{pointer}/options",
                "选项标识不能重复。"));
        }
    }

    private static void ValidateSource(ExamManifest manifest, ManifestQuestion question, string pointer, List<ImportIssue> issues)
    {
        var sourcePointer = $"{pointer}/source";

        if (string.IsNullOrWhiteSpace(question.Source))
        {
            return;
        }

        if (!PackagePathRules.IsRelativeAndInside(question.Source) || question.Source.Length > PackagePathRules.MaxLength)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionSourcePathEscapes,
                sourcePointer,
                $"题目源文件必须是包内相对路径，不能使用盘符、绝对路径或 .. 逃出包根目录：{question.Source}。"));
            return;
        }

        var fileName = PackagePathRules.Normalize(question.Source).Split('/')[^1];
        var nameReason = WindowsFileNameRules.Describe(fileName);
        if (nameReason is not null)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionSourceFileName,
                sourcePointer,
                $"题目源文件名 {fileName} 不能用作 Windows 文件名：{nameReason}"));
            return;
        }

        if (!QuestionSourceFileName.TryParse(question.Source, out var prefix, out var questionNo))
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionSourceFileName,
                sourcePointer,
                $"题目源文件名必须形如「考试名称_题号.tex」，以最后一个下划线后的数字为题号：{fileName}。"));
            return;
        }

        if (!string.Equals(prefix, manifest.ExamName, StringComparison.Ordinal))
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionSourceFileName,
                sourcePointer,
                $"题目源文件名前缀 {prefix} 与清单的考试名称 {manifest.ExamName} 不一致。"));
            return;
        }

        if (questionNo != question.QuestionNo)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionSourceFileName,
                sourcePointer,
                $"题目源文件名给出的题号 {questionNo} 与清单题号 {question.QuestionNo} 不一致。"));
        }
    }

    private static void ValidateDisplayMetadata(ManifestQuestion question, string pointer, List<ImportIssue> issues)
    {
        if (question.DisplayMetadata is not { } metadata)
        {
            return;
        }

        if (metadata.AnchorScheme is { Length: > 0 } scheme && !string.Equals(scheme, AnchorScheme, StringComparison.Ordinal))
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionDisplayAnchorScheme,
                $"{pointer}/displayMetadata/anchorScheme",
                $"坐标规范只能是 {AnchorScheme}，当前为：{scheme}。"));
        }

        if (metadata.OptionCaptions is { } captions
            && question.Options is { } options
            && captions.Count != options.Count)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.QuestionDisplayCaptions,
                $"{pointer}/displayMetadata/optionCaptions",
                $"显示文本必须与选项一一对应：选项 {options.Count} 个，显示文本 {captions.Count} 个。"));
        }
    }

    /// <summary>满分求和。返回值表示是否有满分只能拿到答案后才能核对。</summary>
    private static bool ValidateFullScores(ExamManifest manifest, AnswerKeyFile? answerKey, List<ImportIssue> issues)
    {
        var correctCounts = answerKey?.Answers
            .Where(answer => !string.IsNullOrWhiteSpace(answer.QuestionId))
            .GroupBy(answer => answer.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.First().CorrectOptionIds?.Count ?? 0,
                StringComparer.Ordinal);

        var groupTotals = new Dictionary<string, (ExactRational Total, bool Complete)>(StringComparer.Ordinal);

        for (var index = 0; index < manifest.Groups.Count; index++)
        {
            var group = manifest.Groups[index];
            var total = ExactRational.Zero;
            var complete = true;

            foreach (var question in manifest.Questions.Where(question => question.GroupId == group.GroupId))
            {
                int? correctCount = correctCounts is not null
                                    && correctCounts.TryGetValue(question.QuestionId, out var count)
                    ? count
                    : null;

                if (correctCount is not { } count2 || !TryQuestionFullScore(group, count2, out var fullScore))
                {
                    complete = false;
                    continue;
                }

                total += fullScore;
            }

            groupTotals[group.GroupId] = (total, complete);

            if (!complete
                || string.IsNullOrWhiteSpace(group.MaxScore)
                || !ScoreConstantRules.TryParse(group.MaxScore, out var declaredGroupMax))
            {
                continue;
            }

            if (declaredGroupMax != total)
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.GroupMaxScoreMismatch,
                    $"/groups/{index}/maxScore",
                    $"题组满分 {group.MaxScore} 与逐题满分之和 {total} 不一致。"));
            }
        }

        var requiresAnswerArithmetic = groupTotals.Values.Any(value => !value.Complete);

        for (var i = 0; i < manifest.Subjects.Count; i++)
        {
            var subject = manifest.Subjects[i];
            if (string.IsNullOrWhiteSpace(subject.MaxScore)
                || !ScoreConstantRules.TryParse(subject.MaxScore, out var declaredSubjectMax))
            {
                continue;
            }

            var total = ExactRational.Zero;
            var complete = true;

            foreach (var group in manifest.Groups.Where(group => group.SubjectId == subject.SubjectId))
            {
                if (!groupTotals.TryGetValue(group.GroupId, out var groupTotal) || !groupTotal.Complete)
                {
                    complete = false;
                    continue;
                }

                total += groupTotal.Total;
            }

            if (!complete)
            {
                requiresAnswerArithmetic = true;
                continue;
            }

            if (declaredSubjectMax != total)
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.SubjectMaxScoreMismatch,
                    $"/subjects/{i}/maxScore",
                    $"科目满分 {subject.MaxScore} 与题组逐题满分之和 {total} 不一致。"));
            }
        }

        return requiresAnswerArithmetic;
    }

    private static bool TryQuestionFullScore(ManifestGroup group, int correctOptionCount, out ExactRational value)
    {
        value = ExactRational.Zero;
        if (correctOptionCount < 1 || group.Scoring is null)
        {
            return false;
        }

        // 一个正确选项走 oneCorrect.correct，多个正确选项走 multipleCorrect.allCorrect；
        // 该题满分即这两条分支的得分，也是需求「每题满分取选对或全部选对的得分」的落点。
        var expression = correctOptionCount == 1
            ? group.Scoring.OneCorrect?.Correct
            : group.Scoring.MultipleCorrect?.AllCorrect;

        var check = ScoreExpressionParser.Check(expression);
        if (!check.IsValid || check.Root is null)
        {
            return false;
        }

        try
        {
            // 全部选对时选择的正确选项数等于正确选项总数。
            value = ScoreExpressionParser.Evaluate(check.Root, correctOptionCount, correctOptionCount);
            return true;
        }
        catch (DivideByZeroException)
        {
            return false;
        }
    }

    private static void ValidateAnswerKey(ExamManifest manifest, AnswerKeyFile? answerKey, List<ImportIssue> issues)
    {
        if (answerKey is null)
        {
            return;
        }

        if (answerKey.SchemaVersion != 1)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.AnswerKeySchemaVersion,
                "/answerKey/schemaVersion",
                $"不支持的答案 schemaVersion：{answerKey.SchemaVersion}；当前只支持 1。"));
        }

        var questionsById = manifest.Questions
            .Where(question => !string.IsNullOrWhiteSpace(question.QuestionId))
            .GroupBy(question => question.QuestionId, StringComparer.Ordinal)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First(), StringComparer.Ordinal);

        var groupsById = manifest.Groups
            .Where(group => !string.IsNullOrWhiteSpace(group.GroupId))
            .GroupBy(group => group.GroupId, StringComparer.Ordinal)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First(), StringComparer.Ordinal);

        for (var i = 0; i < answerKey.Answers.Count; i++)
        {
            var answer = answerKey.Answers[i];
            var pointer = $"/answers/{i}";

            if (string.IsNullOrWhiteSpace(answer.QuestionId)
                || !questionsById.TryGetValue(answer.QuestionId, out var question))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.AnswerKeyUnknownQuestion,
                    $"{pointer}/questionId",
                    $"答案引用了清单中不存在的题目：{answer.QuestionId}。"));
                continue;
            }

            if (answer.QuestionNo != question.QuestionNo)
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.AnswerKeyQuestionNoMismatch,
                    $"{pointer}/questionNo",
                    $"答案题号 {answer.QuestionNo} 与清单题号 {question.QuestionNo} 不一致。"));
            }

            var correct = answer.CorrectOptionIds ?? [];
            var declared = question.Options ?? [];
            var notDeclared = correct
                .Where(option => !declared.Contains(option, StringComparer.Ordinal))
                .ToList();

            if (notDeclared.Count > 0)
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.AnswerKeyOptionNotDeclared,
                    $"{pointer}/correctOptionIds",
                    $"答案的正确选项不在该题选项之内：{string.Join('、', notDeclared)}。"));
                continue;
            }

            if (groupsById.TryGetValue(question.GroupId, out var group) && ParseType(group.Type) is { } type)
            {
                var valid = type switch
                {
                    QuestionTypeKind.Single => correct.Count == 1,
                    QuestionTypeKind.Multiple => correct.Count >= 2,
                    _ => correct.Count >= 1
                };

                if (!valid)
                {
                    issues.Add(ImportIssue.Error(
                        ManifestRuleIds.AnswerKeyCorrectCount,
                        $"{pointer}/correctOptionIds",
                        type == QuestionTypeKind.Single
                            ? $"单项选择题必须恰好一个正确选项，当前为 {correct.Count} 个。"
                            : $"该题组题型要求至少两个正确选项，当前为 {correct.Count} 个。"));
                }
            }
        }
    }

    private static QuestionTypeKind? ParseType(string? type) => type switch
    {
        "single" => QuestionTypeKind.Single,
        "multiple" => QuestionTypeKind.Multiple,
        "indeterminate" => QuestionTypeKind.Indeterminate,
        _ => null
    };

    private static void Required(bool satisfied, string pointer, string field, List<ImportIssue> issues)
    {
        if (!satisfied)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.RequiredMissing, pointer, $"缺少必填字段或取值不合规：{field}。"));
        }
    }

    private static bool TryReadManifest(string? json, List<ImportIssue> issues, out ExamManifest manifest)
    {
        manifest = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, "/", "清单内容为空。"));
            return false;
        }

        if (!ManifestShapeScanner.TryScan(json, isManifest: true, issues, out var document))
        {
            return false;
        }

        using (document)
        {
            // 未定义字段先返回：继续反序列化会因拒绝未知字段而重复报错。
            if (issues.Count > 0)
            {
                return false;
            }
        }

        if (!TryDeserialize(json, "清单", issues, out ExamManifest? parsed) || parsed is null)
        {
            return false;
        }

        manifest = parsed;
        return true;
    }

    private static bool TryReadAnswerKey(string? json, List<ImportIssue> issues, out AnswerKeyFile? answerKey)
    {
        answerKey = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        if (!ManifestShapeScanner.TryScan(json, isManifest: false, issues, out var document))
        {
            return false;
        }

        using (document)
        {
            if (issues.Count > 0)
            {
                return false;
            }
        }

        return TryDeserialize(json, "独立答案", issues, out answerKey);
    }

    private static bool TryDeserialize<T>(string json, string label, List<ImportIssue> issues, out T? value)
    {
        value = default;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, ImportJson.Create());
        }
        catch (JsonException exception)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.InvalidJson,
                PointerFromJsonPath(exception.Path),
                $"{label}的字段取值不符合约定：{exception.Message}"));
            return false;
        }

        if (value is null)
        {
            issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, "/", $"{label}内容为空。"));
            return false;
        }

        return true;
    }

    /// <summary>把 System.Text.Json 的 $.a.b[0] 路径转成 RFC 6901 JSON Pointer。</summary>
    private static string PointerFromJsonPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var pointer = path.Replace("$", string.Empty, StringComparison.Ordinal);
        pointer = pointer.Replace("[", "/", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        pointer = pointer.Replace('.', '/');
        return pointer.Length == 0 ? "/" : pointer;
    }
}
