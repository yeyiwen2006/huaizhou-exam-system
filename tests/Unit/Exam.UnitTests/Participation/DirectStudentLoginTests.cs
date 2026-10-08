using Exam.Application.Activation;
using Exam.Application.Participation;
using Exam.Contracts.Participation;
using static Exam.UnitTests.Activation.ActivationServiceTests;

namespace Exam.UnitTests.Participation;

public sealed class DirectStudentLoginTests
{
    [Fact]
    public async Task LoginValidatesRosterBeforeAllocating_AndReusesDeviceAcrossCandidatesAndDays()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero));
        var devices = new ActivationService(new InMemoryActivationPersistence(), new FastCredentialProtector(), clock);
        var roster = new InMemoryCandidateRosterDirectory();
        roster.Add(new("exam", new("candidate-1", "合成考生甲", "01", "0001")));
        roster.Add(new("exam", new("candidate-2", "合成考生乙", "02", "0002")));
        var service = new ParticipationService(new InMemoryParticipationPersistence(), devices, roster,
            new InMemoryExamEntryDirectory(), clock);
        var request = new LoginStudentRequest("login-1", "new-device", "credential", "错误姓名", "01", "0001");
        var mismatch = await Assert.ThrowsAsync<ParticipationRuleException>(() => service.ConnectAndLoginAsync(request, devices).AsTask());
        Assert.Equal(ParticipationErrorCodes.RosterMismatch, mismatch.Code);
        Assert.Null(await devices.FindDeviceAsync(request.DeviceId));

        request = request with { Name = "合成考生甲" };
        var first = await service.ConnectAndLoginAsync(request, devices);
        Assert.Equal("202610080001", first.Device.ClientNumber);
        Assert.Equal("0001", first.Session.Candidate.AdmissionNo);
        Assert.Empty(await devices.ListPendingRequestsAsync());
        Assert.Empty(await service.ListActualParticipationsAsync("exam"));
        var replay = await service.ConnectAndLoginAsync(request, devices);
        Assert.Equal(first.Session.SessionId, replay.Session.SessionId);
        Assert.Equal(first.Session.Candidate, replay.Session.Candidate);
        Assert.Equal(first.Session.ExamIds, replay.Session.ExamIds);
        Assert.Equal(first.Session.CandidateIdsByExam, replay.Session.CandidateIdsByExam);
        Assert.Equal(first.Device.ClientNumber, replay.Device.ClientNumber);

        var second = await service.ConnectAndLoginAsync(request with
            { RequestId = "login-2", Name = "合成考生乙", ClassText = "02", AdmissionNo = "0002" }, devices);
        Assert.Equal(first.Device.ClientNumber, second.Device.ClientNumber);
        Assert.Equal("合成考生乙", second.Session.Candidate.Name);
        clock.SetUtcNow(clock.GetUtcNow().AddDays(1));
        var nextDay = await service.ConnectAndLoginAsync(request with { RequestId = "login-next-day" }, devices);
        Assert.Equal("202610090001", nextDay.Device.ClientNumber);
        Assert.Equal(first.Device.DeviceId, nextDay.Device.DeviceId);
    }
}
