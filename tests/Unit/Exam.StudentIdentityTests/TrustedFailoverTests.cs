using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Participation;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class TrustedFailoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "trusted-failover-" + Guid.NewGuid().ToString("N"));
    private static TrustedServerTopology Topology() => new("logical", [
        new("127.0.0.1", 5101, "primary", new string('1', 64)),
        new("127.0.0.2", 5102, "standby", new string('2', 64))]);
    private static DeviceRecord Device() => new("device", "synthetic-credential", "20261005001", "127.0.0.1", 5101,
        DateTimeOffset.UtcNow, "logical", ActivationStatus.Active, ServerEpoch: 1)
    { Topology = Topology(), ActiveNodeId = "primary" };
    private static SubmitFinalAnswersRequest Submission() => new("fixed-request", "fixed-submission", "exam", "candidate", "participation",
        3, "device", 4, 5, new Dictionary<string, AnswerValue> { ["question"] = AnswerValue.Selected("A") });
    private static HttpResponseMessage Reply<T>(T result, string node = "standby", long epoch = 2, string server = "logical") =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new ApiEnvelope<T>(server, node, epoch, "test", result, null), options: JsonDefaults.Create()) };
    private static HttpResponseMessage Time(string server = "logical", string node = "standby", long epoch = 2) =>
        Reply(new ServerTimeSnapshot(DateTimeOffset.UtcNow, new(server, node, epoch)), node, epoch, server);
    private static SubmissionReceipt Receipt() => new("receipt", "fixed-submission", "exam", "candidate", "participation", DateTimeOffset.UtcNow, 5, 1);

    [Fact]
    public async Task ExistingActivationFetchesTopologyWithOriginalCredentialsWithoutReactivation()
    {
        var original = Device() with { Topology = null, ActiveNodeId = null };
        using var api = new HttpStudentApi(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/student/server-topology", request.RequestUri!.AbsolutePath);
            Assert.Equal(original.DeviceId, request.Headers.GetValues("X-Exam-Device-Id").Single());
            Assert.Equal(original.Credential, request.Headers.GetValues("X-Exam-Device-Credential").Single());
            return Task.FromResult(Reply(new ActivatedServerTopology("logical", [
                new("127.0.0.1", 5101, "primary", new string('1', 64)),
                new("127.0.0.2", 5102, "standby", new string('2', 64))]), "primary", 1));
        }));
        api.ConfigureConnection(original);
        var response = await api.GetTrustedTopologyAsync();
        var upgraded = original with { Topology = TrustedServerTopology.FromActivation(response.Result, original, response.ServerId), ActiveNodeId = response.NodeId };
        var store = new DeviceStore(_root); store.Save(original); store.Save(upgraded);
        Assert.Equal(original.ClientNumber, store.Load("127.0.0.2", 5102)!.ClientNumber);
        Assert.Equal(original.Credential, store.Load()!.Credential);
    }

    [Fact]
    public async Task InconsistentTimeDoesNotAdvanceProtectedEpoch()
    {
        var saves = 0;
        using var api = new HttpStudentApi(new Handler(_ => Task.FromResult(
            Reply(new ServerTimeSnapshot(DateTimeOffset.UtcNow, new("other", "primary", 9)), "primary", 9))));
        api.PersistTrustedConnection = _ => saves++;
        api.ConfigureConnection(Device());
        await Assert.ThrowsAsync<HttpStudentApi.IdentityRejectedException>(() => api.GetServerTimeAsync());
        Assert.Equal(0, saves); Assert.Equal(1, api.CurrentTrustedEndpoint!.Value.Server.ServerEpoch);
        Assert.Throws<InvalidOperationException>(() => api.ConfigureConnection(Device() with { Topology = null, ServerEpoch = 9 }));
        Assert.Equal(1, api.CurrentTrustedEndpoint.Value.Server.ServerEpoch);
    }

    [Fact]
    public void MalformedActivationTopologyPreservesOriginalRecord()
    {
        var original = Device() with { Topology = null, ActiveNodeId = null };
        Assert.Throws<StudentIdentityException>(() => TrustedServerTopology.FromActivation(
            new("logical", [new("bad-host", 0, "primary", "bad-pin")]), original, "logical"));
        Assert.Null(original.Topology); Assert.Equal("20261005001", original.ClientNumber);
    }

    [Fact]
    public async Task LostFinalResponseRetriesIdenticalBytesAndPersistsTakeoverBeforeSending()
    {
        var bodies = new List<string>(); DeviceRecord? saved = null;
        using var api = new HttpStudentApi(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/commands/time")
            {
                Assert.False(request.Headers.Contains("X-Exam-Device-Credential"));
                return Time();
            }
            bodies.Add(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri.Host == "127.0.0.1") throw new HttpRequestException("synthetic lost response");
            Assert.NotNull(saved);
            Assert.Equal(2, saved.ServerEpoch);
            Assert.Equal("standby", saved.ActiveNodeId);
            Assert.Equal("127.0.0.1", saved.Host); // Keep the original identity-store key.
            Assert.Equal("synthetic-credential", request.Headers.GetValues("X-Exam-Device-Credential").Single());
            return Reply(Receipt());
        }));
        api.PersistTrustedConnection = record => saved = record;
        api.ConfigureConnection(Device());
        var receipt = await api.SubmitFinalAnswersAsync(Submission());
        Assert.Equal("fixed-submission", receipt.Result!.SubmissionId);
        Assert.Equal(2, bodies.Count); Assert.Equal(bodies[0], bodies[1]);
        api.ConfigureConnection(Device()); // An old in-memory copy cannot reset the selected active node.
        Assert.Equal("127.0.0.2", api.CurrentTrustedEndpoint!.Value.Device.Host);
        Assert.Equal(2, api.CurrentTrustedEndpoint.Value.Server.ServerEpoch);
    }

    [Theory]
    [InlineData("other", "standby", 2)]
    [InlineData("logical", "unknown", 2)]
    [InlineData("logical", "standby", 1)]
    [InlineData("logical", "standby", 0)]
    public async Task FakeNodeOrNonIncreasingEpochNeverReceivesTheBusinessRequest(string server, string node, long epoch)
    {
        var secondaryWrites = 0; var saves = 0;
        using var api = new HttpStudentApi(new Handler(request =>
        {
            if (request.RequestUri!.Host == "127.0.0.1") throw new HttpRequestException("primary unavailable");
            if (request.Method != HttpMethod.Get) secondaryWrites++;
            return Task.FromResult(Time(server, node, epoch));
        }));
        api.PersistTrustedConnection = _ => saves++;
        api.ConfigureConnection(Device());
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => api.SubmitFinalAnswersAsync(Submission()));
        Assert.Equal(0, secondaryWrites); Assert.Equal(0, saves);
        Assert.Equal("primary", api.CurrentTrustedEndpoint!.Value.Server.NodeId);
    }

    [Fact]
    public async Task ProtectedIdentityWriteFailureKeepsOriginalEndpointAndDoesNotRetryFinal()
    {
        var secondaryWrites = 0;
        using var api = new HttpStudentApi(new Handler(request =>
        {
            if (request.RequestUri!.Host == "127.0.0.1") throw new HttpRequestException("primary unavailable");
            if (request.Method != HttpMethod.Get) secondaryWrites++;
            return Task.FromResult(Time());
        }));
        api.PersistTrustedConnection = _ => throw new DeviceStoreException("synthetic disk failure");
        api.ConfigureConnection(Device());
        await Assert.ThrowsAsync<DeviceStoreException>(() => api.SubmitFinalAnswersAsync(Submission()));
        Assert.Equal(0, secondaryWrites);
        Assert.Equal("primary", api.CurrentTrustedEndpoint!.Value.Server.NodeId);
    }

    [Fact]
    public async Task AuthorizationRejectionDoesNotProbeOrReactivate()
    {
        var calls = 0;
        using var api = new HttpStudentApi(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = JsonContent.Create(new ApiEnvelope<SubmissionReceipt>("logical", "primary", 1, "test", null,
                new ApiError(ErrorCodes.AuthorizationRevoked, "synthetic authorization rejection")), options: JsonDefaults.Create()) });
        }));
        api.ConfigureConnection(Device());
        var response = await api.SubmitFinalAnswersAsync(Submission());
        Assert.Equal(ErrorCodes.AuthorizationRevoked, response.Error!.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RestoredDeviceStartsAtPersistedActiveNodeAndRejectsOldEpoch()
    {
        var store = new DeviceStore(_root); store.Save(Device());
        store.Save(Device() with { ServerEpoch = 2, ActiveNodeId = "standby" });
        var restored = new DeviceStore(_root).Load("127.0.0.2", 5102)!;
        Assert.Equal("20261005001", restored.ClientNumber);
        using var api = new HttpStudentApi(new Handler(request =>
        {
            Assert.Equal("127.0.0.2", request.RequestUri!.Host);
            return Task.FromResult(Time(epoch: 1));
        }));
        api.ConfigureConnection(restored);
        await Assert.ThrowsAsync<HttpStudentApi.IdentityRejectedException>(() => api.GetServerTimeAsync());
        Assert.Throws<DeviceStoreException>(() => store.Save(Device()));
        Assert.Throws<DeviceStoreException>(() => store.Save(restored with { Topology = null }));
        Assert.Equal(2, store.Load()!.ServerEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveRuntimeRecreatesNotificationFeedWithoutReplacingParticipationOrDevice(bool silentPrimary)
    {
        var oldStopped = false;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participation = new ParticipationResult("participation", "exam", "candidate", ParticipationBusinessState.Answering,
            "device", 3, 4, 6, null);
        using var api = new HttpStudentApi(new CancelableHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "127.0.0.1")
            {
                if (silentPrimary) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new HttpRequestException("primary unavailable");
            }
            return Time();
        }));
        api.PersistTrustedConnection = _ => { };
        api.ConfigureConnection(Device());
        var session = new StudentSessionResult("session", "device", new("candidate", "合成考生", "01", "0001"), ["exam"], DateTimeOffset.UtcNow);
        var manager = new ExamPackageManager(api, _root, Device(), session, "exam");
        await using var runtime = new StudentExamRuntime(manager, Device(), commandFeedFactory: (device, server) =>
            new Feed(target =>
            {
                Assert.Equal(participation, target);
                Assert.Equal("device", device.DeviceId);
                Assert.Equal("20261005001", device.ClientNumber);
                if (server.NodeId == "standby")
                { Assert.True(oldStopped); Assert.Equal("127.0.0.2", device.Host); connected.TrySetResult(); }
            }, () => { if (server.NodeId == "primary") oldStopped = true; }));
        await runtime.StartNotificationsAsync(participation, new("logical", "primary", 1));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Same(manager, runtime.Manager);
        await runtime.RevokeSubmissionAuthorizationAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartNotificationsAsync(participation, new("logical", "standby", 2)));
    }

    [Fact]
    public async Task CallerCancellationNeverProbesAnotherNodeOrChangesProtectedIdentity()
    {
        var calls = 0;
        using var api = new HttpStudentApi(new CancelableHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }));
        api.ConfigureConnection(Device());
        api.PersistTrustedConnection = _ => throw new InvalidOperationException("must not persist");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetServerTimeAsync(cancellation.Token));
        Assert.Equal(1, calls);
        Assert.Equal("primary", api.CurrentTrustedEndpoint!.Value.Server.NodeId);
    }

    [Fact]
    public async Task OldAbsolutePackageAddressIsRebasedOnlyToTheTrustedActiveNode()
    {
        var faulted = false;
        using var api = new HttpStudentApi(new Handler(request =>
        {
            if (request.RequestUri!.Host == "127.0.0.1") { faulted = true; throw new HttpRequestException("primary unavailable"); }
            if (request.RequestUri.AbsolutePath == "/api/v1/commands/time") return Task.FromResult(Time());
            Assert.True(faulted);
            Assert.Equal("/api/v1/resources/same-version", request.RequestUri.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("application/vnd.huaizhou.exam-package");
            response.Headers.Add("X-Exam-Server-Id", "logical"); response.Headers.Add("X-Exam-Node-Id", "standby");
            response.Headers.Add("X-Exam-Server-Epoch", "2"); response.Headers.Add("X-Exam-Paper-Version", "5");
            return Task.FromResult(response);
        }));
        api.PersistTrustedConnection = _ => { }; api.ConfigureConnection(Device());
        var bytes = new List<byte>();
        await foreach (var chunk in api.DownloadPackageChunksAsync(new("https://127.0.0.1:5101/api/v1/resources/same-version", 3, 5, 1), "original-preparation", "original-session"))
            bytes.AddRange(chunk);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await foreach (var _ in api.DownloadPackageChunksAsync(new("https://outside.invalid/api/v1/resources/same-version", 3, 5, 1), "original-preparation", "original-session")) { } });
    }

    private sealed class Feed(Action<ParticipationResult> start, Action stop) : IExamCommandFeed
    {
        public Task StartAsync(ParticipationResult participation, Func<CommandDelivery, CancellationToken, Task> handler, CancellationToken ct = default)
        { start(participation); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { stop(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { stop(); return ValueTask.CompletedTask; }
    }

    public void Dispose() { ServerTrust.Configure(null); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class CancelableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => route(request, ct); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> route) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => route(request); }
}
