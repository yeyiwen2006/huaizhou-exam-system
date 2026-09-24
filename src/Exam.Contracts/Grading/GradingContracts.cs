namespace Exam.Contracts.Grading;

/// <summary>
/// 一道题的作答判定。含义按 requirements.md「考试配置与计分」：
/// 「全部选对」为选择与答案完全一致；「选对但不全」为正确答案的非空真子集；
/// 「不选」为空选或「不作答」；选到任一错误选项即「选错」。
/// </summary>
public enum AnswerOutcome
{
    Blank,
    Correct,
    PartialCorrect,
    Wrong
}

/// <summary>逐题得分。分数用字符串传，整数不带小数点（agent-implementation-guide.md）。</summary>
public sealed record QuestionGrade(
    string QuestionId,
    int QuestionNo,
    string SubjectId,
    AnswerOutcome Outcome,
    string ExactScore);

/// <summary>
/// 单科得分。原始分允许为负；最终分按需求「先累计单科原始分，再将负的单科成绩记为 0」。
/// </summary>
public sealed record SubjectGrade(
    string SubjectId,
    string ExactRawScore,
    string ExactFinalScore,
    bool WasClampedToZero);

/// <summary>一份答卷的计分结果。原始答案不在此结构中，计分只读不改。</summary>
public sealed record SubmissionGrade(
    string SubmissionId,
    string ExamId,
    string CandidateId,
    long GradingVersion,
    IReadOnlyList<QuestionGrade> Questions,
    IReadOnlyList<SubjectGrade> Subjects,
    string ExactTotalScore);

/// <summary>计分规则问题。Code 为稳定标识，客户端不解析中文文案分支。</summary>
public sealed record ScoreRuleProblem(
    string Code,
    string Message,
    string? QuestionId = null,
    string? SubjectId = null);

/// <summary>计分规则问题的稳定标识。</summary>
public static class ScoreRuleCodes
{
    /// <summary>规则集合、题目标识、选项集合或题型不合法。</summary>
    public const string RuleInvalid = "score.ruleInvalid";

    /// <summary>参考答案里有选项不在该题选项之内。</summary>
    public const string CorrectOptionNotDeclared = "score.correctOptionNotDeclared";

    /// <summary>参考答案为空。</summary>
    public const string CorrectOptionMissing = "score.correctOptionMissing";

    /// <summary>按正确选项数量需要的计分分支没有配置。</summary>
    public const string BranchMissing = "score.branchMissing";

    /// <summary>表达式文法非法。</summary>
    public const string ExpressionInvalid = "score.expressionInvalid";

    /// <summary>表达式含未知变量。</summary>
    public const string ExpressionUnknownVariable = "score.expressionUnknownVariable";

    /// <summary>某个可达作答情形下分母为 0。</summary>
    public const string DivisionByZero = "score.divisionByZero";

    /// <summary>某个可达作答情形下最终分值不是有限小数（例如 1÷3）。</summary>
    public const string NotFiniteDecimal = "score.notFiniteDecimal";

    /// <summary>满分分支的得分小于 0。</summary>
    public const string NegativeFullScore = "score.negativeFullScore";

    /// <summary>答卷缺少该题的答案。</summary>
    public const string AnswerMissing = "score.answerMissing";
}
