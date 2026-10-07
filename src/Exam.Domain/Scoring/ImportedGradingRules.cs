using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;

namespace Exam.Domain.Scoring;

/// <summary>清单和独立答案的统一组装入口，同时执行 清单校验 满分校验和 精确计分 全作答情形检查。</summary>
public sealed class ImportedGradingRules
{
    public string ExamId { get; }
    public PaperStructure Structure { get; }
    public IReadOnlyList<QuestionRule> Questions { get; }

    private ImportedGradingRules(ExamManifest manifest, AnswerKeyFile answers)
    {
        ExamId = manifest.ExamId;
        Structure = BuildStructure(manifest);
        var groups = manifest.Groups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
        var keys = answers.Answers.ToDictionary(a => a.QuestionId, StringComparer.Ordinal);
        Questions = Array.AsReadOnly(manifest.Questions.Select(q =>
        {
            var group = groups[q.GroupId];
            return new QuestionRule(q.QuestionId, q.QuestionNo, q.SubjectId,
                Array.AsReadOnly(q.Options!.ToArray()), Array.AsReadOnly(keys[q.QuestionId].CorrectOptionIds!.ToArray()), group.Scoring)
            {
                Type = Enum.Parse<QuestionTypeKind>(group.Type!, ignoreCase: true), AllowAbstain = group.AllowAbstain
            };
        }).ToArray());
    }

    public static ImportedGradingRules Create(string? manifestJson, string? answerKeyJson)
    {
        var validation = new ExamManifestValidator().ValidateJson(manifestJson, answerKeyJson);
        if (!validation.IsValid || validation.RequiresAnswerKeyArithmetic || string.IsNullOrWhiteSpace(answerKeyJson))
            throw new ArgumentException("清单、完整参考答案或满分核对未通过。" +
                string.Join("；", validation.Issues.Select(i => i.Message)));
        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson!, ImportJson.Create())!;
        var answers = JsonSerializer.Deserialize<AnswerKeyFile>(answerKeyJson!, ImportJson.Create())!;
        if (answers.Answers.Count != manifest.Questions.Count || answers.Answers.Any(a => a.CorrectOptionIds is not { Count: > 0 }))
            throw new ArgumentException("发布计分规则需要全部题目的参考答案。");
        var result = new ImportedGradingRules(manifest, answers);
        var problems = ScoreCalculator.ValidatePublishable(result.Questions);
        if (problems.Count != 0) throw new ArgumentException(string.Join("；", problems.Select(p => p.Message)));
        return result;
    }

    /// <summary>输入须先通过 清单校验；不接触独立参考答案。</summary>
    public static PaperStructure BuildStructure(ExamManifest manifest)
    {
        var groups = manifest.Groups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
        return new(Array.AsReadOnly(manifest.Questions.Select(q => new PaperQuestionStructure(
            q.QuestionId, q.QuestionNo, q.SubjectId, q.GroupId, groups[q.GroupId].Type!,
            Array.AsReadOnly(q.Options!.ToArray()), groups[q.GroupId].AllowAbstain)).ToArray()));
    }

    public bool Matches(PaperStructure paper) => Structure.Questions.Count == paper.Questions.Count &&
        Structure.Questions.All(a => paper.Questions.Any(b => a.QuestionId == b.QuestionId &&
            a.QuestionNo == b.QuestionNo && a.SubjectId == b.SubjectId && a.GroupId == b.GroupId &&
            a.QuestionType == b.QuestionType && a.AllowAbstain == b.AllowAbstain && a.OptionIds.SequenceEqual(b.OptionIds, StringComparer.Ordinal)));

    public GradeOutcome Grade(FinalSubmission submission, long gradingVersion)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.ExamId != ExamId || submission.PaperVersion < 1 || submission.Receipt is not { } receipt ||
            receipt.ExamId != submission.ExamId || receipt.CandidateId != submission.CandidateId ||
            receipt.SubmissionId != submission.SubmissionId || receipt.ParticipationId != submission.ParticipationId ||
            receipt.PaperVersion != submission.PaperVersion)
            return new(false, "固定答卷、原回执与计分规则的考试或版本不匹配。", null);
        var problem = AnswerSheetContract.Validate(Structure, submission.FrozenAnswers);
        return problem is not null ? new(false, problem, null) : ScoreCalculator.Grade(Questions,
            submission.FrozenAnswers, new(submission.SubmissionId, submission.ExamId, submission.CandidateId, gradingVersion));
    }
}
