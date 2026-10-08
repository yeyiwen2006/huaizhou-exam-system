using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

// Synthetic local answers; exercise the same callback used by the live command feed.
public sealed partial class StudentExamRuntimeTests
{
    [Fact]
    public async Task Pushed_extension_updates_student_deadline_and_keeps_answers_editable_after_late_end()
    {
        using var fixture = await Fixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        await runtime.Manager.ConfirmParticipationAsync();
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", fixture.Api.Start(), CancellationToken.None);
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var end = DateTimeOffset.UtcNow.AddHours(1);
        var extension = fixture.Api.Start() with
        {
            EventId = "synthetic-extension", EventType = CommandEventType.ScheduleChanged, StateVersion = 3,
            NewScheduledStart = end.AddHours(-2), NewScheduledEnd = end, ProtectedUnlockReference = null
        };
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", extension, CancellationToken.None);
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", fixture.Api.End(), CancellationToken.None);
        Assert.Equal(end, runtime.Manager.OverriddenScheduledEnd);
        Assert.False(runtime.Manager.ExamEnded);
        Assert.False(runtime.HasFixedSubmission);
        Assert.NotNull(runtime.Paper);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopening_never_unfreezes_an_explicit_or_end_triggered_submission(bool ended)
    {
        using var fixture = await Fixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        await runtime.Manager.ConfirmParticipationAsync();
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", fixture.Api.Start(), CancellationToken.None);
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        if (ended) await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", fixture.Api.End(), CancellationToken.None);
        else Assert.NotNull((await answers.FixSubmissionAsync("synthetic-fixed")).Record);
        var frozen = runtime.FixedSubmission!;
        var resume = fixture.Api.Start() with
        {
            EventId = "synthetic-reopen", StateVersion = 3,
            Metadata = new Dictionary<string, string> { ["reopenedAtScheduleVersion"] = "3" },
            NewScheduledStart = DateTimeOffset.UtcNow.AddHours(-1), NewScheduledEnd = DateTimeOffset.UtcNow.AddHours(1)
        };
        await InvokeRuntimeCallback(runtime, "ApplyCommandAsync", resume, CancellationToken.None);
        Assert.True(runtime.HasFixedSubmission);
        Assert.Equal(frozen.SubmissionId, runtime.FixedSubmission!.SubmissionId);
        Assert.Equal(frozen.FrozenAnswers["q1"].OptionIds, runtime.FixedSubmission.FrozenAnswers["q1"].OptionIds);
        Assert.False((await answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        Assert.Null(runtime.Paper);
        Assert.Throws<InvalidOperationException>(() => runtime.ReadQuestionPdf("q1"));
    }
}
