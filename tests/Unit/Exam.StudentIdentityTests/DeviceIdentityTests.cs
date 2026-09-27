using System.IO;
using System.Net.Http;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class DeviceIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"exam-identity-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Protected_store_separates_servers_and_preserves_original_after_failed_replacement()
    {
        var store = new DeviceStore(_directory);
        var original = ActiveDevice();
        store.Save(original);
        var second = original with { Host = "127.0.0.2", ServerId = "other-server", DeviceId = "other-device", Credential = "other-credential" };
        store.Save(second);

        Assert.Equal(second, new DeviceStore(_directory).Load());
        Assert.Equal(original, store.Load(original.Host, original.Port));
        Assert.DoesNotContain(original.Credential, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, "devices.protected"))));
        Assert.DoesNotContain(original.Credential, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, "devices.protected.bak"))));

        using (var locked = new FileStream(Path.Combine(_directory, "devices.protected"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<DeviceStoreException>(() => store.Save(second with { Status = ActivationStatus.Revoked }));
        }

        Assert.Equal(second, store.Load());
        Assert.Equal(original, store.Load(original.Host, original.Port));
    }

    [Fact]
    public void Corruption_and_legacy_plaintext_fail_closed_without_overwriting_identity()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "device.json");
        File.WriteAllText(legacy, "{\"Credential\":\"preserve-this\"}", Encoding.UTF8);
        var store = new DeviceStore(_directory);
        Assert.Throws<DeviceStoreException>(() => store.Load());
        Assert.True(File.Exists(legacy));

        var protectedFile = Path.Combine(_directory, "devices.protected");
        File.WriteAllBytes(protectedFile, [1, 2, 3]);
        Assert.Throws<DeviceStoreException>(() => store.Load());
        Assert.Throws<DeviceStoreException>(() => store.Save(ActiveDevice()));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(protectedFile));
    }

    [Fact]
    public async Task Lost_submit_response_reuses_persisted_identity_then_polls_status_instead_of_resubmitting()
    {
        var store = new DeviceStore(_directory);
        var api = new RecordingIdentityApi { DropSubmitResponse = true };
        var workflow = new ActivationWorkflow(store, api);
        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.RequestOrPollAsync("127.0.0.1", 5001));
        var pending = store.Load()!;
        Assert.NotNull(pending.SubmitRequestId);

        var reloadedWorkflow = new ActivationWorkflow(new DeviceStore(_directory), api);
        var submitted = await reloadedWorkflow.RequestOrPollAsync("127.0.0.1", 5001);
        Assert.Equal(ActivationStatus.Pending, submitted.Status);
        var activated = await reloadedWorkflow.RequestOrPollAsync("127.0.0.1", 5001);
        Assert.Equal(ActivationStatus.Active, activated.Status);
        Assert.Equal(2, api.SubmitRequests.Count);
        Assert.Equal(api.SubmitRequests[0], api.SubmitRequests[1]);
        Assert.Single(api.StatusRequests);
        Assert.Equal(pending.DeviceId, activated.DeviceId);
        Assert.Equal(pending.Credential, activated.Credential);
    }

    [Fact]
    public async Task Lost_cancel_response_retries_original_request_and_reactivation_keeps_number_and_credentials()
    {
        var store = new DeviceStore(_directory);
        var active = ActiveDevice();
        store.Save(active);
        var api = new RecordingIdentityApi { DropCancelResponse = true };
        var workflow = new ActivationWorkflow(store, api);
        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.CancelAsync(active));
        Assert.Equal(ActivationStatus.Active, store.Load()!.Status);
        Assert.Equal("cancel", store.Load()!.PendingOperation);

        // A caller retaining its pre-request object must still retry the request saved on disk.
        var revoked = await workflow.CancelAsync(active);
        Assert.Equal(api.CancelRequests[0], api.CancelRequests[1]);
        Assert.Equal(ActivationStatus.Revoked, revoked.Status);
        Assert.Equal(active.ClientNumber, revoked.ClientNumber);
        Assert.Equal(active.Credential, revoked.Credential);
        Assert.Null(revoked.PendingOperationRequestId);

        var reactivated = await workflow.RequestOrPollAsync(active.Host, active.Port);
        Assert.Equal(ActivationStatus.Active, reactivated.Status);
        Assert.Equal(active.DeviceId, reactivated.DeviceId);
        Assert.Equal(active.ClientNumber, reactivated.ClientNumber);
        Assert.Equal(active.Credential, reactivated.Credential);
        Assert.Empty(api.SubmitRequests);
    }

    [Fact]
    public void Server_identity_or_epoch_cannot_replace_existing_registration()
    {
        var store = new DeviceStore(_directory);
        var original = ActiveDevice();
        store.Save(original);
        Assert.Throws<DeviceStoreException>(() => store.Save(original with { ServerId = "other-server" }));
        Assert.Throws<DeviceStoreException>(() => store.Save(original with { ServerEpoch = 0 }));
        Assert.Throws<DeviceStoreException>(() => store.Save(original with { ClientNumber = "20260922002" }));
        Assert.Equal(original, store.Load());
    }

    private static DeviceRecord ActiveDevice() => new("device-1", "synthetic-private-device-credential", "20260922001", "127.0.0.1", 5001,
        DateTimeOffset.Parse("2026-09-22T01:00:00Z"), "server-1", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingIdentityApi : IStudentApi
    {
        public bool DropSubmitResponse { get; set; }
        public bool DropCancelResponse { get; set; }
        public List<SubmitActivationRequest> SubmitRequests { get; } = [];
        public List<GetActivationStatusRequest> StatusRequests { get; } = [];
        public List<CancelOwnActivationRequest> CancelRequests { get; } = [];
        public void ConfigureConnection(DeviceRecord record) { }

        public Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(SubmitActivationRequest request, CancellationToken ct = default)
        {
            SubmitRequests.Add(request);
            if (DropSubmitResponse)
            {
                DropSubmitResponse = false;
                throw new HttpRequestException("Synthetic lost response.");
            }

            return Task.FromResult(Ok(new ActivationRequestResult("activation-1", request.DeviceId, ActivationStatus.Pending, null, 0, DateTimeOffset.UtcNow)));
        }

        public Task<ApiEnvelope<ActivationRequestResult>> GetActivationStatusAsync(GetActivationStatusRequest request, CancellationToken ct = default)
        {
            StatusRequests.Add(request);
            return Task.FromResult(Ok(new ActivationRequestResult(request.ActivationRequestId, request.DeviceId, ActivationStatus.Active, "20260922001", 1, DateTimeOffset.UtcNow)));
        }

        public Task<ApiEnvelope<ActivationResult>> ReactivateAsync(ReactivateDeviceRequest request, CancellationToken ct = default) =>
            Task.FromResult(Ok(new ActivationResult(request.DeviceId, "20260922001", new DateOnly(2026, 9, 22), ActivationStatus.Active, null, 1, 3)));

        public Task<ApiEnvelope<ActivationResult>> CancelActivationAsync(CancelOwnActivationRequest request, CancellationToken ct = default)
        {
            CancelRequests.Add(request);
            if (DropCancelResponse)
            {
                DropCancelResponse = false;
                throw new HttpRequestException("Synthetic lost response.");
            }

            return Task.FromResult(Ok(new ActivationResult(request.DeviceId, "20260922001", new DateOnly(2026, 9, 22), ActivationStatus.Revoked, null, 1, 2)));
        }

        public Task<ApiEnvelope<StudentSessionResult>> LoginAsync(LoginStudentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ServerTimeSnapshot>> GetServerTimeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<PersistedProgress>> SyncProgressAsync(SyncProgressRequest request, CancellationToken ct = default) => throw new NotSupportedException("身份测试不涉及进度上报。");
        public Task<ApiEnvelope<FinalSubmission>> SubmitFinalAnswersAsync(SubmitFinalAnswersRequest request, CancellationToken ct = default) => throw new NotSupportedException("身份测试不涉及交卷。");
        public Task<ApiEnvelope<FinalSubmission>> GetFinalSubmissionAsync(string participationId, long entryVersion, string deviceId, long authorizationVersion, CancellationToken ct = default) => throw new NotSupportedException("身份测试不涉及回执查询。");
        private static ApiEnvelope<T> Ok<T>(T result) => new("server-1", "node-1", 1, Guid.NewGuid().ToString("N"), result, null);
    }
}
