using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;
using Exam.Contracts.Submissions;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// B05 同步与交卷验收判据：进度幂等上报（稳定请求标识）、固定提交重试沿用原请求、
/// 回执丢失后恢复原结果、服务器冲突记录不被覆盖、结束通知后停止上报且本地修改保留、
/// 只有取得可信回执才进入 Confirmed。
/// </summary>
public sealed partial class SubmissionSyncServiceTests
{
    [Fact]
    public async Task FocusLossIsReportedPromptlyWithNewRequestId_AndRegularSyncSkipsIt()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var focus = new FocusTracker();
        var sync = fixture.CreateSync(focus);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);
        Assert.Equal(0, fixture.SubmissionApi.ProgressRequests[0].FocusLostCount);

        // 失焦→复焦：答案版本未变，但失焦计数变化触发补报（新请求标识 + 失焦字段）。
        focus.OnFocusLost();
        focus.OnFocusRegained();
        await sync.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.ProgressRequests.Count);
        var supplement = fixture.SubmissionApi.ProgressRequests[1];
        Assert.Equal(1, supplement.FocusLostCount);
        Assert.NotNull(supplement.LastFocusRegainedAt);
        Assert.NotEqual(fixture.SubmissionApi.ProgressRequests[0].RequestId, supplement.RequestId);
        Assert.Equal(fixture.SubmissionApi.ProgressRequests[0].AnswerVersion, supplement.AnswerVersion);

        // 已上报的失焦计数不重复触发。
        await sync.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.ProgressRequests.Count);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ProgressUploadUsesStableRequestIdPerVersion_AndSkipsAlreadySyncedVersion()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        Assert.True((await answers.SetCurrentQuestionAsync("q1")).Succeeded);

        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);
        var first = fixture.SubmissionApi.ProgressRequests[0];
        Assert.Equal(answers.AnswerVersion, first.AnswerVersion);
        Assert.Equal("participation", first.ParticipationId);
        Assert.Equal(fixture.Device.DeviceId, first.DeviceId);
        Assert.Equal("q1", first.CurrentQuestionId);

        // 同一版本已同步：再次触发不应重复上报。
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);

        // 版本推进后换新请求标识。
        Assert.True((await answers.SetAnswerAsync("q2", AnswerValue.Selected("B"))).Succeeded);
        await sync.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.ProgressRequests.Count);
        Assert.NotEqual(first.RequestId, fixture.SubmissionApi.ProgressRequests[1].RequestId);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ProgressRetryAfterNetworkFailure_ReusesSameRequestId()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);

        fixture.SubmissionApi.ProgressHandler = _ => throw new HttpRequestException("网络中断");
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.WaitingRetry, sync.Phase);
        Assert.Null(sync.ConfirmedReceipt);

        fixture.SubmissionApi.ProgressHandler = null;
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Progressing, sync.Phase);
        Assert.Equal(2, fixture.SubmissionApi.ProgressRequests.Count);
        Assert.Equal(
            fixture.SubmissionApi.ProgressRequests[0].RequestId,
            fixture.SubmissionApi.ProgressRequests[1].RequestId);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task FinalSubmissionUploadsOnceWithStableRequestId_AndConfirmsOnlyWithReceipt()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var frozen = Assert.IsType<FixedSubmissionRecord>((await answers.FixSubmissionAsync("subm-1")).Record);

        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.FinalRequests);
        var request = fixture.SubmissionApi.FinalRequests[0];
        Assert.Equal(frozen.RequestId, request.RequestId);
        Assert.Equal("subm-1", request.SubmissionId);
        Assert.Equal(frozen.PaperVersion, request.PaperVersion);
        Assert.Equal("participation", request.ParticipationId);

        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.NotNull(sync.ConfirmedReceipt);
        Assert.Equal("subm-1", sync.ConfirmedReceipt!.SubmissionId);

        // 已确认后不再产生任何新的网络写入。
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.FinalRequests);
        Assert.Empty(fixture.SubmissionApi.ProgressRequests);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ReceiptLostInTransit_ReplayWithOriginalRequestRecoversOriginalResult()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var frozen = Assert.IsType<FixedSubmissionRecord>((await answers.FixSubmissionAsync("subm-1")).Record);

        // 第一次：服务器已持久化，但响应在网络中丢失。
        fixture.SubmissionApi.SubmitHandler = request =>
        {
            fixture.SubmissionApi.SubmitHandler = null; // 后续按默认幂等重放处理。
            fixture.SubmissionApi.Persist(request);
            throw new HttpRequestException("响应丢失");
        };
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.WaitingRetry, sync.Phase);
        Assert.Null(sync.ConfirmedReceipt);

        // 第二次：同一请求标识重发，服务器幂等重放返回原回执。
        await sync.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.FinalRequests.Count);
        Assert.Equal(
            fixture.SubmissionApi.FinalRequests[0].RequestId,
            fixture.SubmissionApi.FinalRequests[1].RequestId);
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.Equal("subm-1", sync.ConfirmedReceipt!.SubmissionId);
        Assert.Equal(frozen.RequestId, fixture.SubmissionApi.FinalRequests[1].RequestId);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ServerAlreadyHoldsSubmission_QueryRecoversReceiptWithoutNewSubmission()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var frozen = Assert.IsType<FixedSubmissionRecord>((await answers.FixSubmissionAsync("subm-1")).Record);

        fixture.SubmissionApi.SubmitHandler = _ =>
            new ApiEnvelope<SubmissionReceipt>("server", "node", 1, "correlation", null,
                new ApiError(ErrorCodes.AlreadySubmitted, "该参试已有提交。", Retryable: false));
        fixture.SubmissionApi.QueryHandler = () => fixture.SubmissionApi.BuildFinal(frozen);

        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.FinalRequests);
        Assert.Single(fixture.SubmissionApi.QueryCalls);
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.Equal("subm-1", sync.ConfirmedReceipt!.SubmissionId);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ConflictingServerSubmission_BlocksInsteadOfOverwriting_OriginalRecordKept()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var frozen = Assert.IsType<FixedSubmissionRecord>((await answers.FixSubmissionAsync("subm-1")).Record);

        fixture.SubmissionApi.SubmitHandler = _ =>
            new ApiEnvelope<SubmissionReceipt>("server", "node", 1, "correlation", null,
                new ApiError(ErrorCodes.AlreadySubmitted, "该参试已有提交。", Retryable: false));
        var other = frozen with { SubmissionId = "someone-else" };
        fixture.SubmissionApi.QueryHandler = () => fixture.SubmissionApi.BuildFinal(other);

        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Blocked, sync.Phase);
        Assert.Null(sync.ConfirmedReceipt);

        // 本地原固定记录保持不变，不会被覆盖或删除。
        var stored = await fixture.Store.LoadFixedSubmissionAsync(fixture.Key);
        Assert.NotNull(stored);
        Assert.Equal("subm-1", stored!.SubmissionId);
        Assert.Equal(frozen.RequestId, stored.RequestId);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task EndArrivesAfterLocalEdits_ProgressStopsButSavedAnswersRemain()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        var sync = fixture.CreateSync();
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);

        // 结束通知到达：答案挂起，本地已保存修改保留，不再上报进度。
        Assert.True(await fixture.Runtime.Manager.HandleCommandAsync(fixture.Api.End()));
        await fixture.Runtime.SynchronizeLocalAnswersAsync();
        Assert.NotEqual(AnswerSessionStatus.Active, fixture.Runtime.Answers?.Status);

        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);
        var saved = await fixture.Store.LoadAnswerStateAsync(fixture.Key);
        Assert.NotNull(saved);
        Assert.Equal(AnswerKind.Selected, saved!.Answers["q1"].Kind);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task FixedSessionNeverReportsProgress_EvenAfterRestart()
    {
        await using var fixture = await StartedAsync();
        var answers = fixture.Answers!;
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        Assert.True((await answers.FixSubmissionAsync("subm-1")).Succeeded);

        // 模拟重启后的新服务实例：本地已有固定提交。
        var sync = fixture.CreateSync();
        await sync.KickAsync();
        await sync.KickAsync();
        Assert.Empty(fixture.SubmissionApi.ProgressRequests);
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        await sync.DisposeAsync();
    }

    private async Task<SyncFixture> StartedAsync()
    {
        var fixture = await SyncFixture.CreateAsync();
        fixture.Api.Answering = true;
        await fixture.BootstrapRuntimeAsync();
        return fixture;
    }

    private sealed class SyncFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"exam-sync-tests-{Guid.NewGuid():N}");
        public LocalStateKey Key { get; } = new("server", "exam", "exam-candidate");
        public SqliteLocalStateStore Store { get; }
        public PackageApi Api { get; }
        public RecordingSubmissionApi SubmissionApi { get; } = new();
        public DeviceRecord Device { get; } = new("device", "test-credential", "20260922001", "127.0.0.1", 5443,
            DateTimeOffset.UtcNow, "server", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);
        public StudentExamRuntime Runtime { get; private set; } = null!;
        public AnswerSession? Answers => Runtime.Answers;

        private SyncFixture(PackageApi api)
        {
            Api = api;
            Store = new SqliteLocalStateStore(Path.Combine(_root, "state.db"));
        }

        public static async Task<SyncFixture> CreateAsync() => new(await PackageApi.CreateAsync());

        public async Task BootstrapRuntimeAsync()
        {
            Runtime = NewRuntime();
            Assert.True(await Runtime.Manager.PrepareAndDownloadAsync());
            Assert.True((await Runtime.Manager.ConfirmParticipationAsync())?.IsSuccess);
            Assert.True(await Runtime.Manager.HandleCommandAsync(Api.Start()));
            await Runtime.SynchronizeLocalAnswersAsync();
            Assert.Equal(AnswerSessionStatus.Active, Runtime.Answers?.Status);
        }

        private StudentExamRuntime NewRuntime()
        {
            return new StudentExamRuntime(
                new ExamPackageManager(Api, Path.Combine(_root, "packages"), Device,
                    new("session", "device",
                        new("login-candidate", "合成考生", "01", "001"), ["exam"], DateTimeOffset.UtcNow,
                        new Dictionary<string, string> { ["exam"] = "exam-candidate" }),
                    "exam"),
                Device, Store, Key.CandidateId);
        }

        public string DatabasePath => Path.Combine(_root, "state.db");

        public async Task RestartForRecoveryAsync(bool resumeAnswering = false)
        {
            await Runtime.DisposeAsync();
            if (resumeAnswering) await BootstrapRuntimeAsync();
            else
            {
                Runtime = NewRuntime();
                Assert.True(await Runtime.InitializeLocalRecoveryAsync());
            }
        }

        public SubmissionSyncService CreateSync(FocusTracker? focus = null) =>
            new(SubmissionApi, Runtime, Device, focus: focus);

        public async ValueTask DisposeAsync()
        {
            if (Runtime is not null)
            {
                await Runtime.DisposeAsync();
            }

            Store.Dispose();
            Api.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingSubmissionApi : IStudentApi
    {
        public List<SyncProgressRequest> ProgressRequests { get; } = new();
        public List<SubmitFinalAnswersRequest> FinalRequests { get; } = new();
        public List<string> QueryCalls { get; } = new();

        public Func<CancellationToken, Task<ApiEnvelope<ServerTimeSnapshot>>>? TimeHandler { get; set; }
        public Func<SyncProgressRequest, ApiEnvelope<PersistedProgress>?>? ProgressHandler { get; set; }
        public Func<SubmitFinalAnswersRequest, ApiEnvelope<SubmissionReceipt>?>? SubmitHandler { get; set; }
        public Func<FinalSubmission?>? QueryHandler { get; set; }
        public Func<ApiEnvelope<SubmissionReceipt>>? QueryResponseHandler { get; set; }

        private readonly Dictionary<string, FinalSubmission> _persisted = new();

        public Task<ApiEnvelope<PersistedProgress>> SyncProgressAsync(
            SyncProgressRequest request, CancellationToken ct = default)
        {
            ProgressRequests.Add(request);
            var handler = ProgressHandler;
            if (handler is not null)
            {
                return Task.FromResult(handler(request) ?? Fail<PersistedProgress>("进度保存失败。"));
            }

            var persisted = new PersistedProgress(request.ExamId, request.CandidateId, request.ParticipationId,
                request.EntryVersion, request.DeviceId, request.AuthorizationVersion, request.AnswerVersion,
                request.PaperVersion, request.Answers, request.CurrentQuestionId, DateTimeOffset.UtcNow);
            return Task.FromResult(Ok(persisted));
        }

        public Task<ApiEnvelope<SubmissionReceipt>> SubmitFinalAnswersAsync(
            SubmitFinalAnswersRequest request, CancellationToken ct = default)
        {
            FinalRequests.Add(request);
            if (_persisted.TryGetValue(request.RequestId, out var existing))
            {
                return Task.FromResult(Ok(existing.Receipt));
            }

            var handler = SubmitHandler;
            if (handler is not null)
            {
                var result = handler(request);
                return Task.FromResult(result ?? Fail<SubmissionReceipt>("提交失败。"));
            }

            return Task.FromResult(Ok(Persist(request).Receipt));
        }

        public Task<ApiEnvelope<SubmissionReceipt>> GetSubmissionReceiptAsync(
            string participationId, long entryVersion, string deviceId, long authorizationVersion,
            CancellationToken ct = default)
        {
            QueryCalls.Add(participationId);
            if (QueryResponseHandler is not null) return Task.FromResult(QueryResponseHandler());
            var handler = QueryHandler;
            if (handler is not null)
            {
                var found = handler();
                return Task.FromResult(found is null
                    ? Fail<SubmissionReceipt>("没有已持有的提交。")
                    : Ok(found.Receipt));
            }

            return Task.FromResult(Fail<SubmissionReceipt>("没有已持有的提交。"));
        }

        public FinalSubmission Persist(SubmitFinalAnswersRequest request)
        {
            var receipt = new SubmissionReceipt(
                $"receipt-{request.SubmissionId}", request.SubmissionId, request.ExamId, request.CandidateId,
                request.ParticipationId, DateTimeOffset.UtcNow, request.PaperVersion, GradingVersion: 1);
            var submission = new FinalSubmission(request.SubmissionId, request.ExamId, request.CandidateId,
                request.ParticipationId, request.DeviceId, request.AuthorizationVersion, request.PaperVersion,
                request.FrozenAnswers, receipt.ReceivedAt, receipt);
            _persisted[request.RequestId] = submission;
            return submission;
        }

        public FinalSubmission BuildFinal(FixedSubmissionRecord record) =>
            new(record.SubmissionId, "exam", "exam-candidate", record.ParticipationId ?? "participation",
                "device", record.AuthorizationVersion, record.PaperVersion, record.FrozenAnswers,
                DateTimeOffset.UtcNow, new SubmissionReceipt(
                    $"receipt-{record.SubmissionId}", record.SubmissionId, "exam", "exam-candidate",
                    record.ParticipationId ?? "participation", DateTimeOffset.UtcNow, record.PaperVersion, 1));

        private static ApiEnvelope<T> Ok<T>(T value) => new("server", "node", 1, "correlation", value, null);
        private static ApiEnvelope<T> Fail<T>(string message) => new("server", "node", 1, "correlation", default,
            new ApiError(ErrorCodes.ServiceRecovering, message, Retryable: true));

        public void ConfigureConnection(DeviceRecord device) { }
        public Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(SubmitActivationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationRequestResult>> GetActivationStatusAsync(GetActivationStatusRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationResult>> ReactivateAsync(ReactivateDeviceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationResult>> CancelActivationAsync(CancelOwnActivationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<StudentSessionResult>> LoginAsync(LoginStudentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ServerTimeSnapshot>> GetServerTimeAsync(CancellationToken ct = default) =>
            TimeHandler?.Invoke(ct) ?? throw new NotSupportedException();
    }

    private sealed class PackageApi : IExamPackageApi, IDisposable
    {
        public const string ManifestJson = """
            {"schemaVersion":1,"examId":"exam","examName":"同步合成考试",
             "subjects":[{"subjectId":"s1","name":"合成科目","maxScore":"2"}],
             "groups":[{"groupId":"g1","subjectId":"s1","name":"单选","type":"single","order":1,
               "questionCount":2,"maxScore":"2","scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
             "questions":[
               {"questionId":"q1","questionNo":1,"subjectId":"s1","groupId":"g1","source":"questions/同步合成考试_1.tex","options":["A","B"]},
               {"questionId":"q2","questionNo":2,"subjectId":"s1","groupId":"g1","source":"questions/同步合成考试_2.tex","options":["A","B"]}],
             "resourceRoots":[]}
            """;
        private X509Certificate2 _node = null!;
        private EncryptedPackageEnvelope _envelope = null!;
        private byte[] _ciphertext = [];
        public bool Answering { get; set; }

        public static async Task<PackageApi> CreateAsync()
        {
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=B05-sync-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var api = new PackageApi { _node = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)) };
            var root = Path.Combine(Path.GetTempPath(), "sync-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                var compiled = Directory.CreateDirectory(Path.Combine(root, "compiled")).FullName;
                await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), ManifestJson, Encoding.UTF8);
                foreach (var number in new[] { 1, 2 })
                    await File.WriteAllTextAsync(Path.Combine(compiled, $"同步合成考试_{number}.pdf"), "%PDF-1.4\n%synthetic\n%%EOF", new UTF8Encoding(false));
                var package = await new StudentPackageBuilder(new PackageEncryptionService(4096)).BuildAsync(
                    new(source, compiled, Path.Combine(root, "preview"), "package", 1,
                        Path.Combine(root, "cipher", "package.pkg"), [new("node", api._node)]));
                api._envelope = package.Envelope!;
                api._ciphertext = await File.ReadAllBytesAsync(package.EncryptedPackagePath!);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            return api;
        }

        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default) =>
            Task.FromResult(Ok(new EntryPreparationResult(request.RequestId, "exam", EntryPreparationStatus.Ready,
                new("/api/v1/resources/package", _ciphertext.Length, 1, 1), null, null, null, null, null, DateTimeOffset.UtcNow)));
        public Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(string preparationId, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Ok(new InstructionsDocument(1, ["合成说明，仅供同步集成测试。"])));
        public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(ExamResourceDescriptor descriptor, string preparationId,
            string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return _ciphertext;
        }
        public Task<ApiEnvelope<ParticipationResult>> ConfirmParticipationAsync(ConfirmParticipationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Ok(new ParticipationResult("participation", "exam", "exam-candidate",
                Answering ? ParticipationBusinessState.Answering : ParticipationBusinessState.Waiting,
                "device", 1, 1, 7, Answering ? "resource-envelope:package" : null,
                Lifecycle: Answering ? ExamLifecycle.Running : ExamLifecycle.Scheduled)));
        public Task<ApiEnvelope<ParticipationResult>> WithdrawParticipationAsync(WithdrawParticipationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("本组测试不执行退出。");
        public Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(PackageUnlockRequest request, CancellationToken ct = default) =>
            Task.FromResult(Ok(new PackageUnlockMaterial(_envelope.PackageId, _envelope.PaperVersion,
                _envelope.ChunkSize, _envelope.NoncePrefix, PackageEncryptionService.UnwrapDataKey(_envelope, "node", _node))));
        public Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(AcknowledgeCommandRequest request, CancellationToken ct = default) =>
            Task.FromResult(Ok(new CommandAcknowledgement(request.EventId, request.RequestId, request.ProcessingResult,
                request.ActualPaperVersion, request.FailureReason, DateTimeOffset.UtcNow)));
        public CommandDelivery Start() => new(new("server", "node", 1), "start", "exam", "participation", 1,
            CommandEventType.Start, 1, "device", 1, 1, "resource-envelope:package", new Dictionary<string, string>(), DateTimeOffset.UtcNow, 1);
        public CommandDelivery End() => Start() with { EventId = "end", EventType = CommandEventType.End, StateVersion = 2 };
        private static ApiEnvelope<T> Ok<T>(T value) => new("server", "node", 1, "correlation", value, null);
        public void Dispose() => _node.Dispose();
    }
}
