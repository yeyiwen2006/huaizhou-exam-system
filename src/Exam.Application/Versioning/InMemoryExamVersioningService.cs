using System.Collections.Concurrent;
using Exam.Contracts.Versioning;
using Exam.Domain.Versioning;

namespace Exam.Application.Versioning;

public sealed class InMemoryExamVersioningService : IExamVersioningService
{
    private readonly ConcurrentDictionary<string, ExamVersionAggregate> _exams =
        new(StringComparer.Ordinal);

    public VersioningResult<ExamVersionSnapshot> CreateExam(CreateVersionedExamRequest request)
    {
        try
        {
            var aggregate = new ExamVersionAggregate(request.ExamId, request.Schedule, request.Now);
            if (!_exams.TryAdd(request.ExamId, aggregate))
            {
                return VersioningResult<ExamVersionSnapshot>.Failure(
                    VersioningErrorCode.ExamAlreadyExists,
                    $"Exam '{request.ExamId}' already exists.");
            }

            return VersioningResult<ExamVersionSnapshot>.Success(aggregate.GetSnapshot());
        }
        catch (VersioningRuleException exception)
        {
            return Failure<ExamVersionSnapshot>(exception);
        }
    }

    public VersioningResult<ExamVersionSnapshot> GetExam(string examId) =>
        WithExam(examId, aggregate => aggregate.GetSnapshot());

    public VersioningResult<ExamVersionSnapshot> UpdateSchedule(UpdateScheduleRequest request) =>
        WithExam(
            request.ExamId,
            aggregate => aggregate.UpdateSchedule(request.ExpectedVersion, request.Schedule, request.Now));

    public VersioningResult<ScheduleTriggerResult> ExecuteScheduleTrigger(ExecuteScheduleTriggerRequest request) =>
        WithExam(
            request.ExamId,
            aggregate => aggregate.ExecuteScheduleTrigger(
                request.ScheduleVersion,
                request.Trigger,
                request.Now));

    public VersioningResult<ExamVersionSnapshot> PublishPaper(PublishPaperRequest request) =>
        WithExam(request.ExamId, aggregate => aggregate.PublishPaper(request));

    public VersioningResult<ExamVersionSnapshot> PublishGradingRules(PublishGradingRulesRequest request) =>
        WithExam(request.ExamId, aggregate => aggregate.PublishGradingRules(request));

    public VersioningResult<ExamVersionSnapshot> RegisterParticipant(RegisterVersionedParticipantRequest request) =>
        WithExam(
            request.ExamId,
            aggregate => aggregate.RegisterParticipant(request.ParticipantId, request.InitialState));

    public VersioningResult<ExamVersionSnapshot> ChangeParticipantState(ChangeParticipantStateRequest request) =>
        WithExam(
            request.ExamId,
            aggregate => aggregate.ChangeParticipantState(request.ParticipantId, request.State));

    public VersioningResult<ExamVersionSnapshot> ReportPaperTransfer(ReportPaperTransferRequest request) =>
        WithExam(request.ExamId, aggregate => aggregate.ReportPaperTransfer(request));

    public VersioningResult<ExamVersionSnapshot> ConfirmPaperReview(ConfirmPaperReviewRequest request) =>
        WithExam(
            request.ExamId,
            aggregate => aggregate.ConfirmPaperReview(request.ParticipantId, request.PaperVersion));

    private VersioningResult<T> WithExam<T>(string examId, Func<ExamVersionAggregate, T> action)
    {
        if (string.IsNullOrWhiteSpace(examId) || !_exams.TryGetValue(examId, out var aggregate))
        {
            return VersioningResult<T>.Failure(
                VersioningErrorCode.ExamNotFound,
                $"Exam '{examId}' was not found.");
        }

        try
        {
            return VersioningResult<T>.Success(action(aggregate));
        }
        catch (VersioningRuleException exception)
        {
            return Failure<T>(exception);
        }
    }

    private static VersioningResult<T> Failure<T>(VersioningRuleException exception) =>
        VersioningResult<T>.Failure(exception.Code, exception.Message);
}
