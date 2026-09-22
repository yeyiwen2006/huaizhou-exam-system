using System.Collections.ObjectModel;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.Domain.Versioning;

public sealed class ExamVersionAggregate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ParticipantState> _participants = new(StringComparer.Ordinal);
    private readonly string _examId;
    private ScheduleWindow _schedule;
    private long _scheduleVersion = 1;
    private long _paperVersion;
    private long _gradingVersion;
    private ExamLifecycle _lifecycleState;
    private DateTimeOffset? _actualStartedAt;
    private DateTimeOffset? _actualEndedAt;
    private PaperPublicationInfo? _paper;
    private GradingPublicationInfo? _grading;

    public ExamVersionAggregate(string examId, ScheduleWindow schedule, DateTimeOffset now)
    {
        RequireIdentifier(examId, nameof(examId));
        ValidateSchedule(schedule);

        _examId = examId;
        _schedule = schedule;
        _lifecycleState = ExamLifecycle.Scheduled;
        ReconcileLifecycle(now);
    }

    public ExamVersionSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot UpdateSchedule(long expectedVersion, ScheduleWindow schedule, DateTimeOffset now)
    {
        lock (_gate)
        {
            EnsureExpectedVersion(expectedVersion, _scheduleVersion, "scheduleVersion");
            ValidateSchedule(schedule);

            _schedule = schedule;
            _scheduleVersion = checked(_scheduleVersion + 1);
            ReconcileLifecycle(now);
            return CreateSnapshot();
        }
    }

    public ScheduleTriggerResult ExecuteScheduleTrigger(
        long taskScheduleVersion,
        ScheduleTriggerKind trigger,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (taskScheduleVersion != _scheduleVersion)
            {
                return new ScheduleTriggerResult(
                    false,
                    VersioningErrorCode.StaleScheduleTask,
                    $"The timer belongs to scheduleVersion {taskScheduleVersion}, but the current version is {_scheduleVersion}.",
                    CreateSnapshot());
            }

            var dueAt = trigger == ScheduleTriggerKind.Start ? _schedule.StartsAt : _schedule.EndsAt;
            if (now < dueAt)
            {
                return new ScheduleTriggerResult(
                    false,
                    VersioningErrorCode.ScheduleTaskNotDue,
                    $"The {trigger} timer is not due until {dueAt:O}.",
                    CreateSnapshot());
            }

            var before = _lifecycleState;
            ReconcileLifecycle(now);
            return new ScheduleTriggerResult(
                before != _lifecycleState,
                VersioningErrorCode.None,
                null,
                CreateSnapshot());
        }
    }

    public ExamVersionSnapshot PublishPaper(PublishPaperRequest request)
    {
        lock (_gate)
        {
            EnsureExpectedVersion(request.ExpectedVersion, _paperVersion, "paperVersion");
            ValidatePaperRequest(request);

            var normalizedStructure = NormalizeStructure(request.Structure);
            if (_paper is not null &&
                _lifecycleState != ExamLifecycle.Scheduled &&
                !StructuresEqual(_paper.Structure, normalizedStructure))
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.StructuralChangeRejected,
                    "Question, option, subject, group, number, or type structure cannot change after the exam has started.");
            }

            var changedQuestionIds = request.ChangedQuestionIds
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var knownQuestions = normalizedStructure.Questions
                .Select(question => question.QuestionId)
                .ToHashSet(StringComparer.Ordinal);
            if (changedQuestionIds.Any(questionId => !knownQuestions.Contains(questionId)))
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    "Every changed question identifier must exist in the published structure.");
            }

            _paperVersion = checked(_paperVersion + 1);
            _paper = new PaperPublicationInfo(
                _paperVersion,
                request.ArtifactId,
                normalizedStructure,
                Array.AsReadOnly(changedQuestionIds),
                request.Now,
                request.Operator,
                request.Reason);

            foreach (var participant in _participants.Values)
            {
                participant.TargetPaperVersion = _paperVersion;
                participant.PaperUpdateState = PaperUpdateState.NotReceived;
                participant.LastError = null;
            }

            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot PublishGradingRules(PublishGradingRulesRequest request)
    {
        lock (_gate)
        {
            EnsureExpectedVersion(request.ExpectedVersion, _gradingVersion, "gradingVersion");
            RequireIdentifier(request.RuleSetId, nameof(request.RuleSetId));
            RequireIdentifier(request.Operator, nameof(request.Operator));
            RequireIdentifier(request.Reason, nameof(request.Reason));
            if (!request.Validation.IsReady)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.PublicationNotReady,
                    "Answers, scoring rules, and recalculation readiness must all be validated before publishing.");
            }

            _gradingVersion = checked(_gradingVersion + 1);
            _grading = new GradingPublicationInfo(
                _gradingVersion,
                request.RuleSetId,
                request.Now,
                request.Operator,
                request.Reason);
            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot RegisterParticipant(string participantId, ParticipationBusinessState initialState)
    {
        lock (_gate)
        {
            RequireIdentifier(participantId, nameof(participantId));
            if (initialState == ParticipationBusinessState.NotParticipating)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidStateTransition,
                    "NotParticipating is not a registrable participant state.");
            }

            if (_lifecycleState == ExamLifecycle.Ended)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.ExamEnded,
                    "New participants cannot be registered after the exam has ended.");
            }

            if (_participants.TryGetValue(participantId, out var existing))
            {
                if (existing.ExamState != initialState)
                {
                    throw new VersioningRuleException(
                        VersioningErrorCode.InvalidStateTransition,
                        "An existing participant cannot be registered again with a different state.");
                }

                return CreateSnapshot();
            }

            if (_lifecycleState == ExamLifecycle.Running && initialState == ParticipationBusinessState.Waiting)
            {
                initialState = ParticipationBusinessState.Answering;
            }

            _participants.Add(
                participantId,
                new ParticipantState(
                    participantId,
                    initialState,
                    _paperVersion,
                    0,
                    PaperUpdateState.NotReceived,
                    null));
            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot SynchronizePersistedParticipant(
        string participantId,
        ParticipationBusinessState persistedState)
    {
        lock (_gate)
        {
            RequireIdentifier(participantId, nameof(participantId));
            if (persistedState == ParticipationBusinessState.NotParticipating)
            {
                _participants.Remove(participantId);
                return CreateSnapshot();
            }

            if (!_participants.TryGetValue(participantId, out var participant))
            {
                _participants.Add(
                    participantId,
                    new ParticipantState(
                        participantId,
                        persistedState,
                        _paperVersion,
                        0,
                        PaperUpdateState.NotReceived,
                        null));
                return CreateSnapshot();
            }

            participant.ExamState = persistedState;
            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot ChangeParticipantState(string participantId, ParticipationBusinessState requestedState)
    {
        lock (_gate)
        {
            var participant = GetParticipant(participantId);
            if (requestedState == ParticipationBusinessState.NotParticipating)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidStateTransition,
                    "A registered participant cannot transition back to NotParticipating.");
            }

            if (participant.ExamState == requestedState)
            {
                return CreateSnapshot();
            }

            if (participant.ExamState == ParticipationBusinessState.Submitted)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidStateTransition,
                    "A submitted participant cannot be restored to an editable state.");
            }

            if (requestedState == ParticipationBusinessState.Waiting ||
                (_lifecycleState == ExamLifecycle.Ended && requestedState != ParticipationBusinessState.Submitted))
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidStateTransition,
                    "The requested participant state would move the exam backwards.");
            }

            participant.ExamState = requestedState;
            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot ReportPaperTransfer(ReportPaperTransferRequest request)
    {
        lock (_gate)
        {
            var participant = GetParticipant(request.ParticipantId);
            EnsureExpectedVersion(request.PaperVersion, _paperVersion, "paperVersion");
            if (_paperVersion == 0 || _paper is null)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    "No paper has been published for this exam.");
            }

            if (request.InstalledPaperVersion < participant.InstalledPaperVersion)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.ResourceRollbackRejected,
                    "A client cannot replace an installed paper with an older resource version.");
            }

            if (request.InstalledPaperVersion > _paperVersion)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    "The reported installed paper version has not been published by this server.");
            }

            if (request.Outcome == PaperTransferOutcome.Installed &&
                request.InstalledPaperVersion != _paperVersion)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.ResourceRollbackRejected,
                    "An older paper cannot be reported as the completed update for the current version.");
            }

            if (request.Outcome == PaperTransferOutcome.DownloadFailed)
            {
                participant.PaperUpdateState = PaperUpdateState.DownloadFailed;
                participant.LastError = string.IsNullOrWhiteSpace(request.Error)
                    ? "Paper download or validation failed."
                    : request.Error;
                return CreateSnapshot();
            }

            participant.InstalledPaperVersion = request.InstalledPaperVersion;
            participant.PaperUpdateState = _paper.ChangedQuestionIds.Count == 0
                ? PaperUpdateState.Updated
                : PaperUpdateState.ReviewRequired;
            participant.LastError = null;
            return CreateSnapshot();
        }
    }

    public ExamVersionSnapshot ConfirmPaperReview(string participantId, long paperVersion)
    {
        lock (_gate)
        {
            var participant = GetParticipant(participantId);
            EnsureExpectedVersion(paperVersion, _paperVersion, "paperVersion");
            if (participant.InstalledPaperVersion != _paperVersion ||
                participant.PaperUpdateState != PaperUpdateState.ReviewRequired)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidStateTransition,
                    "Review can only be confirmed after the current paper has been installed and requested for review.");
            }

            participant.PaperUpdateState = PaperUpdateState.Updated;
            return CreateSnapshot();
        }
    }

    private void ReconcileLifecycle(DateTimeOffset now)
    {
        if (_lifecycleState == ExamLifecycle.Ended)
        {
            return;
        }

        if (now >= _schedule.EndsAt)
        {
            _lifecycleState = ExamLifecycle.Ended;
            _actualEndedAt ??= now;
            return;
        }

        if (_lifecycleState == ExamLifecycle.Scheduled && now >= _schedule.StartsAt)
        {
            _lifecycleState = ExamLifecycle.Running;
            _actualStartedAt ??= now;
            foreach (var participant in _participants.Values.Where(
                         participant => participant.ExamState == ParticipationBusinessState.Waiting))
            {
                participant.ExamState = ParticipationBusinessState.Answering;
            }
        }
    }

    private ExamVersionSnapshot CreateSnapshot()
    {
        var participants = _participants.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToContract(),
            StringComparer.Ordinal);

        return new ExamVersionSnapshot(
            _examId,
            new VersionVector(_scheduleVersion, _paperVersion, _gradingVersion),
            new ScheduleWindow(_schedule.StartsAt, _schedule.EndsAt),
            _lifecycleState,
            _actualStartedAt,
            _actualEndedAt,
            CopyPaper(_paper),
            _grading,
            new ReadOnlyDictionary<string, ParticipantVersionStatus>(participants));
    }

    private ParticipantState GetParticipant(string participantId)
    {
        RequireIdentifier(participantId, nameof(participantId));
        if (!_participants.TryGetValue(participantId, out var participant))
        {
            throw new VersioningRuleException(
                VersioningErrorCode.ParticipantNotFound,
                $"Participant '{participantId}' is not registered for exam '{_examId}'.");
        }

        return participant;
    }

    private static void ValidateSchedule(ScheduleWindow schedule)
    {
        if (schedule.EndsAt <= schedule.StartsAt)
        {
            throw new VersioningRuleException(
                VersioningErrorCode.InvalidSchedule,
                "The exam end time must be later than its start time.");
        }
    }

    private static void ValidatePaperRequest(PublishPaperRequest request)
    {
        RequireIdentifier(request.ArtifactId, nameof(request.ArtifactId));
        RequireIdentifier(request.Operator, nameof(request.Operator));
        RequireIdentifier(request.Reason, nameof(request.Reason));
        if (!request.Validation.IsReady)
        {
            throw new VersioningRuleException(
                VersioningErrorCode.PublicationNotReady,
                "Manifest, compilation, preview, and encrypted package checks must pass before publishing.");
        }

        if (request.Structure.Questions.Count == 0)
        {
            throw new VersioningRuleException(
                VersioningErrorCode.InvalidRequest,
                "A published paper must contain at least one question.");
        }
    }

    private static PaperStructure NormalizeStructure(PaperStructure structure)
    {
        var seenQuestions = new HashSet<string>(StringComparer.Ordinal);
        var seenQuestionNumbers = new HashSet<int>();
        var normalized = new PaperQuestionStructure[structure.Questions.Count];

        for (var index = 0; index < structure.Questions.Count; index++)
        {
            var question = structure.Questions[index];
            RequireIdentifier(question.QuestionId, nameof(question.QuestionId));
            RequireIdentifier(question.SubjectId, nameof(question.SubjectId));
            RequireIdentifier(question.GroupId, nameof(question.GroupId));
            RequireIdentifier(question.QuestionType, nameof(question.QuestionType));
            if (!seenQuestions.Add(question.QuestionId))
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    $"Question identifier '{question.QuestionId}' is duplicated.");
            }

            if (question.QuestionNo <= 0)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    $"Question '{question.QuestionId}' must have a positive whole-paper question number.");
            }

            if (!seenQuestionNumbers.Add(question.QuestionNo))
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    $"Whole-paper question number '{question.QuestionNo}' is duplicated.");
            }

            if (question.OptionIds.Count == 0 ||
                question.OptionIds.Any(string.IsNullOrWhiteSpace) ||
                question.OptionIds.Distinct(StringComparer.Ordinal).Count() != question.OptionIds.Count)
            {
                throw new VersioningRuleException(
                    VersioningErrorCode.InvalidRequest,
                    $"Question '{question.QuestionId}' must have non-empty, unique option identifiers.");
            }

            normalized[index] = new PaperQuestionStructure(
                question.QuestionId,
                question.QuestionNo,
                question.SubjectId,
                question.GroupId,
                question.QuestionType,
                Array.AsReadOnly(question.OptionIds.ToArray()));
        }

        return new PaperStructure(Array.AsReadOnly(normalized));
    }

    private static bool StructuresEqual(PaperStructure left, PaperStructure right)
    {
        if (left.Questions.Count != right.Questions.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Questions.Count; index++)
        {
            var a = left.Questions[index];
            var b = right.Questions[index];
            if (!StringComparer.Ordinal.Equals(a.QuestionId, b.QuestionId) ||
                a.QuestionNo != b.QuestionNo ||
                !StringComparer.Ordinal.Equals(a.SubjectId, b.SubjectId) ||
                !StringComparer.Ordinal.Equals(a.GroupId, b.GroupId) ||
                !StringComparer.Ordinal.Equals(a.QuestionType, b.QuestionType) ||
                !a.OptionIds.SequenceEqual(b.OptionIds, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static PaperPublicationInfo? CopyPaper(PaperPublicationInfo? paper)
    {
        if (paper is null)
        {
            return null;
        }

        return paper with
        {
            Structure = NormalizeStructure(paper.Structure),
            ChangedQuestionIds = Array.AsReadOnly(paper.ChangedQuestionIds.ToArray())
        };
    }

    private static void EnsureExpectedVersion(long expected, long actual, string versionName)
    {
        if (expected != actual)
        {
            throw new VersioningRuleException(
                VersioningErrorCode.VersionConflict,
                $"Expected {versionName} {expected}, but the current version is {actual}.");
        }
    }

    private static void RequireIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new VersioningRuleException(
                VersioningErrorCode.InvalidRequest,
                $"{field} is required.");
        }
    }

    private sealed class ParticipantState
    {
        public ParticipantState(
            string participantId,
            ParticipationBusinessState examState,
            long targetPaperVersion,
            long installedPaperVersion,
            PaperUpdateState paperUpdateState,
            string? lastError)
        {
            ParticipantId = participantId;
            ExamState = examState;
            TargetPaperVersion = targetPaperVersion;
            InstalledPaperVersion = installedPaperVersion;
            PaperUpdateState = paperUpdateState;
            LastError = lastError;
        }

        public string ParticipantId { get; }

        public ParticipationBusinessState ExamState { get; set; }

        public long TargetPaperVersion { get; set; }

        public long InstalledPaperVersion { get; set; }

        public PaperUpdateState PaperUpdateState { get; set; }

        public string? LastError { get; set; }

        public ParticipantVersionStatus ToContract() =>
            new(
                ParticipantId,
                ExamState,
                TargetPaperVersion,
                InstalledPaperVersion,
                PaperUpdateState,
                LastError);
    }
}
