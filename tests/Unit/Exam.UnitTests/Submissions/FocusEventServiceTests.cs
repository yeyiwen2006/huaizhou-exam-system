using Exam.Application.Submissions;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;

namespace Exam.UnitTests.Submissions;

/// <summary>
/// B06 失焦留痕验收：请求校验、(ParticipationId, EventId) 幂等去重、授权元组核对与考务汇总查询。
/// </summary>
public sealed class FocusEventServiceTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static ReportFocusEventsRequest Request(
        string participationId = "participation-1",
        string? eventId = null,
        int eventCount = 1,
        string? deviceId = null,
        long authorizationVersion = 1) => new(
        Guid.NewGuid().ToString("N"),
        "exam-1",
        "candidate-1",
        participationId,
        1,
        deviceId ?? "device-1",
        authorizationVersion,
        2,
        Enumerable.Range(0, eventCount)
            .Select(index => new ReportedFocusLossEvent(
                eventId ?? $"focus-{index}",
                BaseTime.AddSeconds(index),
                BaseTime.AddSeconds(index + 5),
                5000))
            .ToArray());

    private static async Task<(FocusEventService Service, InMemoryFocusEventStore Store)> CreateAsync()
    {
        var store = new InMemoryFocusEventStore();
        await store.SetAuthorizationAsync(new SubmissionAuthorizationState(
            "exam-1", "candidate-1", "participation-1", 1, "device-1", 1,
            ParticipationBusinessState.Answering, 1, 2));
        var service = new FocusEventService(store, new FakeTimeProvider(BaseTime));
        return (service, store);
    }

    [Fact]
    public async Task Report_ValidRequestPersistsEventsAndReturnsReceipt()
    {
        var (service, _) = await CreateAsync();

        var result = await service.ReportAsync(Request(eventId: "focus-a"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("participation-1", result.Value!.ParticipationId);
        Assert.Equal(["focus-a"], result.Value.AcceptedEventIds);
    }

    [Fact]
    public async Task Report_DuplicateEventIdsAreIdempotentAcrossRetries()
    {
        var (service, store) = await CreateAsync();

        Assert.True((await service.ReportAsync(Request(eventId: "focus-a"))).IsSuccess);
        // 客户端重试（新 RequestId、同一事件标识）：幂等成功，不产生重复记录。
        Assert.True((await service.ReportAsync(Request(eventId: "focus-a"))).IsSuccess);

        var summaries = await store.ListSummariesAsync("exam-1");
        var summary = Assert.Single(summaries);
        Assert.Equal(1, summary.EventCount);
    }

    [Fact]
    public async Task Report_MismatchedAuthorizationTupleIsRejected()
    {
        var (service, _) = await CreateAsync();

        var wrongDevice = await service.ReportAsync(Request(deviceId: "other-device"));
        var wrongAuthorization = await service.ReportAsync(Request(authorizationVersion: 2));

        Assert.False(wrongDevice.IsSuccess);
        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, wrongDevice.ErrorCode);
        Assert.False(wrongAuthorization.IsSuccess);
        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, wrongAuthorization.ErrorCode);
    }

    [Fact]
    public async Task Report_UnknownParticipationIsRejected()
    {
        var (service, _) = await CreateAsync();

        var result = await service.ReportAsync(Request(participationId: "participation-unknown"));

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Report_EmptyOrOversizedBatchIsRejected()
    {
        var (service, _) = await CreateAsync();

        var empty = await service.ReportAsync(Request(eventCount: 0));
        var oversized = await service.ReportAsync(Request(eventCount: 201));

        Assert.False(empty.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, empty.ErrorCode);
        Assert.False(oversized.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, oversized.ErrorCode);
    }

    [Fact]
    public async Task Report_DuplicateEventIdsWithinOneBatchAreRejected()
    {
        var (service, _) = await CreateAsync();

        var request = new ReportFocusEventsRequest(
            Guid.NewGuid().ToString("N"), "exam-1", "candidate-1", "participation-1", 1, "device-1", 1, 2,
            [new ReportedFocusLossEvent("dup", BaseTime, BaseTime.AddSeconds(3), 3000),
             new ReportedFocusLossEvent("dup", BaseTime.AddMinutes(1), BaseTime.AddMinutes(1).AddSeconds(2), 2000)]);
        var result = await service.ReportAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, result.ErrorCode);
    }

    [Fact]
    public async Task Summaries_AggregateCountDurationAndLastLostAtPerCandidate()
    {
        var (service, store) = await CreateAsync();
        await store.SetAuthorizationAsync(new SubmissionAuthorizationState(
            "exam-2", "candidate-2", "participation-2", 1, "device-2", 1,
            ParticipationBusinessState.Answering, 1, 2));

        Assert.True((await service.ReportAsync(new ReportFocusEventsRequest(
            Guid.NewGuid().ToString("N"), "exam-1", "candidate-1", "participation-1", 1, "device-1", 1, 2,
            [new ReportedFocusLossEvent("e1", BaseTime, BaseTime.AddSeconds(3), 3000),
             new ReportedFocusLossEvent("e2", BaseTime.AddMinutes(5), BaseTime.AddMinutes(5).AddSeconds(7), 7000)]))).IsSuccess);
        Assert.True((await service.ReportAsync(new ReportFocusEventsRequest(
            Guid.NewGuid().ToString("N"), "exam-2", "candidate-2", "participation-2", 1, "device-2", 1, 2,
            [new ReportedFocusLossEvent("e3", BaseTime.AddHours(1), BaseTime.AddHours(1).AddSeconds(1), 1000)]))).IsSuccess);

        var examOne = await service.ListSummariesAsync("exam-1");
        var summary = Assert.Single(examOne.Value!);
        Assert.Equal("candidate-1", summary.CandidateId);
        Assert.Equal(2, summary.EventCount);
        Assert.Equal(10000, summary.TotalDurationMilliseconds);
        Assert.Equal(BaseTime.AddMinutes(5), summary.LastLostAt);

        var examTwo = await service.ListSummariesAsync("exam-2");
        Assert.Equal(1, Assert.Single(examTwo.Value!).EventCount);
        Assert.False((await service.ListSummariesAsync("exam-missing")).Value!.Count > 0);
    }

    [Fact]
    public async Task Summaries_EmptyExamIdIsRejected()
    {
        var (service, _) = await CreateAsync();

        var result = await service.ListSummariesAsync(" ");

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, result.ErrorCode);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
