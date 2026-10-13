using Exam.Application.Versioning;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.UnitTests.Versioning;

public sealed partial class ExamVersioningServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 9, 22, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Create_rejects_an_invalid_schedule()
    {
        var service = new InMemoryExamVersioningService();

        var result = service.CreateExam(
            new CreateVersionedExamRequest(
                "exam-1",
                new ScheduleWindow(BaseTime.AddHours(2), BaseTime.AddHours(1)),
                BaseTime));

        Assert.False(result.Succeeded);
        Assert.Equal(VersioningErrorCode.InvalidSchedule, result.ErrorCode);
    }

    [Fact]
    public void Schedule_update_uses_expected_version_and_invalidates_the_old_end_timer()
    {
        var service = CreateService();
        var originalEnd = BaseTime.AddHours(2);

        var updated = service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddHours(1), BaseTime.AddHours(3)),
                BaseTime));
        var staleTimer = service.ExecuteScheduleTrigger(
            new ExecuteScheduleTriggerRequest(
                "exam-1",
                1,
                ScheduleTriggerKind.End,
                originalEnd));
        var conflict = service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddHours(1), BaseTime.AddHours(4)),
                BaseTime));

        Assert.True(updated.Succeeded);
        Assert.Equal(2, updated.Value!.Versions.ScheduleVersion);
        Assert.True(staleTimer.Succeeded);
        Assert.False(staleTimer.Value!.Applied);
        Assert.Equal(VersioningErrorCode.StaleScheduleTask, staleTimer.Value.ErrorCode);
        Assert.Equal(ExamLifecycle.Scheduled, staleTimer.Value.Snapshot.LifecycleState);
        Assert.False(conflict.Succeeded);
        Assert.Equal(VersioningErrorCode.VersionConflict, conflict.ErrorCode);
    }

    [Fact]
    public void Legacy_time_correction_replay_does_not_return_a_running_exam_to_waiting()
    {
        var service = CreateService(now: BaseTime.AddHours(1));
        var started = service.GetExam("exam-1").Value!;

        var result = service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddHours(2), BaseTime.AddHours(4)),
                BaseTime.AddHours(1).AddMinutes(5)));

        Assert.Equal(ExamLifecycle.Running, started.LifecycleState);
        Assert.True(result.Succeeded);
        Assert.Equal(ExamLifecycle.Running, result.Value!.LifecycleState);
        Assert.Equal(started.ActualStartedAt, result.Value.ActualStartedAt);
    }

    [Fact]
    public void Current_schedule_timers_apply_once_and_do_not_reverse_terminal_state()
    {
        var service = CreateService();

        var started = service.ExecuteScheduleTrigger(
            new ExecuteScheduleTriggerRequest(
                "exam-1",
                1,
                ScheduleTriggerKind.Start,
                BaseTime.AddHours(1)));
        var repeatedStart = service.ExecuteScheduleTrigger(
            new ExecuteScheduleTriggerRequest(
                "exam-1",
                1,
                ScheduleTriggerKind.Start,
                BaseTime.AddHours(1).AddMinutes(1)));
        var ended = service.ExecuteScheduleTrigger(
            new ExecuteScheduleTriggerRequest(
                "exam-1",
                1,
                ScheduleTriggerKind.End,
                BaseTime.AddHours(2)));
        var lateStart = service.ExecuteScheduleTrigger(
            new ExecuteScheduleTriggerRequest(
                "exam-1",
                1,
                ScheduleTriggerKind.Start,
                BaseTime.AddHours(3)));

        Assert.True(started.Value!.Applied);
        Assert.Equal(ExamLifecycle.Running, started.Value.Snapshot.LifecycleState);
        Assert.False(repeatedStart.Value!.Applied);
        Assert.True(ended.Value!.Applied);
        Assert.Equal(ExamLifecycle.Ended, ended.Value.Snapshot.LifecycleState);
        Assert.False(lateStart.Value!.Applied);
        Assert.Equal(ExamLifecycle.Ended, lateStart.Value.Snapshot.LifecycleState);
    }

    [Fact]
    public void Replaying_a_legacy_ended_exam_correction_does_not_reopen_or_allow_new_participants()
    {
        var service = CreateService(now: BaseTime.AddHours(3));

        var updated = service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddHours(1), BaseTime.AddHours(6)),
                BaseTime.AddHours(3)));
        var registration = service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "late-participant",
                ParticipationBusinessState.Answering));

        Assert.True(updated.Succeeded);
        Assert.Equal(ExamLifecycle.Ended, updated.Value!.LifecycleState);
        Assert.NotNull(updated.Value.ActualEndedAt);
        Assert.False(registration.Succeeded);
        Assert.Equal(VersioningErrorCode.ExamEnded, registration.ErrorCode);
    }

    [Fact]
    public void Submitted_participant_cannot_be_restored_by_later_changes()
    {
        var service = CreateService();
        Assert.True(service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "p1",
                ParticipationBusinessState.Waiting)).Succeeded);
        Assert.True(service.ChangeParticipantState(
            new ChangeParticipantStateRequest("exam-1", "p1", ParticipationBusinessState.Submitted)).Succeeded);

        var schedule = service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddMinutes(-1), BaseTime.AddHours(2)),
                BaseTime));
        var restore = service.ChangeParticipantState(
            new ChangeParticipantStateRequest("exam-1", "p1", ParticipationBusinessState.Answering));

        Assert.True(schedule.Succeeded);
        Assert.Equal(ExamLifecycle.Running, schedule.Value!.LifecycleState);
        Assert.Equal(ParticipationBusinessState.Submitted, schedule.Value.Participants["p1"].ExamState);
        Assert.False(restore.Succeeded);
        Assert.Equal(VersioningErrorCode.InvalidStateTransition, restore.ErrorCode);
    }

    [Fact]
    public void Not_participating_is_rejected_for_registration_and_backward_transition()
    {
        var service = CreateService();

        var registration = service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "not-entered",
                ParticipationBusinessState.NotParticipating));
        Assert.True(service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "p1",
                ParticipationBusinessState.Waiting)).Succeeded);
        var rollback = service.ChangeParticipantState(
            new ChangeParticipantStateRequest(
                "exam-1",
                "p1",
                ParticipationBusinessState.NotParticipating));

        Assert.False(registration.Succeeded);
        Assert.Equal(VersioningErrorCode.InvalidStateTransition, registration.ErrorCode);
        Assert.False(rollback.Succeeded);
        Assert.Equal(VersioningErrorCode.InvalidStateTransition, rollback.ErrorCode);
        Assert.Equal(
            ParticipationBusinessState.Waiting,
            service.GetExam("exam-1").Value!.Participants["p1"].ExamState);
    }

    [Fact]
    public void Paper_requires_all_prepublication_checks_and_versions_increase_monotonically()
    {
        var service = CreateService();

        var rejected = service.PublishPaper(PaperRequest(
            expectedVersion: 0,
            validation: new PaperPublicationValidation(true, true, false, true)));
        var first = service.PublishPaper(PaperRequest(expectedVersion: 0));
        var stale = service.PublishPaper(PaperRequest(expectedVersion: 0, artifactId: "paper-v2"));
        var second = service.PublishPaper(PaperRequest(expectedVersion: 1, artifactId: "paper-v2"));

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.PublicationNotReady, rejected.ErrorCode);
        Assert.Equal(1, first.Value!.Versions.PaperVersion);
        Assert.False(stale.Succeeded);
        Assert.Equal(VersioningErrorCode.VersionConflict, stale.ErrorCode);
        Assert.Equal(2, second.Value!.Versions.PaperVersion);
    }

    [Fact]
    public void Paper_requires_positive_unique_whole_paper_question_numbers()
    {
        var service = CreateService();
        var nonPositive = new PaperStructure(
            [new PaperQuestionStructure("q1", 0, "subject", "group", "single", ["A", "B"])]);
        var duplicate = new PaperStructure(
            [
                new PaperQuestionStructure("q1", 1, "subject", "group", "single", ["A", "B"]),
                new PaperQuestionStructure("q2", 1, "subject", "group", "single", ["A", "B"])
            ]);

        var nonPositiveResult = service.PublishPaper(PaperRequest(0, structure: nonPositive));
        var duplicateResult = service.PublishPaper(PaperRequest(0, structure: duplicate));

        Assert.False(nonPositiveResult.Succeeded);
        Assert.Equal(VersioningErrorCode.InvalidRequest, nonPositiveResult.ErrorCode);
        Assert.False(duplicateResult.Succeeded);
        Assert.Equal(VersioningErrorCode.InvalidRequest, duplicateResult.ErrorCode);
        Assert.Equal(0, service.GetExam("exam-1").Value!.Versions.PaperVersion);
    }

    [Fact]
    public void Structural_change_is_rejected_after_start_but_content_revision_is_allowed()
    {
        var service = CreateService();
        Assert.True(service.PublishPaper(PaperRequest(0)).Succeeded);
        Assert.True(service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddMinutes(-1), BaseTime.AddHours(2)),
                BaseTime)).Succeeded);

        var changedStructure = new PaperStructure(
            [new PaperQuestionStructure("q1", 1, "subject", "group", "single", ["A", "B", "C"])]);
        var rejected = service.PublishPaper(PaperRequest(
            1,
            artifactId: "paper-v2-structural",
            structure: changedStructure,
            changedQuestionIds: ["q1"]));
        var accepted = service.PublishPaper(PaperRequest(
            1,
            artifactId: "paper-v2-content",
            changedQuestionIds: ["q1"]));

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.StructuralChangeRejected, rejected.ErrorCode);
        Assert.True(accepted.Succeeded);
        Assert.Equal(2, accepted.Value!.Versions.PaperVersion);
        Assert.Equal(ExamLifecycle.Running, accepted.Value.LifecycleState);
    }

    [Fact]
    public void Changing_only_the_whole_paper_question_number_is_rejected_after_start()
    {
        var service = CreateService();
        Assert.True(service.PublishPaper(PaperRequest(0)).Succeeded);
        Assert.True(service.UpdateSchedule(
            new UpdateScheduleRequest(
                "exam-1",
                1,
                new ScheduleWindow(BaseTime.AddMinutes(-1), BaseTime.AddHours(2)),
                BaseTime)).Succeeded);

        var renumbered = new PaperStructure(
            [new PaperQuestionStructure("q1", 2, "subject", "group", "single", ["A", "B"])]);
        var result = service.PublishPaper(PaperRequest(
            1,
            artifactId: "paper-v2-renumbered",
            structure: renumbered,
            changedQuestionIds: ["q1"]));

        Assert.False(result.Succeeded);
        Assert.Equal(VersioningErrorCode.StructuralChangeRejected, result.ErrorCode);
        Assert.Equal(1, service.GetExam("exam-1").Value!.Versions.PaperVersion);
    }

    [Fact]
    public void Paper_progress_distinguishes_failure_update_and_required_review()
    {
        var service = CreateService();
        Assert.True(service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "p1",
                ParticipationBusinessState.Waiting)).Succeeded);
        Assert.True(service.PublishPaper(PaperRequest(
            0,
            changedQuestionIds: ["q1"])).Succeeded);

        var initial = service.GetExam("exam-1").Value!.Participants["p1"];
        var failed = service.ReportPaperTransfer(
            new ReportPaperTransferRequest(
                "exam-1",
                "p1",
                1,
                0,
                PaperTransferOutcome.DownloadFailed,
                "package validation failed"));
        var installed = service.ReportPaperTransfer(
            new ReportPaperTransferRequest(
                "exam-1",
                "p1",
                1,
                1,
                PaperTransferOutcome.Installed,
                null));
        var confirmed = service.ConfirmPaperReview(
            new ConfirmPaperReviewRequest("exam-1", "p1", 1));

        Assert.Equal(PaperUpdateState.NotReceived, initial.PaperUpdateState);
        Assert.Equal(PaperUpdateState.DownloadFailed, failed.Value!.Participants["p1"].PaperUpdateState);
        Assert.Equal("package validation failed", failed.Value.Participants["p1"].LastError);
        Assert.Equal(PaperUpdateState.ReviewRequired, installed.Value!.Participants["p1"].PaperUpdateState);
        Assert.Equal(PaperUpdateState.Updated, confirmed.Value!.Participants["p1"].PaperUpdateState);
    }

    [Fact]
    public void Review_confirmation_replay_after_lost_receipt_is_idempotent()
    {
        var service = CreateService();
        Assert.True(service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "p1",
                ParticipationBusinessState.Waiting)).Succeeded);
        Assert.True(service.PublishPaper(PaperRequest(0, changedQuestionIds: ["q1"])).Succeeded);
        Assert.True(service.ReportPaperTransfer(new ReportPaperTransferRequest(
            "exam-1", "p1", 1, 1, PaperTransferOutcome.Installed, null)).Succeeded);
        Assert.True(service.ConfirmPaperReview(new ConfirmPaperReviewRequest("exam-1", "p1", 1)).Succeeded);

        // 失焦记录 服务端闭环：学生端回执丢失后用新请求标识重发同一版本的确认，按幂等重放成功。
        var replay = service.ConfirmPaperReview(new ConfirmPaperReviewRequest("exam-1", "p1", 1));

        Assert.True(replay.Succeeded);
        Assert.Equal(PaperUpdateState.Updated, replay.Value!.Participants["p1"].PaperUpdateState);
    }

    [Fact]
    public void Client_cannot_roll_back_an_installed_paper_or_complete_an_old_target()
    {
        var service = CreateService();
        Assert.True(service.RegisterParticipant(
            new RegisterVersionedParticipantRequest(
                "exam-1",
                "p1",
                ParticipationBusinessState.Waiting)).Succeeded);
        Assert.True(service.PublishPaper(PaperRequest(0)).Succeeded);
        Assert.True(service.ReportPaperTransfer(
            new ReportPaperTransferRequest(
                "exam-1", "p1", 1, 1, PaperTransferOutcome.Installed, null)).Succeeded);
        Assert.True(service.PublishPaper(PaperRequest(1, artifactId: "paper-v2")).Succeeded);

        var oldTarget = service.ReportPaperTransfer(
            new ReportPaperTransferRequest(
                "exam-1", "p1", 1, 1, PaperTransferOutcome.Installed, null));
        var rollback = service.ReportPaperTransfer(
            new ReportPaperTransferRequest(
                "exam-1", "p1", 2, 0, PaperTransferOutcome.DownloadFailed, "old cache"));

        Assert.False(oldTarget.Succeeded);
        Assert.Equal(VersioningErrorCode.VersionConflict, oldTarget.ErrorCode);
        Assert.False(rollback.Succeeded);
        Assert.Equal(VersioningErrorCode.ResourceRollbackRejected, rollback.ErrorCode);
    }

    [Fact]
    public void Grading_rules_have_an_independent_validated_version_sequence()
    {
        var service = CreateService();

        var invalid = service.PublishGradingRules(
            new PublishGradingRulesRequest(
                "exam-1",
                0,
                "rules-v1",
                new GradingPublicationValidation(true, true, false),
                "operator",
                "correction",
                BaseTime));
        var first = service.PublishGradingRules(
            new PublishGradingRulesRequest(
                "exam-1",
                0,
                "rules-v1",
                new GradingPublicationValidation(true, true, true),
                "operator",
                "correction",
                BaseTime));

        Assert.False(invalid.Succeeded);
        Assert.Equal(VersioningErrorCode.PublicationNotReady, invalid.ErrorCode);
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Value!.Versions.GradingVersion);
        Assert.Equal(0, first.Value.Versions.PaperVersion);
    }

    [Fact]
    public async Task Concurrent_updates_with_the_same_expected_version_have_one_winner()
    {
        var service = CreateService();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 32)
            .Select(async index =>
            {
                await ready.Task;
                return service.UpdateSchedule(
                    new UpdateScheduleRequest(
                        "exam-1",
                        1,
                        new ScheduleWindow(BaseTime.AddHours(1), BaseTime.AddHours(3).AddMinutes(index)),
                        BaseTime));
            })
            .ToArray();

        ready.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Succeeded);
        Assert.Equal(31, results.Count(result => result.ErrorCode == VersioningErrorCode.VersionConflict));
        Assert.Equal(2, service.GetExam("exam-1").Value!.Versions.ScheduleVersion);
    }

    private static InMemoryExamVersioningService CreateService(DateTimeOffset? now = null)
    {
        var service = new InMemoryExamVersioningService();
        var result = service.CreateExam(
            new CreateVersionedExamRequest(
                "exam-1",
                new ScheduleWindow(BaseTime.AddHours(1), BaseTime.AddHours(2)),
                now ?? BaseTime));
        Assert.True(result.Succeeded);
        return service;
    }

    private static PublishPaperRequest PaperRequest(
        long expectedVersion,
        string artifactId = "paper-v1",
        PaperStructure? structure = null,
        IReadOnlyList<string>? changedQuestionIds = null,
        PaperPublicationValidation? validation = null) =>
        new(
            "exam-1",
            expectedVersion,
            artifactId,
            structure ?? DefaultStructure(),
            changedQuestionIds ?? [],
            validation ?? new PaperPublicationValidation(true, true, true, true),
            "operator",
            "content correction",
            BaseTime);

    private static PaperStructure DefaultStructure() =>
        new(
            [new PaperQuestionStructure("q1", 1, "subject", "group", "single", ["A", "B"])]);
}
