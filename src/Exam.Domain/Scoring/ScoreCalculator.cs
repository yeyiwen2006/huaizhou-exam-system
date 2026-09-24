using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;

namespace Exam.Domain.Scoring;

/// <summary>
/// 一道题的计分输入。计分分支按**参考答案的选项数量**选择（1 个走 oneCorrect，多个走 multipleCorrect），
/// 与 D01 校验器的逐题满分判定保持一致；题组题型只决定配置里有哪些分支可用。
/// </summary>
public sealed record QuestionRule(
    string QuestionId,
    int QuestionNo,
    string SubjectId,
    IReadOnlyList<string> OptionIds,
    IReadOnlyList<string> CorrectOptionIds,
    ManifestGroupScoring? Scoring);

/// <summary>一份答卷的标识，用于把计分结果与原答卷对应。</summary>
public sealed record GradeIdentity(
    string SubmissionId,
    string ExamId,
    string CandidateId,
    long GradingVersion);

/// <summary>计分结果或失败原因。失败时不返回任何分数，绝不给出编造的分数。</summary>
public sealed record GradeOutcome(bool Succeeded, string? FailureReason, SubmissionGrade? Grade);

/// <summary>
/// 精确计分。依据 requirements.md「考试配置与计分」：原始得分精确计算、不舍入，整数不带小数点；
/// 小题允许负分，先累计单科原始分，再将负的单科成绩记为 0；总分为各科最终成绩之和，最低 0。
/// 全部为纯逻辑：只读答卷与规则，不访问文件、网络和数据库。
/// </summary>
public static class ScoreCalculator
{
    /// <summary>
    /// 判定一道题的作答情形。空选与「不作答」同走「不选」；选到任一错误选项即「选错」；
    /// 选择与答案完全一致为「选对/全部选对」；其余（正确答案的非空真子集）为「选对但不全」。
    /// </summary>
    public static AnswerOutcome DetermineOutcome(QuestionRule rule, AnswerValue? answer)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (answer is null || answer.Kind != AnswerKind.Selected || answer.OptionIds.Count == 0)
        {
            return AnswerOutcome.Blank;
        }

        var selected = answer.OptionIds;
        if (selected.Any(option => !Contains(rule.CorrectOptionIds, option)))
        {
            return AnswerOutcome.Wrong;
        }

