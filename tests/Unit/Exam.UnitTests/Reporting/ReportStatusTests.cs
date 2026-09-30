using System.Text.Json;
using Exam.Application.Reporting;
using Exam.Contracts.Reporting;
using Exam.Domain.Reporting;

namespace Exam.UnitTests.Reporting;

// Only synthetic input/status metadata is used.
public sealed partial class ReportWorkbookWriterTests
{
    [Theory]
    [InlineData("negative")]
    [InlineData("collected-overflow")]
    [InlineData("graded-overflow")]
    [InlineData("missing-count")]
    [InlineData("duplicate-missing")]
    [InlineData("zero-ready")]
    [InlineData("missing-rules")]
    [InlineData("missing-snapshot")]
    [InlineData("unavailable-snapshot")]
    [InlineData("invalid-state")]
    public void Status_RejectsContradictoryCountsVersionsAndAvailability(string corruption)
    {
        var ready=Status("synthetic-current");
        var status=corruption switch
        {
            "negative"=>ready with { GradedCount=-1 },
            "collected-overflow"=>ready with { CollectedCount=3 },
            "graded-overflow"=>ready with { GradedCount=3 },
            "missing-count"=>ready with { MissingParticipants=[new("synthetic-missing","0001","合成考生","01")] },
            "duplicate-missing"=>ready with { State=ReportState.Collecting,SnapshotId=null,CollectedCount=0,GradedCount=0,
                MissingParticipants=[new("synthetic-missing","0001","合成考生","01"),new("synthetic-missing","0001","合成考生","01")] },
            "zero-ready"=>ready with { ParticipantCount=0,CollectedCount=0,GradedCount=0 },
            "missing-rules"=>ready with { GradingVersion=0 },
            "missing-snapshot"=>ready with { SnapshotId=null },
            "unavailable-snapshot"=>ready with { State=ReportState.ExamNotEnded },
            "invalid-state"=>ready with { State=(ReportState)999 },
            _=>throw new ArgumentException()
        };
        Assert.Throws<ArgumentException>(()=>ReportStatusValidator.Validate(status));
    }
    [Fact]
    public void Published_RejectsValidStatusWithDifferentParticipantCount()
    {
        var result=ReportCalculator.Calculate(TwoParticipantInput());
        var one=Status("synthetic-current") with { ParticipantCount=1,CollectedCount=1,GradedCount=1 };
        var published=new PublishedReport(1,one,JsonSerializer.SerializeToElement(result,ReportJson.Create()));
        Assert.Throws<ArgumentException>(()=>ReportJson.Read(published));
    }
    [Fact]
    public async Task Publication_RejectsSourceThatReturnsAnotherExam()
    {
        var input=TwoParticipantInput();
        var source=new SequenceReportSource(new ReportSourceSnapshot(Status("synthetic-current") with { ExamId="synthetic-other-exam" },input));
        var reports=new ReportPublicationService(source);
        await Assert.ThrowsAsync<ArgumentException>(()=>reports.StatusAsync(input.ExamId));
        await Assert.ThrowsAsync<ArgumentException>(()=>reports.ReadAsync(input.ExamId));
    }
}
