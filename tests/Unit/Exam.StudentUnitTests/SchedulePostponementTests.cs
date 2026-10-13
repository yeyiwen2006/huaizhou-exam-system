using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed partial class ExamPackageManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postponement_returns_to_waiting_and_survives_restart(bool previouslyEnded)
    {
        using var fixture = await Fixture.CreateAsync();
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var pause = fixture.Api.Start() with
        {
            EventId = "synthetic-postpone", EventType = CommandEventType.ScheduleChanged, StateVersion = 3,
            NewScheduledStart = future, NewScheduledEnd = future.AddHours(1), ProtectedUnlockReference = null,
            Metadata = new Dictionary<string, string> { ["lifecycleState"] = "scheduled", ["reopenedAtScheduleVersion"] = "3" }
        };
        using (var manager = fixture.Manager())
        {
            Assert.True(await manager.PrepareAndDownloadAsync());
            await manager.ConfirmParticipationAsync();
            Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
            if (previouslyEnded)
                Assert.True(await manager.HandleCommandAsync(fixture.Api.Start() with
                { EventId = "synthetic-end", EventType = CommandEventType.End, StateVersion = 2 }));
            Assert.True(await manager.HandleCommandAsync(pause));
            Assert.False(manager.ExamStarted);
            Assert.False(manager.ExamEnded);
            Assert.Null(manager.UnlockedPayload);
            Assert.Equal(ExamPackageManager.Phase.ConfirmedWaiting, manager.CurrentPhase);
            Assert.True(await manager.HandleCommandAsync(fixture.Api.Start() with { EventId = "synthetic-delayed-start" }));
            Assert.Null(manager.UnlockedPayload);
            Assert.Equal(CommandProcessingResult.RejectedAsStale, fixture.Api.Acks[^1].ProcessingResult);
        }
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Scheduled;
        fixture.Api.ScheduleVersion = fixture.Api.ReopenedAtScheduleVersion = 3;
        fixture.Api.ScheduledStart = future;
        fixture.Api.ScheduledEnd = future.AddHours(1);
        using var restored = fixture.Manager();
        Assert.True(await restored.PrepareAndDownloadAsync());
        Assert.True((await restored.ConfirmParticipationAsync())?.IsSuccess);
        Assert.False(restored.ExamStarted);
        Assert.False(restored.ExamEnded);
        Assert.Null(restored.UnlockedPayload);
        var start = pause with
        {
            EventId = "synthetic-new-start", EventType = CommandEventType.Start,
            ProtectedUnlockReference = fixture.Api.Start().ProtectedUnlockReference,
            Metadata = new Dictionary<string, string> { ["lifecycleState"] = "running", ["reopenedAtScheduleVersion"] = "3" }
        };
        Assert.True(await restored.HandleCommandAsync(start));
        Assert.True(restored.ExamStarted);
        Assert.Equal(fixture.Api.Plaintext, restored.UnlockedPayload);
        Assert.True(await restored.HandleCommandAsync(pause with { EventId = "synthetic-late-pause" }));
        Assert.Equal(fixture.Api.Plaintext, restored.UnlockedPayload);
    }
}

public sealed partial class StudentExamRuntimeTests
{
    [Fact]
    public async Task Postponement_pauses_answers_then_resumes_the_same_saved_answers()
    {
        using var fixture = await Fixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        await runtime.Manager.ConfirmParticipationAsync();
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", fixture.Api.Start(), CancellationToken.None);
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var pause = fixture.Api.Start() with
        {
            EventId = "synthetic-postpone", EventType = CommandEventType.ScheduleChanged, StateVersion = 3,
            NewScheduledStart = future, NewScheduledEnd = future.AddHours(1), ProtectedUnlockReference = null,
            Metadata = new Dictionary<string, string> { ["lifecycleState"] = "scheduled", ["reopenedAtScheduleVersion"] = "3" }
        };
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", pause, CancellationToken.None);
        Assert.False(runtime.Manager.ExamEnded);
        Assert.False(runtime.HasFixedSubmission);
        Assert.Null(runtime.Paper);
        Assert.Equal(AnswerSessionStatus.Suspended, answers.Status);
        Assert.False((await answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        Assert.Equal(new[] { "A" }, answers.Answers["q1"].OptionIds);
        var resume = pause with
        {
            EventId = "synthetic-resume", EventType = CommandEventType.Start,
            ProtectedUnlockReference = fixture.Api.Start().ProtectedUnlockReference,
            Metadata = new Dictionary<string, string> { ["lifecycleState"] = "running", ["reopenedAtScheduleVersion"] = "3" }
        };
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", resume, CancellationToken.None);
        Assert.Same(answers, runtime.Answers);
        Assert.NotNull(runtime.Paper);
        Assert.Equal(new[] { "A" }, answers.Answers["q1"].OptionIds);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
    }
}
