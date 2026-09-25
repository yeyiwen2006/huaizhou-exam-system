using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

public sealed class RegradeReviewBoundaryTests
{
    [Fact]
    public void DuplicateCurrentRowsCannotDeclareRecalculationComplete()
    {
        var plan = RegradePlanner.Plan(3, [new("s", 3), new("s", 3)]);
        Assert.False(plan.IsComplete);
        Assert.Single(plan.DuplicateSubmissionIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidSubmissionIdentityIsRejected(string? id) =>
        Assert.ThrowsAny<ArgumentException>(() => RegradePlanner.Plan(1, [new(id!, null)]));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPersistedVersionIsRejected(long version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RegradePlanner.Plan(3, [new("s", version)]));

    [Fact]
    public void DuplicateClassificationDoesNotDependOnInputOrder()
    {
        var first = RegradePlanner.Plan(3, [new("s", 3), new("s", 1)]);
        var reversed = RegradePlanner.Plan(3, [new("s", 1), new("s", 3)]);
        Assert.Equal(first.Pending, reversed.Pending);
        Assert.Equal(first.UpToDate, reversed.UpToDate);
    }

    [Fact]
    public void PlanCollectionsCannotBeMutatedToBypassReadiness()
    {
        var plan = RegradePlanner.Plan(3, [new("s", null)]);
        if (plan.Pending is IList<RegradeItem> items)
            Assert.Throws<NotSupportedException>(() => items.Clear());
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public void FutureResultIsNotClassifiedAsAnOldVersionToDowngrade()
    {
        var plan = RegradePlanner.Plan(2, [new("s", 3)]);
        Assert.False(plan.IsComplete);
        Assert.DoesNotContain(plan.Pending, item => item.Reason == RegradeReason.StaleVersion);
    }
}
