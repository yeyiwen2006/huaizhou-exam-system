using System.Numerics;
using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Reporting;

/// <summary>All identifiers and scores in this file are independent synthetic examples.</summary>
public sealed partial class ReportCalculatorTests
{
    [Fact]
    public void FourParticipantExample_UsesOneCommonSampleAndCompetitionRanks()
    {
        var result = ReportCalculator.Calculate(FourParticipantInput());

        Assert.Equal(["1001", "1002", "1003", "1004"],
            result.StudentTotals.Select(row => row.Participant.ExamNumber));
        Assert.Equal([1, 1, 3, 3], result.StudentTotals.Select(row => row.TotalRank));
        Assert.Equal(3, result.CommonSampleCount);
        Assert.Equal(R(60), result.TotalMaximumScore);
        Assert.Equal(ExactRational.Create(50, 3), result.TotalMean!.Exact);
        Assert.Equal("16.67", result.TotalMean.Display);

        var first = result.Subjects[0];
        Assert.Equal("a", first.Subject.SubjectId);
        Assert.Equal(["1001", "1004", "1003", "1002"],
            first.Scores.Select(row => row.Participant.ExamNumber));
        Assert.Equal([1, 2, 3, 4], first.Scores.Select(row => row.Rank));
        Assert.Equal(ExactRational.Create(23, 3), first.SubjectMean!.Exact);
        Assert.Equal("7.67", first.SubjectMean.Display);
        Assert.Equal(ExactRational.Create(23, 3), Assert.Single(first.QuestionMeans).Mean!.Exact);

        var second = result.Subjects[1];
        Assert.Equal("9.00", second.SubjectMean!.Display);
        Assert.Equal(["1002", "1001", "1003", "1004"],
            second.Scores.Select(row => row.Participant.ExamNumber));
        Assert.Equal("01", Assert.Single(result.ClassAverages).ClassNumber);
        Assert.Equal(3, result.ClassAverages[0].SampleCount);
        Assert.Equal(1, result.ClassAverages[0].TotalRank);
    }

    [Fact]
    public void NumericExamNumbers_KeepLeadingZerosAndDoNotOverflow()
    {
        var input = CreateInput([
            ("one", "10", "01", R(5), R(5)),
            ("two", "2", "01", R(5), R(5)),
            ("three", "0002", "01", R(5), R(5)),
            ("four", "999999999999999999999999999999999999", "01", R(5), R(5))
        ]);

        var result = ReportCalculator.Calculate(input);
        Assert.Equal(["0002", "2", "10", "999999999999999999999999999999999999"],
            result.StudentTotals.Select(row => row.Participant.ExamNumber));
        Assert.All(result.StudentTotals, row => Assert.Equal(1, row.TotalRank));
    }

    [Fact]
    public void EmptyEligibleSample_HasNoMeanOrClassRankButRetainsStudents()
    {
        var input = CreateInput([
            ("one", "1", "01", R(0), R(3)),
            ("two", "2", "02", R(2), R(0))
        ]);

        var result = ReportCalculator.Calculate(input);
        Assert.Equal(2, result.StudentTotals.Count);
        Assert.Equal(0, result.CommonSampleCount);
        Assert.Null(result.TotalMean);
        Assert.All(result.Subjects, table =>
        {
            Assert.Null(table.SubjectMean);
            Assert.Null(Assert.Single(table.QuestionMeans).Mean);
        });
        Assert.All(result.ClassAverages, row =>
        {
            Assert.Equal(0, row.SampleCount);
            Assert.Null(row.TotalMean);
            Assert.Null(row.TotalRank);
            Assert.All(row.Subjects, subject => Assert.Null(subject.Rank));
        });
    }

    [Fact]
    public void RoundedEqualClassMeans_StillRankByExactValue()
    {
        var input = CreateInput([
            ("one", "1", "01", ExactRational.Create(10001, 1000), R(1)),
            ("two", "2", "02", ExactRational.Create(10002, 1000), R(1))
        ]);

        var classes = ReportCalculator.Calculate(input).ClassAverages;
        Assert.Equal(["01", "02"], classes.Select(row => row.ClassNumber));
        Assert.Equal("10.00", classes[0].Subjects[0].Mean!.Display);
        Assert.Equal("10.00", classes[1].Subjects[0].Mean!.Display);
        Assert.Equal(2, classes[0].Subjects[0].Rank);
        Assert.Equal(1, classes[1].Subjects[0].Rank);
    }

