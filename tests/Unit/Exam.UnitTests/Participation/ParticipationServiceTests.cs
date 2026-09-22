using Exam.Application.Activation;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using static Exam.UnitTests.Activation.ActivationServiceTests;

namespace Exam.UnitTests.Participation;

public sealed class ParticipationServiceTests
{
    [Fact]
    public async Task Login_RequiresActiveCredentialAndExactRosterIdentity()
    {
        var fixture = await Fixture.CreateAsync();

        var mismatch = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.LoginAsync(new(
                "login-wrong-name",
                "device-1",
                "secret-1",
                "错误姓名",
                "001",
                "0001")).AsTask());
        Assert.Equal(ParticipationErrorCodes.RosterMismatch, mismatch.Code);

        var badCredential = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.LoginAsync(new(
                "login-wrong-secret",
                "device-1",
                "wrong",
                "张三",
                "001",
                "0001")).AsTask());
        Assert.Equal(ParticipationErrorCodes.NotActivated, badCredential.Code);

        var session = await fixture.LoginAsync("device-1", "secret-1", "login-ok");
        Assert.Equal([Fixture.ExamId], session.ExamIds);
        Assert.Equal("0001", session.Candidate.AdmissionNo);
    }

    [Fact]
    public async Task PreparationDoesNotCountAsParticipation_ConfirmAndWaitingWithdrawalDo()
    {
        var fixture = await Fixture.CreateAsync();
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");

        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));
        Assert.Equal(EntryPreparationStatus.Ready, preparation.Status);
        Assert.Null(preparation.ParticipationId);
        Assert.Empty(await fixture.Service.ListActualParticipationsAsync(Fixture.ExamId));

        var participation = await fixture.ConfirmAsync(session, preparation, "confirm-1");
        Assert.Equal(ParticipationBusinessState.Waiting, participation.BusinessState);
        Assert.Equal(1, participation.EntryVersion);
        Assert.Single(await fixture.Service.ListActualParticipationsAsync(Fixture.ExamId));

        var withdrawn = await fixture.Service.WithdrawAsync(new(
            "withdraw-1",
            session.SessionId,
            participation.ParticipationId,
            participation.Version));
        Assert.Equal(ParticipationBusinessState.NotParticipating, withdrawn.BusinessState);
        Assert.Empty(await fixture.Service.ListActualParticipationsAsync(Fixture.ExamId));

        var repeated = await fixture.Service.WithdrawAsync(new(
            "withdraw-2",
            session.SessionId,
            participation.ParticipationId,
            participation.Version));
        Assert.Equal(withdrawn, repeated);

        var secondPreparation = await fixture.Service.PrepareEntryAsync(
            new("prepare-2", session.SessionId, Fixture.ExamId));
        var reentered = await fixture.ConfirmAsync(session, secondPreparation, "confirm-2");
        Assert.Equal(2, reentered.EntryVersion);
        Assert.Equal(2, reentered.AuthorizationVersion);
    }

    [Fact]
    public async Task RunningExam_ConfirmsAsAnsweringWithUnlock_AndCannotWithdraw()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.SetExam(ExamLifecycle.Running, "protected-key-reference");
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));

        var participation = await fixture.ConfirmAsync(session, preparation, "confirm-1");

        Assert.Equal(ParticipationBusinessState.Answering, participation.BusinessState);
        Assert.Equal("protected-key-reference", participation.UnlockReference);
        var error = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.WithdrawAsync(new(
                "withdraw-1",
                session.SessionId,
                participation.ParticipationId,
                participation.Version)).AsTask());
        Assert.Equal(ParticipationErrorCodes.InvalidBusinessState, error.Code);
    }

    [Fact]
    public async Task SecondDeviceWaitsForDecision_OriginalRemainsValid_ThenOldAuthorizationIsRejected()
    {
        var fixture = await Fixture.CreateAsync();
        var firstSession = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var firstPreparation = await fixture.Service.PrepareEntryAsync(
            new("prepare-1", firstSession.SessionId, Fixture.ExamId));
        var participation = await fixture.ConfirmAsync(firstSession, firstPreparation, "confirm-1");

        var secondSession = await fixture.LoginAsync("device-2", "secret-2", "login-2");
        var pending = await fixture.Service.PrepareEntryAsync(
            new("prepare-2", secondSession.SessionId, Fixture.ExamId));
        Assert.Equal(EntryPreparationStatus.DeviceDecisionPending, pending.Status);
        Assert.Equal("device-1", pending.AuthorizedDeviceId);
        Assert.Equal("device-2", pending.PendingDeviceId);

        var beforeDecision = await fixture.Service.EnsureAuthorizedForWriteAsync(new(
            participation.ParticipationId,
            "device-1",
            participation.EntryVersion,
            participation.AuthorizationVersion));
        Assert.Equal("device-1", beforeDecision.AuthorizedDeviceId);

        var decision = await fixture.Service.DecideDeviceAsync(new(
            "decision-1",
            participation.ParticipationId,
            "device-2",
            participation.Version,
            "admin-1",
            "原设备硬件异常"));
        Assert.Equal("device-2", decision.AuthorizedDeviceId);
        Assert.Equal("device-1", decision.RevokedDeviceId);
        Assert.Equal(participation.AuthorizationVersion + 1, decision.AuthorizationVersion);

        var revoked = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.EnsureAuthorizedForWriteAsync(new(
                participation.ParticipationId,
                "device-1",
                participation.EntryVersion,
                participation.AuthorizationVersion)).AsTask());
        Assert.Equal(ParticipationErrorCodes.AuthorizationRevoked, revoked.Code);

        var selected = await fixture.Service.EnsureAuthorizedForWriteAsync(new(
            participation.ParticipationId,
            "device-2",
            participation.EntryVersion,
            decision.AuthorizationVersion));
        Assert.Equal("device-2", selected.AuthorizedDeviceId);
    }

    [Fact]
    public async Task KeepingOriginalDevice_StillAdvancesAuthorizationVersion()
    {
        var fixture = await Fixture.CreateAsync();
        var firstSession = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", firstSession.SessionId, Fixture.ExamId));
        var participation = await fixture.ConfirmAsync(firstSession, preparation, "confirm-1");
        var secondSession = await fixture.LoginAsync("device-2", "secret-2", "login-2");
        await fixture.Service.PrepareEntryAsync(new("prepare-2", secondSession.SessionId, Fixture.ExamId));

        var decision = await fixture.Service.DecideDeviceAsync(new(
            "decision-1",
            participation.ParticipationId,
            "device-1",
            participation.Version,
            "admin-1",
            "保留同步进度较新的原设备"));

        Assert.Equal("device-1", decision.AuthorizedDeviceId);
        Assert.True(decision.AuthorizationVersion > participation.AuthorizationVersion);
        var oldVersion = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.EnsureAuthorizedForWriteAsync(new(
                participation.ParticipationId,
                "device-1",
                participation.EntryVersion,
                participation.AuthorizationVersion)).AsTask());
        Assert.Equal(ParticipationErrorCodes.AuthorizationRevoked, oldVersion.Code);
    }

    [Fact]
    public async Task SubmittedParticipationCannotBeReenteredOrChangedByPendingConflict()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.SetExam(ExamLifecycle.Running, "key-ref");
        var firstSession = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", firstSession.SessionId, Fixture.ExamId));
        var participation = await fixture.ConfirmAsync(firstSession, preparation, "confirm-1");
        var secondSession = await fixture.LoginAsync("device-2", "secret-2", "login-2");
        await fixture.Service.PrepareEntryAsync(new("prepare-2", secondSession.SessionId, Fixture.ExamId));

        var submitted = await fixture.Service.FinalizeAsync(new(
            "finalize-1",
            participation.ParticipationId,
            "device-1",
            participation.EntryVersion,
            participation.AuthorizationVersion));
        Assert.Equal(ParticipationBusinessState.Submitted, submitted.BusinessState);

        var decisionError = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.DecideDeviceAsync(new(
                "decision-after-submit",
                participation.ParticipationId,
                "device-2",
                submitted.Version,
                "admin-1",
                "尝试换机")).AsTask());
        Assert.Equal(ParticipationErrorCodes.AlreadySubmitted, decisionError.Code);

        var entryError = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.PrepareEntryAsync(new("prepare-after-submit", firstSession.SessionId, Fixture.ExamId)).AsTask());
        Assert.Equal(ParticipationErrorCodes.AlreadySubmitted, entryError.Code);
    }

    [Fact]
    public async Task ConfirmationRechecksCurrentResourceVersionAndEntryWindow()
    {
        var fixture = await Fixture.CreateAsync();
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));
        fixture.SetExam(ExamLifecycle.Scheduled, null, paperVersion: 2);

        var versionError = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.ConfirmAsync(session, preparation, "confirm-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.PreparationVersionMismatch, versionError.Code);

        fixture.Clock.SetUtcNow(Fixture.Start.AddMinutes(-21));
        var timeError = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.PrepareEntryAsync(new("prepare-too-early", session.SessionId, Fixture.ExamId)).AsTask());
        Assert.Equal(ParticipationErrorCodes.EntryNotAllowed, timeError.Code);
    }

    [Fact]
    public async Task IdempotentConfirmationReturnsSameRecord_AndPayloadChangeIsRejected()
    {
        var fixture = await Fixture.CreateAsync();
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));
        var request = new ConfirmParticipationRequest(
            "confirm-same",
            session.SessionId,
            Fixture.ExamId,
            preparation.PreparationId,
            preparation.Resources!.PaperVersion,
            preparation.Resources.InstructionsVersion);

        var first = await fixture.Service.ConfirmParticipationAsync(request);
        var replay = await fixture.Service.ConfirmParticipationAsync(request);
        Assert.Equal(first, replay);

        var conflict = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.ConfirmParticipationAsync(request with { DownloadedPaperVersion = 99 }).AsTask());
        Assert.Equal(ParticipationErrorCodes.IdempotencyConflict, conflict.Code);
    }

    [Fact]
    public async Task SameRosterIdentityMayUseDifferentInternalCandidateIdsAcrossExams()
    {
        var fixture = await Fixture.CreateAsync();
        const string secondExamId = "exam-2";
        fixture.Roster.Add(new CandidateRosterEntry(
            secondExamId,
            new CandidateIdentity("candidate-for-exam-2", "张三", "001", "0001")));
        fixture.Exams.Set(new ExamEntrySnapshot(
            secondExamId,
            Fixture.Start,
            Fixture.Start.AddHours(2),
            ExamLifecycle.Scheduled,
            true,
            true,
            "https://exam.invalid/packages/exam-2.enc",
            1024,
            1,
            1,
            null));

        var session = await fixture.LoginAsync("device-1", "secret-1", "login-multi-exam");
        var preparation = await fixture.Service.PrepareEntryAsync(
            new("prepare-exam-2", session.SessionId, secondExamId));
        var participation = await fixture.Service.ConfirmParticipationAsync(new(
            "confirm-exam-2",
            session.SessionId,
            secondExamId,
            preparation.PreparationId,
            1,
            1));

        Assert.Equal([Fixture.ExamId, secondExamId], session.ExamIds);
        Assert.Equal("candidate-for-exam-2", participation.CandidateId);
    }

    [Fact]
    public async Task RevokedActivationCannotContinueWritingWithAnExistingSession()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.SetExam(ExamLifecycle.Running, "key-ref");
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));
        var participation = await fixture.ConfirmAsync(session, preparation, "confirm-1");
        await fixture.Activation.RevokeAsync(new("revoke-device-1", "device-1", "考务撤销"));

        var error = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.EnsureAuthorizedForWriteAsync(new(
                participation.ParticipationId,
                participation.AuthorizedDeviceId,
                participation.EntryVersion,
                participation.AuthorizationVersion)).AsTask());

        Assert.Equal(ParticipationErrorCodes.NotActivated, error.Code);
    }

    [Fact]
    public async Task WithdrawalDoesNotReleaseDeviceAuthorization_SecondDeviceStillNeedsDecisionBeforeReentry()
    {
        var fixture = await Fixture.CreateAsync();
        var firstSession = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var firstPreparation = await fixture.Service.PrepareEntryAsync(
            new("prepare-1", firstSession.SessionId, Fixture.ExamId));
        var initial = await fixture.ConfirmAsync(firstSession, firstPreparation, "confirm-1");
        var withdrawn = await fixture.Service.WithdrawAsync(new(
            "withdraw-1",
            firstSession.SessionId,
            initial.ParticipationId,
            initial.Version));

        var secondSession = await fixture.LoginAsync("device-2", "secret-2", "login-2");
        var pending = await fixture.Service.PrepareEntryAsync(
            new("prepare-device-2", secondSession.SessionId, Fixture.ExamId));
        Assert.Equal(EntryPreparationStatus.DeviceDecisionPending, pending.Status);

        var beforeDecision = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.ConfirmParticipationAsync(new(
                "confirm-before-decision",
                secondSession.SessionId,
                Fixture.ExamId,
                pending.PreparationId,
                1,
                1)).AsTask());
        Assert.Equal(ParticipationErrorCodes.DeviceDecisionPending, beforeDecision.Code);

        var decision = await fixture.Service.DecideDeviceAsync(new(
            "decision-after-withdraw",
            initial.ParticipationId,
            "device-2",
            withdrawn.Version,
            "admin-1",
            "允许备用设备重新进入"));
        var newPreparation = await fixture.Service.PrepareEntryAsync(
            new("prepare-device-2-after-decision", secondSession.SessionId, Fixture.ExamId));
        var reentered = await fixture.ConfirmAsync(secondSession, newPreparation, "confirm-after-decision");

        Assert.Equal(2, reentered.EntryVersion);
        Assert.True(reentered.AuthorizationVersion > decision.AuthorizationVersion);
        Assert.True(decision.AuthorizationVersion > initial.AuthorizationVersion);
        Assert.Equal("device-2", reentered.AuthorizedDeviceId);
    }

    [Fact]
    public async Task ExamStartAndWaitingWithdrawal_AreSerializedByTheSameTransaction()
    {
        var fixture = await Fixture.CreateAsync();
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));
        var waiting = await fixture.ConfirmAsync(session, preparation, "confirm-1");

        using var barrier = new Barrier(2);
        var startTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await fixture.Service.AdvanceExamToAnsweringAsync(new("start-1", Fixture.ExamId));
        });
        var withdrawalTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await Record.ExceptionAsync(() => fixture.Service.WithdrawAsync(new(
                "withdraw-race",
                session.SessionId,
                waiting.ParticipationId,
                waiting.Version)).AsTask());
        });
        var startResult = await startTask;
        var withdrawalError = await withdrawalTask;

        var current = await fixture.Service.FindParticipationAsync(waiting.ParticipationId);
        if (withdrawalError is null)
        {
            Assert.Equal(0, startResult.ChangedParticipationCount);
            Assert.Equal(ParticipationBusinessState.NotParticipating, current!.BusinessState);
        }
        else
        {
            var error = Assert.IsType<ParticipationRuleException>(withdrawalError);
            Assert.True(error.Code is ParticipationErrorCodes.VersionConflict or ParticipationErrorCodes.InvalidBusinessState);
            Assert.Equal(1, startResult.ChangedParticipationCount);
            Assert.Equal(ParticipationBusinessState.Answering, current!.BusinessState);
        }
    }

    [Fact]
    public async Task FinalizeRejectsWaiting_AndAcceptsAnsweringOnly()
    {
        var fixture = await Fixture.CreateAsync();
        var session = await fixture.LoginAsync("device-1", "secret-1", "login-1");
        var preparation = await fixture.Service.PrepareEntryAsync(new("prepare-1", session.SessionId, Fixture.ExamId));
        var waiting = await fixture.ConfirmAsync(session, preparation, "confirm-1");

        var waitingError = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            fixture.Service.FinalizeAsync(new(
                "finalize-waiting",
                waiting.ParticipationId,
                waiting.AuthorizedDeviceId,
                waiting.EntryVersion,
                waiting.AuthorizationVersion)).AsTask());
        Assert.Equal(ParticipationErrorCodes.InvalidBusinessState, waitingError.Code);

        await fixture.Service.AdvanceExamToAnsweringAsync(new("start-1", Fixture.ExamId));
        var answering = await fixture.Service.FindParticipationAsync(waiting.ParticipationId);
        var submitted = await fixture.Service.FinalizeAsync(new(
            "finalize-answering",
            answering!.ParticipationId,
            answering.AuthorizedDeviceId,
            answering.EntryVersion,
            answering.AuthorizationVersion));
        Assert.Equal(ParticipationBusinessState.Submitted, submitted.BusinessState);
    }

    private sealed class Fixture
    {
        public const string ExamId = "exam-1";
        public static readonly DateTimeOffset Start = new(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);
        private static readonly CandidateIdentity Candidate = new("candidate-1", "张三", "001", "0001");

        private Fixture(
            MutableTimeProvider clock,
            ActivationService activation,
            ParticipationService service,
            InMemoryCandidateRosterDirectory roster,
            InMemoryExamEntryDirectory exams)
        {
            Clock = clock;
            Activation = activation;
            Service = service;
            Roster = roster;
            Exams = exams;
        }

        public MutableTimeProvider Clock { get; }
        public ActivationService Activation { get; }
        public ParticipationService Service { get; }
        public InMemoryCandidateRosterDirectory Roster { get; }
        public InMemoryExamEntryDirectory Exams { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var clock = new MutableTimeProvider(Start.AddMinutes(-10));
            var activationStore = new InMemoryActivationPersistence();
            var activation = new ActivationService(activationStore, new FastCredentialProtector(), clock);
            await ActivateAsync(activation, "device-1", "secret-1", "1");
            await ActivateAsync(activation, "device-2", "secret-2", "2");

            var roster = new InMemoryCandidateRosterDirectory();
            roster.Add(new CandidateRosterEntry(ExamId, Candidate));
            var exams = new InMemoryExamEntryDirectory();
            var participationStore = new InMemoryParticipationPersistence();
            var service = new ParticipationService(participationStore, activation, roster, exams, clock);
            var fixture = new Fixture(clock, activation, service, roster, exams);
            fixture.SetExam(ExamLifecycle.Scheduled, null);
            return fixture;
        }

        public void SetExam(ExamLifecycle lifecycle, string? unlockReference, long paperVersion = 1) =>
            Exams.Set(new ExamEntrySnapshot(
                ExamId,
                Start,
                Start.AddHours(2),
                lifecycle,
                true,
                true,
                "https://exam.invalid/packages/exam-1.enc",
                1024,
                paperVersion,
                1,
                unlockReference));

        public ValueTask<StudentSessionResult> LoginAsync(string deviceId, string credential, string requestId) =>
            Service.LoginAsync(new LoginStudentRequest(
                requestId,
                deviceId,
                credential,
                Candidate.Name,
                Candidate.ClassText,
                Candidate.AdmissionNo));

        public ValueTask<ParticipationResult> ConfirmAsync(
            StudentSessionResult session,
            EntryPreparationResult preparation,
            string requestId) =>
            Service.ConfirmParticipationAsync(new ConfirmParticipationRequest(
                requestId,
                session.SessionId,
                ExamId,
                preparation.PreparationId,
                preparation.Resources!.PaperVersion,
                preparation.Resources.InstructionsVersion));

        private static async Task ActivateAsync(
            ActivationService activation,
            string deviceId,
            string credential,
            string suffix)
        {
            var request = await activation.SubmitRequestAsync(new($"activate-{suffix}", deviceId, credential));
            await activation.ApproveAsync(new($"approve-{suffix}", request.ActivationRequestId));
        }
    }
}
