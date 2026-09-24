using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
using Exam.Infrastructure.Security;
using Exam.Infrastructure.Packaging;
using Exam.Application.Submissions;
using Exam.Domain.Submissions;
using Exam.Domain.Scoring;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class StudentExamRuntimeTests
{
    [Fact]
    public async Task DecryptedButInvalidPackageCannotCreateBlankLocalWork()
    {
        using var fixture = await Fixture.CreateAsync(invalidPackage: true);
        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        Assert.True((await runtime.Manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await runtime.Manager.HandleCommandAsync(fixture.Api.Start()));
        await runtime.SynchronizeLocalAnswersAsync();
        Assert.Null(runtime.Paper);
        Assert.Null(runtime.Answers);
        Assert.Contains("试题包", runtime.LocalStateError);
        Assert.Null(await fixture.Store.LoadAnswerStateAsync(fixture.Key));
    }

    [Fact]
    public async Task PackagedQuestions_CreateCompleteDurableSubmission_AndGradeWithImportedRules()
    {
        using var fixture = await Fixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        Assert.True((await runtime.Manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await runtime.Manager.HandleCommandAsync(fixture.Api.Start()));
        await runtime.SynchronizeLocalAnswersAsync();
        var paper = Assert.IsType<StudentPaperManifest>(runtime.Paper);
        Assert.Equal(2, paper.Questions.Count);
        Assert.StartsWith("%PDF-", Encoding.UTF8.GetString(runtime.ReadQuestionPdf("q1")));
        var session = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.Equal(2, session.Answers.Count);
        Assert.True((await session.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        Assert.False((await session.SetAnswerAsync("q1", AnswerValue.Selected("A", "B"))).Succeeded);
        Assert.False((await session.SetAnswerAsync("unknown", AnswerValue.Empty())).Succeeded);
        var frozen = Assert.IsType<FixedSubmissionRecord>((await session.FixSubmissionAsync("submission")).Record);
        Assert.Equal(AnswerKind.Empty, frozen.FrozenAnswers["q2"].Kind);
        Assert.Equal(2, (await fixture.Store.LoadFixedSubmissionAsync(fixture.Key))!.FrozenAnswers.Count);

        var rules = ImportedGradingRules.Create(PackageApi.ManifestJson, PackageApi.AnswerKeyJson);
        var store = new InMemorySubmissionStore();
        await store.SetAuthorizationStateAsync(new("exam", "exam-candidate", "participation", 1,
            "device", 1, ParticipationBusinessState.Answering, 1, 1));
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(), paperReader: _ => rules.Structure);
        var request = new SubmitFinalAnswersRequest(frozen.RequestId!, frozen.SubmissionId, "exam", "exam-candidate",
            frozen.ParticipationId!, frozen.EntryVersion, frozen.DeviceId!, frozen.AuthorizationVersion,
            frozen.PaperVersion, frozen.FrozenAnswers);
        var received = await service.SubmitAsync(request);
        Assert.True(received.IsSuccess, received.Message);
        var persisted = Assert.IsType<FinalSubmission>(await store.FindSubmissionAsync("exam", "exam-candidate"));
        var grade = rules.Grade(persisted, received.Value!.GradingVersion);
        Assert.True(grade.Succeeded, grade.FailureReason);
        Assert.Equal("1", grade.Grade!.ExactTotalScore);
        Assert.True((await service.SubmitAsync(request)).IsReplay);
        await runtime.Manager.HandleCommandAsync(fixture.Api.End());
        Assert.Null(runtime.Paper);
        Assert.Throws<InvalidOperationException>(() => runtime.ReadQuestionPdf("q1"));
    }

    [Fact]
    public async Task SparseSavedAnswersArePreservedAndBlocked_WhenPackageCatalogBecomesAvailable()
    {
        using var fixture = await Fixture.CreateAsync();
        var old = new AnswerSession(fixture.Store);
        Assert.True((await old.StartAsync(fixture.Key, new("participation", 1, 1, 1, "device", true))).IsUsable);
        Assert.True((await old.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        Assert.True((await runtime.Manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await runtime.Manager.HandleCommandAsync(fixture.Api.Start()));
        await runtime.SynchronizeLocalAnswersAsync();
        Assert.Equal(AnswerSessionStatus.PermissionDenied, runtime.Answers?.Status);
        Assert.Single((await fixture.Store.LoadAnswerStateAsync(fixture.Key))!.Answers);
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
    }

    [Fact]
    public async Task CachedPreparationDoesNotAuthorizeAnswers_FreshUnlockRestoresDurableState()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = fixture.Runtime();
        await using (first)
        {
            Assert.True(await first.InitializeLocalRecoveryAsync());
            Assert.True(await first.Manager.PrepareAndDownloadAsync());
            Assert.True((await first.Manager.ConfirmParticipationAsync())?.IsSuccess);
            await first.SynchronizeLocalAnswersAsync();
            Assert.Null(first.Answers);
            Assert.Null(await fixture.Store.LoadAnswerStateAsync(fixture.Key));
            Assert.Equal(0, fixture.Api.UnlockCalls);

            Assert.True(await first.Manager.HandleCommandAsync(fixture.Api.Start()));
            await first.SynchronizeLocalAnswersAsync();
            var answers = Assert.IsType<AnswerSession>(first.Answers);
            Assert.Equal(AnswerSessionStatus.Active, answers.Status);
            Assert.True((await answers.SetAnswerAsync("q1", new(AnswerKind.Selected, ["A"]))).Succeeded);
            Assert.True((await answers.ToggleMarkAsync("q1")).Succeeded);
            Assert.True((await answers.SetCurrentQuestionAsync("q1")).Succeeded);
        }

        await using var restarted = fixture.Runtime();
        await restarted.SynchronizeLocalAnswersAsync();
        Assert.Null(restarted.Answers);
        Assert.Equal(1, fixture.Api.UnlockCalls);
        Assert.Equal(ExamPackageManager.Phase.ConfirmedWaiting, restarted.Manager.CurrentPhase);

        fixture.Api.Answering = true;
        Assert.True(await restarted.Manager.PrepareAndDownloadAsync());
        Assert.True((await restarted.Manager.ConfirmParticipationAsync())?.IsSuccess);
        await restarted.SynchronizeLocalAnswersAsync();
        var restored = Assert.IsType<AnswerSession>(restarted.Answers);
        Assert.Equal(AnswerSessionStatus.Active, restored.Status);
        Assert.Equal(new[] { "A" }, restored.Answers["q1"].OptionIds);
        Assert.Contains("q1", restored.Marks);
        Assert.Equal("q1", restored.CurrentQuestionId);
        Assert.Equal(2, fixture.Api.UnlockCalls);
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
    }

    [Fact]
    public async Task FixedSubmissionIsReadBeforeNewEntry_AndIsNeverShownAsServerSuccess()
    {
        using var fixture = await Fixture.CreateAsync();
        var original = new AnswerSession(fixture.Store);
        Assert.True((await original.StartAsync(fixture.Key,
            new("participation", 1, 1, 1, "device", IsServerAuthorized: true))).IsUsable);
        Assert.True((await original.SetAnswerAsync("q1", new(AnswerKind.Selected, ["A"]))).Succeeded);
        var fixedResult = await original.FixSubmissionAsync("existing-submission");
        Assert.True(fixedResult.Succeeded);
        var preserved = Assert.IsType<FixedSubmissionRecord>(fixedResult.Record);

        await using var runtime = fixture.Runtime();
        Assert.True(await runtime.InitializeLocalRecoveryAsync());
        await runtime.SynchronizeLocalAnswersAsync();
        Assert.True(runtime.HasFixedSubmission);
        Assert.Null(runtime.Answers);
        Assert.Equal(0, fixture.Api.PrepareCalls);
        Assert.Equal(0, fixture.Api.UnlockCalls);
        Assert.Contains("尚不能据此视为服务器交卷成功", runtime.LocalStateSummary);
        var retained = Assert.IsType<FixedSubmissionRecord>(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
        Assert.Equal(preserved.SubmissionId, retained.SubmissionId);
        Assert.Equal(preserved.RequestId, retained.RequestId);
        Assert.Equal(preserved.ParticipationId, retained.ParticipationId);
        Assert.Equal(preserved.AuthorizationVersion, retained.AuthorizationVersion);
        Assert.Equal(new[] { "A" }, retained.FrozenAnswers["q1"].OptionIds);
    }

    [Fact]
    public async Task ExistingStartedRecordAndEmptyReplacementDatabaseCannotCreateANewAnswerSession()
    {
        using var fixture = await Fixture.CreateAsync();
        await using (var first = fixture.Runtime())
        {
            Assert.True(await first.InitializeLocalRecoveryAsync());
            Assert.True(await first.Manager.PrepareAndDownloadAsync());
            Assert.True((await first.Manager.ConfirmParticipationAsync())?.IsSuccess);
            Assert.True(await first.Manager.HandleCommandAsync(fixture.Api.Start()));
            await first.SynchronizeLocalAnswersAsync();
            Assert.Equal(AnswerSessionStatus.Active, first.Answers?.Status);
        }

        using var emptyReplacement = fixture.EmptyReplacementStore();
        await using var restarted = fixture.Runtime(emptyReplacement);
        Assert.True(restarted.Manager.RequiresLocalAnswerRecovery);
        Assert.False(await restarted.InitializeLocalRecoveryAsync());
        await restarted.SynchronizeLocalAnswersAsync();
        Assert.False(restarted.LocalRecoveryChecked);
        Assert.Null(restarted.Answers);
        Assert.Contains("原本地答案缺失", restarted.LocalStateError);
        Assert.Equal(1, fixture.Api.PrepareCalls);
        Assert.Equal(1, fixture.Api.UnlockCalls);
        Assert.Null(await emptyReplacement.LoadAnswerStateAsync(fixture.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalCallbackDuringSqliteRecoveryCannotReactivateOrFabricateSubmission(bool revoked)
    {
        using var fixture = await Fixture.CreateAsync();
        var delayedStore = new DelayedAnswerReadStore(fixture.Store);
        await using var runtime = fixture.Runtime(delayedStore);
        Assert.True(await runtime.InitializeLocalRecoveryAsync());
        Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
        Assert.True((await runtime.Manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await runtime.Manager.HandleCommandAsync(fixture.Api.Start()));

        var recovery = runtime.SynchronizeLocalAnswersAsync();
        await delayedStore.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task terminal;
        try
        {
            // Invoke the same UI-dispatched callback used by the real feed, without creating
            // a WPF Application or changing the manager's private state in this unit test.
            terminal = revoked
                ? InvokeRuntimeCallback(runtime, "InvalidateAuthorizationOnUiAsync")
                : InvokeRuntimeCallback(runtime, "ApplyCommandAsync", fixture.Api.End(), CancellationToken.None);
            var answers = Assert.IsType<AnswerSession>(runtime.Answers);
            Assert.NotEqual(AnswerSessionStatus.Active, answers.Status);
            Assert.Null(runtime.Manager.UnlockedPayload);
        }
        finally { delayedStore.ContinueRead.TrySetResult(); }

        await Task.WhenAll(recovery, terminal).WaitAsync(TimeSpan.FromSeconds(10));
        var recovered = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.Equal(revoked ? AnswerSessionStatus.PermissionDenied : AnswerSessionStatus.Suspended, recovered.Status);
        Assert.False((await recovered.SetAnswerAsync("q1", new(AnswerKind.Selected, ["B"]))).Succeeded);
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
        Assert.False(runtime.HasFixedSubmission);
        Assert.Null(recovered.FixedSubmissionId);
        Assert.False(runtime.CanRetryLocalSave);
    }

    private static Task InvokeRuntimeCallback(StudentExamRuntime runtime, string methodName, params object[] arguments)
    {
        var method = typeof(StudentExamRuntime).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(runtime, arguments));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"exam-runtime-tests-{Guid.NewGuid():N}");
        public LocalStateKey Key { get; } = new("server", "exam", "exam-candidate");
        public SqliteLocalStateStore Store { get; }
        public PackageApi Api { get; }
        private readonly DeviceRecord _device = new("device", "test-credential", "20260922001", "127.0.0.1", 5443,
            DateTimeOffset.UtcNow, "server", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);
        private readonly StudentSessionResult _session = new("session", "device",
            new("login-candidate", "合成考生", "01", "001"), ["exam"], DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["exam"] = "exam-candidate" });

        private Fixture(PackageApi api)
        {
            Api = api;
            Store = new SqliteLocalStateStore(Path.Combine(_root, "state.db"));
        }
        public static async Task<Fixture> CreateAsync(bool invalidPackage = false) => new(await PackageApi.CreateAsync(invalidPackage));
        public StudentExamRuntime Runtime(ILocalStateStore? store = null) => new(
            new ExamPackageManager(Api, Path.Combine(_root, "packages"), _device, _session, "exam"),
            _device, store ?? Store, Key.CandidateId);
        public SqliteLocalStateStore EmptyReplacementStore() => new(Path.Combine(_root, "replacement-state.db"));
        public void Dispose()
        {
            Store.Dispose();
            Api.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class PackageApi : IExamPackageApi, IDisposable
    {
        public const string ManifestJson = """
            {"schemaVersion":1,"examId":"exam","examName":"接线合成考试",
             "subjects":[{"subjectId":"s1","name":"合成科目","maxScore":"2"}],
             "groups":[{"groupId":"g1","subjectId":"s1","name":"单选","type":"single","order":1,
               "questionCount":2,"maxScore":"2","scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
             "questions":[
               {"questionId":"q1","questionNo":1,"subjectId":"s1","groupId":"g1","source":"questions/接线合成考试_1.tex","options":["A","B"]},
               {"questionId":"q2","questionNo":2,"subjectId":"s1","groupId":"g1","source":"questions/接线合成考试_2.tex","options":["A","B"]}],
             "resourceRoots":[]}
            """;
        public const string AnswerKeyJson = """
            {"schemaVersion":1,"examId":"exam","answers":[
             {"questionId":"q1","questionNo":1,"correctOptionIds":["A"]},
             {"questionId":"q2","questionNo":2,"correctOptionIds":["B"]}]}
            """;
        private X509Certificate2 _node = null!;
        private EncryptedPackageEnvelope _envelope = null!;
        private byte[] _ciphertext = [];
        public bool Answering { get; set; }
        public int PrepareCalls { get; private set; }
        public int UnlockCalls { get; private set; }
        public static async Task<PackageApi> CreateAsync(bool invalidPackage)
        {
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=B04-runtime-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var api = new PackageApi { _node = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)) };
            var root = Path.Combine(Path.GetTempPath(), "runtime-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                var compiled = Directory.CreateDirectory(Path.Combine(root, "compiled")).FullName;
                await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), ManifestJson, Encoding.UTF8);
                foreach (var number in new[] { 1, 2 })
                    await File.WriteAllTextAsync(Path.Combine(compiled, $"接线合成考试_{number}.pdf"), "%PDF-1.4\n%synthetic\n%%EOF", new UTF8Encoding(false));
                var package = await new StudentPackageBuilder(new PackageEncryptionService(4096)).BuildAsync(
                    new(source, compiled, Path.Combine(root, "preview"), "package", 1,
                        Path.Combine(root, "cipher", "package.pkg"), [new("node", api._node)]));
                Assert.True(package.Succeeded, string.Join(";", package.Issues.Select(i => i.Message)));
                api._envelope = package.Envelope!;
                api._ciphertext = await File.ReadAllBytesAsync(package.EncryptedPackagePath!);
                if (invalidPackage)
                {
                    using var invalid = new MemoryStream("not a zip package"u8.ToArray());
                    using var cipher = new MemoryStream();
                    api._envelope = await new PackageEncryptionService(4096).EncryptAsync(invalid, cipher,
                        "package", 1, [new("node", api._node)]);
                    api._ciphertext = cipher.ToArray();
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            return api;
        }
        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default)
        {
            PrepareCalls++;
            return Task.FromResult(Ok(new EntryPreparationResult(request.RequestId, "exam", EntryPreparationStatus.Ready,
                new("/api/v1/resources/package", _ciphertext.Length, 1, 1), null, null, null, null, null, DateTimeOffset.UtcNow)));
        }
        public Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(string preparationId, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Ok(new InstructionsDocument(1, ["合成说明，仅供运行时集成测试。"] )));
        public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(ExamResourceDescriptor descriptor, string preparationId,
            string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
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
        public Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(PackageUnlockRequest request, CancellationToken ct = default)
        {
            UnlockCalls++;
            return Task.FromResult(Ok(new PackageUnlockMaterial(_envelope.PackageId, _envelope.PaperVersion,
                _envelope.ChunkSize, _envelope.NoncePrefix, PackageEncryptionService.UnwrapDataKey(_envelope, "node", _node))));
        }
        public Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(AcknowledgeCommandRequest request, CancellationToken ct = default) =>
            Task.FromResult(Ok(new CommandAcknowledgement(request.EventId, request.RequestId, request.ProcessingResult,
                request.ActualPaperVersion, request.FailureReason, DateTimeOffset.UtcNow)));
        public CommandDelivery Start() => new(new("server", "node", 1), "start", "exam", "participation", 1,
            CommandEventType.Start, 1, "device", 1, 1, "resource-envelope:package", new Dictionary<string, string>(), DateTimeOffset.UtcNow, 1);
        public CommandDelivery End() => Start() with { EventId = "end", EventType = CommandEventType.End, StateVersion = 2 };
        private static ApiEnvelope<T> Ok<T>(T value) => new("server", "node", 1, "correlation", value, null);
        public void Dispose() => _node.Dispose();
    }

    /// <summary>Only delays a real SQLite read; every durable operation still uses the production store.</summary>
    private sealed class DelayedAnswerReadStore(ILocalStateStore inner) : ILocalStateStore
    {
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default)
        {
            ReadEntered.TrySetResult();
            await ContinueRead.Task.WaitAsync(ct);
            return await inner.LoadAnswerStateAsync(key, ct);
        }
        public Task<StoreWriteResult> SaveAnswerStateAsync(LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default) =>
            inner.SaveAnswerStateAsync(key, state, snapshot, ct);
        public Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default) =>
            inner.SaveMarksAsync(key, marks, ct);
        public Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadPendingSnapshotAsync(key, ct);
        public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default) =>
            inner.TryFixSubmissionAsync(key, record, ct);
        public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(LocalStateKey key, FixedSubmissionRecord record,
            AnswerStateRecord pendingState, PendingSnapshot pendingSnapshot, CancellationToken ct = default) =>
            inner.TryFixSubmissionAsync(key, record, pendingState, pendingSnapshot, ct);
        public Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadFixedSubmissionAsync(key, ct);
        public Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default) => inner.SavePreparationAsync(key, record, ct);
        public Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadPreparationAsync(key, ct);
        public Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public void Dispose() { } // The fixture owns the underlying store.
    }
}