        return selected.Count == rule.CorrectOptionIds.Count
            ? AnswerOutcome.Correct
            : AnswerOutcome.PartialCorrect;
    }

    /// <summary>逐题计分并汇总到单科与总分。任何一道题的分值不合法时整体失败。</summary>
    public static GradeOutcome Grade(
        IReadOnlyList<QuestionRule> rules,
        IReadOnlyDictionary<string, AnswerValue> answers,
        GradeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(identity);

        var questionGrades = new List<QuestionGrade>();
        var subjectRaw = new Dictionary<string, ExactRational>(StringComparer.Ordinal);
        var subjectOrder = new List<string>();

        foreach (var rule in rules)
        {
            var answer = answers.TryGetValue(rule.QuestionId, out var value) ? value : null;
            var outcome = DetermineOutcome(rule, answer);

            if (!TryResolveExpression(rule, outcome, out var expression, out var problemCode, out var message))
            {
                return new GradeOutcome(
                    false,
                    $"题目 {rule.QuestionNo} 无法计分（{problemCode}）：{message}",
                    null);
            }

            var correctCount = rule.CorrectOptionIds.Count;
            var selectedCorrectCount = CountSelectedCorrect(rule, answer);

            // 先解析成语法树再求值：运行时也要挡住非法表达式，绝不退回「0 分」这类假结果。
            var check = ScoreExpressionParser.Check(expression);
            if (!check.IsValid || check.Root is null)
            {
                return new GradeOutcome(
                    false,
                    $"题目 {rule.QuestionNo} 的分值表达式不合法（{check.Status}）：{check.Message}",
                    null);
            }

            ExactRational score;
            try
            {
                score = ScoreExpressionParser.Evaluate(check.Root, correctCount, selectedCorrectCount);
            }
            catch (DivideByZeroException)
            {
                return new GradeOutcome(
                    false,
                    $"题目 {rule.QuestionNo} 在该作答情形下分母为 0，配置不合法。",
                    null);
            }

            if (!score.TryFormatFiniteDecimal(out var text))
            {
                return new GradeOutcome(
                    false,
                    $"题目 {rule.QuestionNo} 的最终分值不是有限小数；发布前应当拒绝该配置。",
                    null);
            }

            questionGrades.Add(new QuestionGrade(
                rule.QuestionId, rule.QuestionNo, rule.SubjectId, outcome, text));

            if (!subjectRaw.TryGetValue(rule.SubjectId, out var current))
            {
                current = ExactRational.Zero;
                subjectOrder.Add(rule.SubjectId);
            }

            subjectRaw[rule.SubjectId] = current + score;
        }

        var subjectGrades = new List<SubjectGrade>();
        var total = ExactRational.Zero;
        foreach (var subjectId in subjectOrder)
        {
            var raw = subjectRaw[subjectId];
            var clamped = raw.IsNegative;
            var finalScore = clamped ? ExactRational.Zero : raw;
            total += finalScore;

            subjectGrades.Add(new SubjectGrade(
                subjectId,
                Display(raw),
                Display(finalScore),
                clamped));
        }

        // 每科最终分都不小于 0，因此总分天然不小于 0；此处再取一次下限以防规则被改坏。
        if (total.IsNegative)
        {
            total = ExactRational.Zero;
        }

        return new GradeOutcome(
            true,
            null,
            new SubmissionGrade(
                identity.SubmissionId,
                identity.ExamId,
                identity.CandidateId,
                identity.GradingVersion,
                questionGrades,
                subjectGrades,
                Display(total)));
    }

    /// <summary>
    /// 发布前校验：枚举每题所有**可达**的作答情形，逐一求值并检查最终分值。
    /// requirements.md 要求「发布前校验所有合法作答情况，最终逐题分必须为有限小数」，
    /// 因此像 1÷3 这样的规则必须在发布前被挡下，而不是等计分时才暴露。
    /// </summary>
    public static IReadOnlyList<ScoreRuleProblem> ValidatePublishable(IReadOnlyList<QuestionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var problems = new List<ScoreRuleProblem>();

        foreach (var rule in rules)
        {
            problems.AddRange(ValidateQuestion(rule));
        }

        return problems;
    }

    private static IEnumerable<ScoreRuleProblem> ValidateQuestion(QuestionRule rule)
    {
        if (rule.CorrectOptionIds.Count == 0)
        {
            yield return new ScoreRuleProblem(
                ScoreRuleCodes.CorrectOptionMissing,
                $"题目 {rule.QuestionNo} 没有配置正确答案。",
                rule.QuestionId);
            yield break;
        }

        var notDeclared = rule.CorrectOptionIds
            .Where(option => !Contains(rule.OptionIds, option))
            .ToList();
        if (notDeclared.Count > 0)
        {
            yield return new ScoreRuleProblem(
                ScoreRuleCodes.CorrectOptionNotDeclared,
                $"题目 {rule.QuestionNo} 的正确答案不在选项之内：{string.Join('、', notDeclared)}。",
                rule.QuestionId);
            yield break;
        }

        var correctCount = rule.CorrectOptionIds.Count;
        var hasIncorrectOption = rule.OptionIds.Count > correctCount;

        // 分支是否存在是**每道题一个**问题，不随作答情形重复上报。
        if (rule.Scoring is null)
        {
            yield return new ScoreRuleProblem(
                ScoreRuleCodes.BranchMissing,
                $"题目 {rule.QuestionNo} 的题组没有配置计分分支。",
                rule.QuestionId);
            yield break;
        }

        var oneCorrect = rule.Scoring.OneCorrect;
        var multipleCorrect = rule.Scoring.MultipleCorrect;
        if (correctCount >= 2 && multipleCorrect is null)
        {
            yield return new ScoreRuleProblem(
                ScoreRuleCodes.BranchMissing,
                $"题目 {rule.QuestionNo} 有 {correctCount} 个正确选项，必须配置 multipleCorrect 分支。",
                rule.QuestionId);
            yield break;
        }

        if (correctCount == 1 && oneCorrect is null)
        {
            yield return new ScoreRuleProblem(
                ScoreRuleCodes.BranchMissing,
                $"题目 {rule.QuestionNo} 只有一个正确选项，必须配置 oneCorrect 分支。",
                rule.QuestionId);
            yield break;
        }

        foreach (var (outcome, selectedCorrectCount) in EnumerateReachableOutcomes(correctCount, hasIncorrectOption))
        {
            if (!TryResolveExpression(rule, outcome, out var expression, out var code, out var message))
            {
                yield return new ScoreRuleProblem(
                    code!,
                    $"题目 {rule.QuestionNo} 的{Describe(outcome)}分支不可用：{message}",
                    rule.QuestionId);
                continue;
            }

            var check = ScoreExpressionParser.Check(expression);
            if (!check.IsValid || check.Root is null)
            {
                // 解析期就能判定的除零与非有限小数要报得更精确：客户端据此给出可操作提示。
                var parseCode = check.Status switch
                {
                    ScoreExpressionStatus.UnknownVariable => ScoreRuleCodes.ExpressionUnknownVariable,
                    ScoreExpressionStatus.DivisionByZero => ScoreRuleCodes.DivisionByZero,
                    ScoreExpressionStatus.NonTerminatingDecimal => ScoreRuleCodes.NotFiniteDecimal,
                    _ => ScoreRuleCodes.ExpressionInvalid
                };

                yield return new ScoreRuleProblem(
                    parseCode,
                    $"题目 {rule.QuestionNo} 的{Describe(outcome)}分值表达式不合法：{check.Message}",
                    rule.QuestionId);
                continue;
            }

            // 迭代器里不能带 yield 的 catch：先把异常转成标志，再在 catch 之外上报。
            var value = ExactRational.Zero;
            var dividedByZero = false;
            try
            {
                value = ScoreExpressionParser.Evaluate(check.Root, correctCount, selectedCorrectCount);
            }
            catch (DivideByZeroException)
            {
                dividedByZero = true;
            }

            if (dividedByZero)
            {
                yield return new ScoreRuleProblem(
                    ScoreRuleCodes.DivisionByZero,
                    $"题目 {rule.QuestionNo} 在{Describe(outcome)}（选对 {selectedCorrectCount} 个、"
                    + $"共 {correctCount} 个正确选项）时分母为 0。",
                    rule.QuestionId);
                continue;
            }

            if (!value.IsFiniteDecimal)
            {
                yield return new ScoreRuleProblem(
                    ScoreRuleCodes.NotFiniteDecimal,
                    $"题目 {rule.QuestionNo} 在{Describe(outcome)}（选对 {selectedCorrectCount} 个、"
                    + $"共 {correctCount} 个正确选项）时的分值是 {value}，不是有限小数。",
                    rule.QuestionId);
                continue;
            }

            if (outcome == AnswerOutcome.Correct && value.IsNegative)
            {
                yield return new ScoreRuleProblem(
                    ScoreRuleCodes.NegativeFullScore,
                    $"题目 {rule.QuestionNo} 的满分（选对或全部选对的得分）为负：{value}。",
                    rule.QuestionId);
            }
        }
    }

    /// <summary>
    /// 枚举一道题可达的作答情形及对应的「选择的正确选项数」。
    /// 只有存在错误选项时才可能「选错」；只有正确选项多于一个时才可能「选对但不全」。
    /// </summary>
    private static IEnumerable<(AnswerOutcome Outcome, int SelectedCorrectCount)> EnumerateReachableOutcomes(
        int correctCount,
        bool hasIncorrectOption)
    {
        yield return (AnswerOutcome.Blank, 0);
        yield return (AnswerOutcome.Correct, correctCount);

        for (var selected = 1; selected < correctCount; selected++)
        {
            yield return (AnswerOutcome.PartialCorrect, selected);
        }

        if (!hasIncorrectOption)
        {
            yield break;
        }

        // 选错时可能选中 0 到 correctCount-1 个正确选项，再加上至少一个错误选项。
        for (var selected = 0; selected < correctCount; selected++)
        {
            yield return (AnswerOutcome.Wrong, selected);
        }
    }

    private static bool TryResolveExpression(
        QuestionRule rule,
        AnswerOutcome outcome,
        out string? expression,
        out string? problemCode,
        out string? message)
    {
        expression = null;
        problemCode = null;
        message = null;

        var usesMultipleBranch = rule.CorrectOptionIds.Count >= 2;
        var scoring = rule.Scoring;
        if (scoring is null)
        {
            problemCode = ScoreRuleCodes.BranchMissing;
            message = "题组没有配置计分分支。";
            return false;
        }

        expression = outcome switch
        {
            AnswerOutcome.Correct => usesMultipleBranch
                ? scoring.MultipleCorrect?.AllCorrect
                : scoring.OneCorrect?.Correct,
            AnswerOutcome.PartialCorrect => scoring.MultipleCorrect?.PartialCorrect,
            AnswerOutcome.Blank => usesMultipleBranch
                ? scoring.MultipleCorrect?.Blank
                : scoring.OneCorrect?.Blank,
            _ => usesMultipleBranch
                ? scoring.MultipleCorrect?.Wrong
                : scoring.OneCorrect?.Wrong
        };

        if (expression is null)
        {
            problemCode = ScoreRuleCodes.BranchMissing;
            message = usesMultipleBranch
                ? "该题有多个正确选项，必须配置 multipleCorrect 分支。"
                : "该题只有一个正确选项，必须配置 oneCorrect 分支。";
            return false;
        }

        return true;
    }

    private static int CountSelectedCorrect(QuestionRule rule, AnswerValue? answer)
    {
        if (answer is null || answer.Kind != AnswerKind.Selected)
        {
            return 0;
        }

        return answer.OptionIds.Count(option => Contains(rule.CorrectOptionIds, option));
    }

    private static bool Contains(IReadOnlyList<string> options, string option) =>
        options.Contains(option, StringComparer.Ordinal);

    private static string Display(ExactRational value) =>
        value.TryFormatFiniteDecimal(out var text) ? text : value.ToString();

    private static string Describe(AnswerOutcome outcome) => outcome switch
    {
        AnswerOutcome.Blank => "不选",
        AnswerOutcome.Correct => "选对/全部选对",
        AnswerOutcome.PartialCorrect => "选对但不全",
        _ => "选错"
    };
}
