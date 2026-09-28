using Exam.Domain.Reporting;

namespace Exam.UnitTests.Reporting;

/// <summary>All identifiers and scores are synthetic examples, not real candidate data.</summary>
public sealed partial class ReportCalculatorTests
{
    [Fact]
    public void ThreeHundredSyntheticParticipants_KeepZeroScoresInRanksButUseOneMeanSample()
    {
        var rows = Enumerable.Range(1, 300)
            .Select(index => (
                CandidateId: $"synthetic-{index}",
                ExamNumber: index.ToString("D4"),
                ClassNumber: ((index - 1) % 3 + 1).ToString("D2"),
                First: R(index % 10 == 0 ? 0 : 10),
                Second: R(20)))
            .ToArray();

        var result = ReportCalculator.Calculate(CreateInput(rows));

        Assert.Equal(300, result.StudentTotals.Count);
        Assert.Equal(270, result.CommonSampleCount);
        Assert.Equal("30.00", result.TotalMean!.Display);
        Assert.Equal("10.00", result.Subjects[0].SubjectMean!.Display);
        Assert.Equal("20.00", result.Subjects[1].SubjectMean!.Display);
        Assert.Equal(270, result.StudentTotals.Count(row => row.TotalRank == 1));
        Assert.Equal(30, result.StudentTotals.Count(row => row.TotalRank == 271));
        Assert.Equal(300, result.Subjects[1].Scores.Count(row => row.Rank == 1));
        Assert.Equal([90, 90, 90], result.ClassAverages.Select(row => row.SampleCount));
        Assert.All(result.ClassAverages, row => Assert.Equal("30.00", row.TotalMean!.Display));
    }

    [Fact]
    public void CalculatedResult_IsUnaffectedByLaterChangesToInputCollections()
    {
        var source = CreateInput([("synthetic-1", "0001", "01", R(5), R(7))]);
        var subjects = source.Subjects.ToArray();
        var participants = source.Participants.ToArray();
        var submissions = source.Submissions.ToArray();
        var input = source with { Subjects = subjects, Participants = participants, Submissions = submissions };
        var result = ReportCalculator.Calculate(input);

        subjects[0] = subjects[0] with { DisplayName = "changed" };
        participants[0] = participants[0] with { DisplayName = "changed" };
        submissions[0] = submissions[0] with { TotalScore = R(0) };

        Assert.Equal("Subject A", result.Subjects[0].Subject.DisplayName);
        Assert.Equal("Synthetic candidate", result.StudentTotals[0].Participant.DisplayName);
        Assert.Equal(R(12), result.StudentTotals[0].TotalScore);
        Assert.Equal("12.00", result.TotalMean!.Display);
    }
}
