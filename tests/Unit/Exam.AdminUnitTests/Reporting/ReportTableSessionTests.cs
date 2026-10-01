using Exam.Admin.Reporting;
using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.AdminUnitTests.Reporting;

/// <summary>All identifiers and scores are synthetic examples, not real candidate data.</summary>
public sealed class ReportTableSessionTests
{
    [Fact]
    public void ExamSwitch_CancelsOldRequestAndRejectsLateResult()
    {
        using var session = new ReportTableSessionViewModel();
        var old = session.BeginLoad("synthetic-exam-a");
        var current = session.BeginLoad("synthetic-exam-b");

        Assert.True(old.CancellationToken.IsCancellationRequested);
        Assert.False(session.TryAcceptPublished(old, Result(1)));
        Assert.False(session.HasTables);
        Assert.Null(session.SelectedTable);
        Assert.True(session.TryAcceptPublished(current, Result(2)));
        Assert.Equal(8, session.Tables.Count);
        Assert.Equal(ReportTableKind.StudentTotals, session.SelectedTable!.Kind);
        Assert.Equal("synthetic-exam-b", session.ExamId);
        Assert.False(session.TryAcceptPublished(current, Result(1)));
    }

    [Fact]
    public void Refresh_HidesOldTableAndKeepsSelectionOnlyWhenItStillExists()
    {
        using var session = new ReportTableSessionViewModel();
        var first = session.BeginLoad("synthetic-exam-a");
        Assert.True(session.TryAcceptPublished(first, Result(2)));
        Assert.True(session.TrySelect(ReportTableKind.SubjectAnswers, "s2"));

        var refresh = session.BeginLoad("synthetic-exam-a");
        Assert.Empty(session.Tables);
        Assert.Null(session.SelectedTable);
        Assert.Equal("正在加载报表", session.UnavailableReason);
        Assert.True(session.TryAcceptPublished(refresh, Result(2)));
        Assert.Equal(ReportTableKind.SubjectAnswers, session.SelectedTable!.Kind);
        Assert.Equal("s2", session.SelectedTable.SubjectId);

        var structureChanged = session.BeginLoad("synthetic-exam-a");
        Assert.True(session.TryAcceptPublished(structureChanged, Result(1)));
        Assert.Equal(ReportTableKind.StudentTotals, session.SelectedTable!.Kind);
        Assert.False(session.TrySelect(ReportTableKind.SubjectAnswers, "s2"));
    }

    [Fact]
    public void InvalidationAndFailure_ClearResultsAndRejectCancelledResponses()
    {
        using var session = new ReportTableSessionViewModel();
        var first = session.BeginLoad("synthetic-exam-a");
        Assert.True(session.TryAcceptPublished(first, Result(1)));
        var pending = session.BeginLoad("synthetic-exam-a");
        session.Invalidate();

        Assert.True(pending.CancellationToken.IsCancellationRequested);
        Assert.False(session.TryAcceptPublished(pending, Result(1)));
        Assert.False(session.TryFail(pending));
        Assert.False(session.HasTables);
        Assert.Null(session.SelectedTable);
        Assert.Equal("报表尚未开放", session.UnavailableReason);

        var failed = session.BeginLoad("synthetic-exam-a");
        Assert.True(session.TryFail(failed));
        Assert.Equal("报表请求失败", session.UnavailableReason);
        Assert.False(session.TryAcceptPublished(failed, Result(1)));
    }

    [Fact]
    public void NewExam_ResetsPriorSubjectSelection()
    {
        using var session = new ReportTableSessionViewModel();
        Assert.True(session.TryAcceptPublished(session.BeginLoad("synthetic-exam-a"), Result(2)));
        Assert.True(session.TrySelect(ReportTableKind.QuestionScores, "s2"));

        Assert.True(session.TryAcceptPublished(session.BeginLoad("synthetic-exam-b"), Result(2)));

        Assert.Equal(ReportTableKind.StudentTotals, session.SelectedTable!.Kind);
        Assert.Null(session.SelectedTable.SubjectId);
    }

    [Fact]
    public void RepeatedRefresh_CancelsEarlierRequestEvenForTheSameExam()
    {
        using var session = new ReportTableSessionViewModel();
        var earlier = session.BeginLoad("synthetic-exam-a");
        var later = session.BeginLoad("synthetic-exam-a");

        Assert.True(earlier.CancellationToken.IsCancellationRequested);
        Assert.False(session.TryFail(earlier));
        Assert.False(session.TryAcceptPublished(earlier, Result(1)));
        Assert.True(session.TryAcceptPublished(later, Result(1)));
        Assert.True(session.HasTables);
    }

    [Fact]
    public void InvalidPublishedResult_IsNotShownAndNextLoadCanRecover()
    {
        using var session = new ReportTableSessionViewModel();
        var report = Result(1);
        var subject = report.Subjects[0];
        var invalid = report with
        {
            Subjects = [subject with { Scores = [subject.Scores[0] with { Score = R(4) }] }]
        };

        Assert.False(session.TryAcceptPublished(session.BeginLoad("synthetic-exam"), invalid));
        Assert.False(session.HasTables);
        Assert.Null(session.SelectedTable);
        Assert.Equal("报表结果无效", session.UnavailableReason);

        Assert.True(session.TryAcceptPublished(session.BeginLoad("synthetic-exam"), report));
        Assert.True(session.HasTables);
    }

    [Fact]
    public void ContradictoryMean_IsHiddenAndReloadRecovers()
    {
        using var session = new ReportTableSessionViewModel();
        var report = Result(1);
        var invalid = report with { TotalMean = report.TotalMean! with { Exact = R(4) } };

        Assert.False(session.TryAcceptPublished(session.BeginLoad("synthetic-exam"), invalid));
        Assert.False(session.HasTables);
        Assert.Null(session.SelectedTable);
        Assert.Equal("报表结果无效", session.UnavailableReason);
        Assert.True(session.TryAcceptPublished(session.BeginLoad("synthetic-exam"), report));
        Assert.True(session.HasTables);
    }

    internal static ReportCalculationResult Result(int subjectCount)
    {
        var participant = new ReportParticipant("synthetic-candidate", "0001", "Synthetic candidate", "01");
        var subjects = Enumerable.Range(1, subjectCount).Select(number =>
            new ReportSubject($"s{number}", $"Synthetic subject {number}", number, R(5),
                [new ReportQuestion($"q{number}", number, R(5))])).ToArray();
        var grades = subjects.Select(subject => new ReportSubjectGrade(subject.SubjectId, R(5),
            [new ReportQuestionGrade(subject.Questions[0].QuestionId, "A", AnswerOutcome.Correct, R(5))]))
            .ToArray();
        var submission = new ReportSubmission("synthetic-submission", "synthetic-exam",
            participant.CandidateId, 1, grades, R(5 * subjectCount));
        return ReportCalculator.Calculate(new ReportCalculationInput("synthetic-exam", 1,
            subjects, [participant], [submission]));
    }

    private static ExactRational R(int value) => ExactRational.FromInteger(value);
}
