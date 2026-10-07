using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Reporting;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Contracts.Reporting;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Common;
using Exam.Domain.Importing;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.HighAvailability;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Reporting;

public sealed class PostgresReportSource(NpgsqlDataSource dataSource, PostgresActiveNodeWriteGuard writeGuard) : IReportSource
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    public async ValueTask<ReportSourceSnapshot> ReadAsync(string examId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await writeGuard.BeginAsync(connection, cancellationToken);
        // Lock version/roster changes only for this short read. The unchanged row
        // update produces WAL: COMMIT fences every visible submission/grade under
        // the configured synchronous durability policy. No calculation holds locks.
        foreach (var sql in new[]
        {
            "UPDATE exam.version_state SET updated_at = updated_at WHERE exam_id = $1;",
            "SELECT roster_version FROM exam.actual_roster_versions WHERE exam_id = $1 FOR SHARE;"
        })
        {
            await using var fence = new NpgsqlCommand(sql, connection, transaction);
            fence.Parameters.Add(new Npgsql.NpgsqlParameter { Value=examId });
            await fence.ExecuteNonQueryAsync(cancellationToken);
        }
        string lifecycle, stateJson, membersJson;
        string? rulesJson;
        long paperVersion, gradingVersion, rosterVersion, revision;
        await using (var command = new NpgsqlCommand("""
            SELECT v.lifecycle, v.paper_version, v.grading_version, v.stream_revision,
                   COALESCE(rv.roster_version, 0), v.snapshot::text, publication.payload::text,
                   COALESCE((SELECT jsonb_agg(jsonb_build_object(
                       'candidateId', p.candidate_id, 'participationId', p.participation_id,
                       'entryVersion', p.entry_version,
                       'examNumber', r.admission_no, 'displayName', r.candidate_name, 'classNumber', r.class_text,
                       'submissionId', s.submission_id, 'paperVersion', s.paper_version,
                       'receiptId', s.receipt_id, 'answers', s.frozen_answers,
                       'gradingVersion', g.grading_version, 'questions', g.question_scores,
                       'subjects', g.subject_scores, 'total', g.total_score)
                       ORDER BY p.candidate_id COLLATE "C")
                    FROM exam.participations p
                    JOIN exam.candidate_roster_entries r ON r.exam_id=p.exam_id AND r.candidate_id=p.candidate_id
                    LEFT JOIN exam.final_submissions s ON s.participation_id=p.participation_id
                         AND s.exam_id=p.exam_id AND s.candidate_id=p.candidate_id
                    LEFT JOIN exam.grading_results g ON g.submission_id=s.submission_id
                         AND g.exam_id=s.exam_id AND g.candidate_id=s.candidate_id
                    WHERE p.exam_id=v.exam_id AND p.business_state <> 'not_participating'), '[]'::jsonb)::text
            FROM exam.version_state v
            LEFT JOIN exam.actual_roster_versions rv ON rv.exam_id=v.exam_id
            LEFT JOIN LATERAL (SELECT payload FROM exam.version_events
                WHERE exam_id=v.exam_id AND operation_type='grading.publish' AND stream_revision<=v.stream_revision
                ORDER BY stream_revision DESC LIMIT 1) publication ON true
            WHERE v.exam_id=$1;
            """, connection, transaction))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value=examId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new DomainRuleException(ErrorCodes.NotFound, "找不到考试。");
            lifecycle = reader.GetString(0); paperVersion = reader.GetInt64(1); gradingVersion = reader.GetInt64(2);
            revision = reader.GetInt64(3); rosterVersion = reader.GetInt64(4); stateJson = reader.GetString(5);
            rulesJson = reader.IsDBNull(6) ? null : reader.GetString(6); membersJson = reader.GetString(7);
        }
        // A failed/cancelled durability fence never yields a published snapshot.
        await transaction.CommitAsync(cancellationToken);
        var members = JsonSerializer.Deserialize<SourceMember[]>(membersJson, Json) ?? [];
        var missing = members.Where(member => member.SubmissionId is null || member.ReceiptId is null)
            .Select(member => new ReportMissingParticipant(member.CandidateId, member.ExamNumber, member.DisplayName, member.ClassNumber)).ToArray();
        var graded = members.Count(member => member.SubmissionId is not null && member.GradingVersion == gradingVersion && gradingVersion > 0);
        ReportSourceSnapshot Unavailable(ReportState state, string reason) => new(
            new(examId, state, reason, members.Length, members.Length-missing.Length, graded,
                rosterVersion, paperVersion, gradingVersion, null, missing), null);
        if (lifecycle != "Ended") return Unavailable(ReportState.ExamNotEnded, "考试尚未结束。");
        if (members.Length == 0) return Unavailable(ReportState.NoParticipants, "本场考试没有实际参加考试者。");
        if (missing.Length != 0) return Unavailable(ReportState.Collecting, "实际参加考试考生的答卷尚未全部回收。");
        if (gradingVersion <= 0 || graded != members.Length)
            return Unavailable(ReportState.Grading, "计分尚未完成或正在重新计分。");
        try
        {
            var publication = rulesJson is null ? null : JsonSerializer.Deserialize<PublishGradingRulesRequest>(rulesJson, Json);
            if (publication is null || publication.ExamId != examId || publication.ExpectedVersion != gradingVersion-1)
                return Unavailable(ReportState.InvalidSource, "计分规则与当前试卷版本不一致，请核对后重新发布计分规则。");
            var rules = ImportedGradingRules.Create(publication.ManifestJson, publication.AnswerKeyJson);
            var version = JsonSerializer.Deserialize<ExamVersionSnapshot>(stateJson, Json);
            if (rules.ExamId != examId || version?.Paper is null || !rules.Matches(version.Paper.Structure))
                return Unavailable(ReportState.InvalidSource, "试卷题目与计分规则不一致，请核对后重新发布计分规则。");
            var manifest = JsonSerializer.Deserialize<ExamManifest>(publication.ManifestJson!, ImportJson.Create())!;
            var questions = rules.Questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
            var subjects = manifest.Subjects.Select((subject, index) => new ReportSubject(subject.SubjectId,
                subject.Name, index+1, ReportJson.Parse(subject.MaxScore),
                manifest.Questions.Where(question => question.SubjectId == subject.SubjectId).OrderBy(question => question.QuestionNo)
                    .Select(question => new ReportQuestion(question.QuestionId, question.QuestionNo, Maximum(questions[question.QuestionId]))).ToArray())).ToArray();
            var participants = members.Select(member => new ReportParticipant(member.CandidateId, member.ExamNumber,
                member.DisplayName, member.ClassNumber)).ToArray();
            var submissions = members.Select(member =>
            {
                if (member.PaperVersion is null or < 1 || member.PaperVersion > paperVersion || member.Answers is null ||
                    member.Questions is null || member.Subjects is null || member.Total is null ||
                    AnswerSheetContract.Validate(rules.Structure, member.Answers) is not null)
                    throw new ArgumentException("答卷或成绩源不完整。");
                var grades = member.Questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
                if (grades.Count != rules.Questions.Count || member.Subjects.Count != subjects.Length)
                    throw new ArgumentException("成绩范围不完整。");
                foreach (var grade in member.Subjects)
                {
                    var raw = grades.Values.Where(question => question.SubjectId == grade.SubjectId)
                        .Aggregate(ExactRational.Zero, (sum, question) => sum + ReportJson.Parse(question.ExactScore));
                    if (raw != ReportJson.Parse(grade.ExactRawScore) || grade.WasClampedToZero != raw.IsNegative)
                        throw new ArgumentException("科目原始成绩与小题成绩不一致。");
                }
                return new ReportSubmission(member.SubmissionId!, examId, member.CandidateId, gradingVersion,
                    member.Subjects.Select(grade => new ReportSubjectGrade(grade.SubjectId, ReportJson.Parse(grade.ExactFinalScore),
                        manifest.Questions.Where(question => question.SubjectId == grade.SubjectId).OrderBy(question => question.QuestionNo)
                            .Select(question =>
                            {
                                var score = grades[question.QuestionId];
                                if (score.SubjectId != question.SubjectId || score.QuestionNo != question.QuestionNo)
                                    throw new ArgumentException("题目与成绩列不一致。");
                                return new ReportQuestionGrade(question.QuestionId, AnswerText(question, member.Answers[question.QuestionId]),
                                    score.Outcome, ReportJson.Parse(score.ExactScore));
                            }).ToArray())).ToArray(), ReportJson.Parse(member.Total));
            }).ToArray();
            var input = new ReportCalculationInput(examId, gradingVersion, subjects, participants, submissions);
            ReportCalculator.Validate(input);
            var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                examId+"\n"+revision+"\n"+rosterVersion+"\n"+stateJson+"\n"+rulesJson+"\n"+membersJson)));
            return new(new(examId, ReportState.Ready, "成绩已生成。", members.Length, members.Length, graded,
                rosterVersion, paperVersion, gradingVersion, token, []), input);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Unavailable(ReportState.InvalidSource, "成绩数据核对未通过，请联系系统管理员。");
        }
    }

    private static ExactRational Maximum(QuestionRule question)
    {
        var expression = question.CorrectOptionIds.Count == 1 ? question.Scoring?.OneCorrect?.Correct : question.Scoring?.MultipleCorrect?.AllCorrect;
        var parsed = ScoreExpressionParser.Check(expression);
        if (!parsed.IsValid || parsed.Root is null) throw new ArgumentException("题目满分规则无效。");
        return ScoreExpressionParser.Evaluate(parsed.Root, question.CorrectOptionIds.Count, question.CorrectOptionIds.Count);
    }
    private static string AnswerText(ManifestQuestion question, AnswerValue answer)
    {
        if (answer.Kind == AnswerKind.Empty) return "未作答";
        if (answer.Kind == AnswerKind.Abstain) return "不作答";
        var captions = question.DisplayMetadata?.OptionCaptions;
        if (captions is not null && captions.Count != question.Options!.Count) throw new ArgumentException("选项显示映射不完整。");
        return string.Concat(question.Options!.Select((option, index) => (option, index))
            .Where(item => answer.OptionIds.Contains(item.option, StringComparer.Ordinal))
            .Select(item => captions is null ? item.option : captions[item.index]));
    }
    private sealed record SourceMember(string CandidateId, string ParticipationId, long EntryVersion,
        string ExamNumber, string DisplayName, string ClassNumber, string? SubmissionId, long? PaperVersion,
        string? ReceiptId, Dictionary<string, AnswerValue>? Answers, long? GradingVersion,
        IReadOnlyList<QuestionGrade>? Questions, IReadOnlyList<SubjectGrade>? Subjects, string? Total);
}