    [Fact]
    public void NegativeQuestionScore_DoesNotRemovePositiveSubjectStudent()
    {
        var input = CreateInput([("one", "1", "01", R(2), R(3))]);
        var original = Assert.Single(input.Submissions);
        var subject = original.Subjects[0] with
        {
            Questions = [new ReportQuestionGrade("q-a", "A", AnswerOutcome.Wrong, R(-1)),
                new ReportQuestionGrade("q-a-positive", "B", AnswerOutcome.PartialCorrect, R(3))]
        };
        input = input with
        {
            Subjects = [input.Subjects[0] with
                { Questions = [new("q-a", 1, R(1)), new("q-a-positive", 2, R(29))] }, input.Subjects[1]],
            Submissions = [original with { Subjects = [subject, original.Subjects[1]] }]
        };

        var result = ReportCalculator.Calculate(input);
        Assert.Equal(1, result.CommonSampleCount);
        Assert.Equal(R(-1), result.Subjects[0].QuestionMeans[0].Mean!.Exact);
        Assert.Equal("-1.00", result.Subjects[0].QuestionMeans[0].Mean!.Display);
    }

    [Fact]
    public void AnswerCorrectnessAndFullScoreColorConditions_AreIndependent()
    {
        var input = CreateInput([("one", "1", "01", R(30), R(2))]);
        var original = Assert.Single(input.Submissions);
        var partialAtFullScore = original.Subjects[0] with
        {
            Questions = [new ReportQuestionGrade("q-a", "A", AnswerOutcome.PartialCorrect, R(30))]
        };
        input = input with { Submissions = [original with { Subjects = [partialAtFullScore, original.Subjects[1]] }] };

        var subject = ReportCalculator.Calculate(input).Subjects[0];
        Assert.False(Assert.Single(Assert.Single(subject.Answers).Answers).IsCompletelyCorrect);
        Assert.True(Assert.Single(Assert.Single(subject.QuestionScores).Questions).IsFullScore);
    }

    [Fact]
    public void InvalidOrIncompleteInput_IsRejectedInsteadOfFilledWithZero()
    {
        var input = FourParticipantInput();
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input with
        {
            Submissions = input.Submissions.Skip(1).ToArray()
        }));
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input with
        {
            Submissions = [input.Submissions[0], input.Submissions[0], .. input.Submissions.Skip(2)]
        }));
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input with
        {
            Submissions = [input.Submissions[0] with { GradingVersion = 2 }, .. input.Submissions.Skip(1)]
        }));
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input with
        {
            Submissions = [input.Submissions[0] with
            {
                Subjects = [input.Submissions[0].Subjects[0]]
            }, .. input.Submissions.Skip(1)]
        }));
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input with
        {
            Submissions = [input.Submissions[0] with
            {
                TotalScore = R(0)
            }, .. input.Submissions.Skip(1)]
        }));
    }

    [Fact]
    public void ZeroParticipants_AreExplicitlyRejectedUntilContractIsConfirmed()
    {
        var input = FourParticipantInput() with
        {
            Participants = [],
            Submissions = []
        };
        Assert.Throws<ArgumentException>(() => ReportCalculator.Calculate(input));
    }

    private static ReportCalculationInput FourParticipantInput() => CreateInput([
        ("one", "1001", "01", R(10), R(20)),
        ("two", "1002", "01", R(0), R(30)),
        ("three", "1003", "01", R(5), R(5)),
        ("four", "1004", "01", R(8), R(2))
    ]);

    private static ReportCalculationInput CreateInput(
        IReadOnlyList<(string CandidateId, string ExamNumber, string ClassNumber,
            ExactRational First, ExactRational Second)> rows)
    {
        ReportSubject[] subjects = [
            new("a", "Subject A", 1, R(30), [new("q-a", 1, R(30))]),
            new("b", "Subject B", 2, R(30), [new("q-b", 1, R(30))])
        ];
        var participants = rows.Select(row => new ReportParticipant(row.CandidateId,
            row.ExamNumber, "Synthetic candidate", row.ClassNumber)).ToArray();
        var submissions = rows.Select(row => new ReportSubmission("submission-" + row.CandidateId,
            "synthetic-exam", row.CandidateId, 1,
            [
                new ReportSubjectGrade("a", row.First,
                    [new ReportQuestionGrade("q-a", "A", AnswerOutcome.Correct, row.First)]),
                new ReportSubjectGrade("b", row.Second,
                    [new ReportQuestionGrade("q-b", "B", AnswerOutcome.Correct, row.Second)])
            ], row.First + row.Second)).ToArray();
        return new("synthetic-exam", 1, subjects, participants, submissions);
    }

    private static ExactRational R(int value) => ExactRational.FromInteger(new BigInteger(value));
}
