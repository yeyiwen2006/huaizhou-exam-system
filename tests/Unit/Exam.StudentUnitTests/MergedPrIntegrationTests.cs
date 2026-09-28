using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;
using Exam.Contracts.Submissions;
using Exam.Application.Submissions;
using Exam.Domain.Submissions;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class MergedPrIntegrationTests
{
    [Fact]
    public async Task D03V2Package_OptionRegions_RoundTripThroughStudentReader()
    {
        // D03 组包器写入的坐标产物，必须能被学生端读取器原样接受并逐题逐选项核对通过。
        var artifact = new OptionRegionArtifact(
            OptionRegionArtifact.CurrentArtifactVersion,
            OptionRegionArtifact.AnchorSchemeName,
            "exam",
            1,
            [
                new OptionRegionQuestion("q1", FourOptions(0.2300)),
                new OptionRegionQuestion("q2", FourOptions(0.5400))
            ]);
        using var fixture = await StudentPaperFixture.CreateAsync(optionRegions: artifact);

        var plaintext = await fixture.DecryptAsStudentAsync();
        var manifest = StudentPaperPackage.ReadManifest(plaintext, "exam", 1);

        Assert.Equal(StudentPaperPackage.OptionRegionSchemaVersion, manifest.SchemaVersion);
        var map = OptionRegionMap.TryReadFromPackage(plaintext, manifest);
        Assert.NotNull(map);
        var first = map!.ForQuestion("q1")!;
        Assert.Equal(4, first.Count);
        Assert.Equal(0.2300, first["A"].X, 6);
        Assert.Equal(0, first["A"].PageIndex);
        Assert.Equal(0.5400, map.ForQuestion("q2")!["D"].X, 6);
    }

    private static List<Exam.Contracts.Importing.OptionRegion> FourOptions(double top)
    {
        var optionIds = new[] { "A", "B", "C", "D" };
        return optionIds
            .Select((optionId, index) => new Exam.Contracts.Importing.OptionRegion(
                optionId, 0, top, 0.18 + (index * 0.03), 0.02, 0.02))
            .ToList();
    }

    [Fact]
    public async Task D03Package_B02Unlock_B04Freeze_A05Submission_D04Grade_PreservesUntouchedQuestions()
    {
        using var fixture = await StudentPaperFixture.CreateAsync();
        await using var runtime = await fixture.StartRuntimeAsync();
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.Equal(AnswerSessionStatus.Active, answers.Status);
        Assert.Equal(2, answers.Answers.Count);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var fixedResult = await answers.FixSubmissionAsync("synthetic-submission");
        Assert.True(fixedResult.Succeeded, fixedResult.Error);
        var frozen = Assert.IsType<FixedSubmissionRecord>(fixedResult.Record);
        Assert.Equal(2, frozen.FrozenAnswers.Count);
        Assert.Equal(AnswerKind.Empty, frozen.FrozenAnswers["q2"].Kind);
        Assert.Empty(frozen.FrozenAnswers["q2"].OptionIds);
        // Exercise the A05 JSON boundary and application service without claiming a B05 UI/worker exists.
        var store = new InMemorySubmissionStore();
        var queue = new InMemoryGradingTaskQueue();
        await store.SetAuthorizationStateAsync(new("exam", "candidate", "participation", 1, "device", 1,
            ParticipationBusinessState.Answering, 1, 1));
        var rules = fixture.ImportedRules();
        using var service = new SubmissionService(store, queue, paperReader: id => id == rules.ExamId ? rules.Structure : null);
        var request = new SubmitFinalAnswersRequest(frozen.RequestId!, frozen.SubmissionId, "exam", "candidate",
            frozen.ParticipationId!, frozen.EntryVersion, frozen.DeviceId!, frozen.AuthorizationVersion,
            frozen.PaperVersion, frozen.FrozenAnswers);
        var wireRequest = JsonSerializer.Deserialize<SubmitFinalAnswersRequest>(
            JsonSerializer.Serialize(request, JsonDefaults.Create()), JsonDefaults.Create())!;
        var receipt = await service.SubmitAsync(wireRequest);
        Assert.True(receipt.IsSuccess, receipt.Message);
        var serverReplay = await service.SubmitAsync(wireRequest);
        Assert.True(serverReplay.IsReplay);
        Assert.Equal(receipt.Value, serverReplay.Value);
        Assert.Equal(1, queue.Count);
        var persisted = Assert.IsType<FinalSubmission>(await store.FindSubmissionAsync("exam", "candidate"));
        var grade = rules.Grade(persisted, persisted.Receipt.GradingVersion);
        Assert.True(grade.Succeeded, grade.FailureReason);
        Assert.Equal("3", grade.Grade!.ExactTotalScore);
        Assert.Equal(2, grade.Grade.Questions.Count);
        var replay = await answers.FixSubmissionAsync("different-submission");
        Assert.True(replay.IsReplay);
        Assert.Equal(frozen.SubmissionId, replay.Record!.SubmissionId);
        Assert.Equal(frozen.RequestId, replay.Record.RequestId);
    }

    [Fact]
    public async Task AuthenticatedNonZipPayloadCannotCreateAnAnswerSession()
    {
        using var fixture = await StudentPaperFixture.CreateAsync(
            plaintextOverride: Encoding.UTF8.GetBytes("synthetic authenticated payload, not a student package"));
        await using var runtime = await fixture.StartRuntimeAsync();
        Assert.Null(runtime.Answers);
        Assert.NotNull(runtime.LocalStateError);
        Assert.Null(await fixture.Store.LoadAnswerStateAsync(fixture.Key));
    }

    [Fact]
    public async Task PackageForAnotherExamCannotCreateAnAnswerSession()
    {
        using var fixture = await StudentPaperFixture.CreateAsync(manifestExamId: "another-exam");
        await using var runtime = await fixture.StartRuntimeAsync();
        Assert.Null(runtime.Answers);
        Assert.NotNull(runtime.LocalStateError);
        Assert.Null(await fixture.Store.LoadAnswerStateAsync(fixture.Key));
    }

    [Theory]
    [InlineData("unknown", "A")]
    [InlineData("Q1", "A")]
    [InlineData("q1", "Z")]
    [InlineData("q1", "A,B")]
    public async Task UndeclaredQuestionsOptionsAndSingleChoiceMultiSelectionsAreRejected(string questionId, string options)
    {
        using var fixture = await StudentPaperFixture.CreateAsync();
        await using var runtime = await fixture.StartRuntimeAsync();
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        var before = answers.AnswerVersion;
        Assert.False((await answers.SetAnswerAsync(questionId, AnswerValue.Selected(options.Split(',')))).Succeeded);
        Assert.Equal(before, answers.AnswerVersion);
        Assert.DoesNotContain("unknown", answers.Answers.Keys);
    }

    [Fact]
    public async Task AbstainAndNavigationUseThePublishedQuestionCatalog()
    {
        using var fixture = await StudentPaperFixture.CreateAsync();
        await using var runtime = await fixture.StartRuntimeAsync();
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.False((await answers.SetAnswerAsync("q1", AnswerValue.Abstain())).Succeeded);
        Assert.True((await answers.SetAnswerAsync("q2", AnswerValue.Abstain())).Succeeded);
        Assert.False((await answers.ToggleMarkAsync("unknown")).Succeeded);
        Assert.False((await answers.SetCurrentQuestionAsync("unknown")).Succeeded);
        Assert.True((await answers.ToggleMarkAsync("q2")).Succeeded);
    }

    [Fact]
    public async Task SparseRecoveryRecordIsPreservedRatherThanSilentlyFilledOrFrozen()
    {
        using var fixture = await StudentPaperFixture.CreateAsync();
        var legacy = new AnswerSession(fixture.Store);
        Assert.True((await legacy.StartAsync(fixture.Key, StudentPaperFixture.Context)).IsUsable);
        Assert.True((await legacy.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        var version = legacy.AnswerVersion;
        await using var runtime = await fixture.StartRuntimeAsync();
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.Equal(AnswerSessionStatus.PermissionDenied, answers.Status);
        Assert.False((await answers.FixSubmissionAsync("must-not-freeze")).Succeeded);
        var saved = Assert.IsType<AnswerStateRecord>(await fixture.Store.LoadAnswerStateAsync(fixture.Key));
        Assert.Equal(version, saved.AnswerVersion);
        Assert.Single(saved.Answers);
        Assert.Equal(new[] { "A" }, saved.Answers["q1"].OptionIds);
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
    }
}

/// <summary>Synthetic D03 producer and B02 transport; SQLite, encryption and runtime are production implementations.</summary>
internal sealed class StudentPaperFixture : IExamPackageApi, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"exam-pr-integration-{Guid.NewGuid():N}");
    private X509Certificate2 _node = null!;
    private EncryptedPackageEnvelope _envelope = null!;
    private byte[] _ciphertext = [];
    public ExamManifest Manifest { get; private set; } = null!;
    public LocalStateKey Key { get; } = new("server", "exam", "candidate");
    public static AnswerSessionContext Context { get; } = new("participation", 1, 1, 1, "device", true);
    public SqliteLocalStateStore Store { get; }
    private readonly DeviceRecord _device = new("device", "synthetic-credential", "20260924001", "127.0.0.1", 5443,
        DateTimeOffset.UtcNow, "server", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);
    private readonly StudentSessionResult _session = new("session", "device",
        new("candidate", "合成考生", "01", "001"), ["exam"], DateTimeOffset.UtcNow);

    private StudentPaperFixture() => Store = new SqliteLocalStateStore(Path.Combine(_root, "state.db"));

    public static async Task<StudentPaperFixture> CreateAsync(string manifestExamId = "exam", byte[]? plaintextOverride = null,
        OptionRegionArtifact? optionRegions = null)
    {
        var fixture = new StudentPaperFixture();
        try
        {
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=synthetic-pr-integration", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            fixture._node = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            fixture.Manifest = CreateManifest(manifestExamId);
            var input = Path.Combine(fixture._root, "input");
            var compiled = Path.Combine(fixture._root, "compiled");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(compiled);
            await File.WriteAllTextAsync(Path.Combine(input, "manifest.json"),
                JsonSerializer.Serialize(fixture.Manifest, ImportJson.Create()));
            foreach (var question in fixture.Manifest.Questions)
                await File.WriteAllTextAsync(Path.Combine(compiled, Path.GetFileNameWithoutExtension(question.Source) + ".pdf"),
                    "%PDF-1.4\n% Synthetic header-only fixture; not a rendering/TeX acceptance test.\n");
            var encryption = new PackageEncryptionService(4096);
            var request = new StudentPackageRequest(input, compiled,
                Path.Combine(fixture._root, "preview"), "package", 1, Path.Combine(fixture._root, "paper.epkg"),
                [new("node", fixture._node)]) { OptionRegions = optionRegions };
            var result = await new StudentPackageBuilder(encryption).BuildAsync(request);
            Assert.True(result.Succeeded, string.Join("; ", result.Issues.Select(issue => issue.Message)));
            fixture._envelope = result.Envelope!;
            fixture._ciphertext = await File.ReadAllBytesAsync(result.EncryptedPackagePath!);
            if (plaintextOverride is not null)
            {
                using var ciphertext = new MemoryStream();
                using var plaintext = new MemoryStream(plaintextOverride);
                fixture._envelope = await encryption.EncryptAsync(plaintext, ciphertext, "package", 1, [new("node", fixture._node)]);
                fixture._ciphertext = ciphertext.ToArray();
            }
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    public static ExamManifest CreateManifest(string examId = "exam") => new(1, examId, "衔接合成考试",
        [new("subject", "合成科目", "5")],
        [new("single", "subject", "单项选择", "single", 1, 1, false, "3", new(new("3", "0", "-0.75"), null)),
         new("multiple", "subject", "多项选择", "multiple", 2, 1, true, "2", new(null, new("2", "1", "0", "-1")))],
        [new("q1", 1, "subject", "single", "questions/衔接合成考试_1.tex", ["A", "B", "C", "D"], null),
         new("q2", 2, "subject", "multiple", "questions/衔接合成考试_2.tex", ["A", "B", "C", "D"], null)], []);

    public ImportedGradingRules ImportedRules() => ImportedGradingRules.Create(
        JsonSerializer.Serialize(Manifest, ImportJson.Create()),
        JsonSerializer.Serialize(new AnswerKeyFile(1, Manifest.ExamId,
            [new("q1", 1, ["A"]), new("q2", 2, ["A", "B"])]), ImportJson.Create()));

    public static StudentPaperManifest CreateStudentManifest()
    {
        var manifest = CreateManifest();
        var structure = ImportedGradingRules.BuildStructure(manifest);
        return new(1, manifest.ExamId, manifest.ExamName, 1,
            manifest.Subjects.Select(s => new StudentPaperSubject(s.SubjectId, s.Name)).ToArray(),
            manifest.Groups.Select(g => new StudentPaperGroup(g.GroupId, g.SubjectId, g.Name, g.Order)).ToArray(),
            manifest.Questions.Select((q, i) => new StudentPaperQuestion(structure.Questions[i],
                StudentPackageLayout.QuestionPdfPath(q.Source), null)).ToArray(), []);
    }

    public async Task<StudentExamRuntime> StartRuntimeAsync()
    {
        var runtime = new StudentExamRuntime(new ExamPackageManager(this, Path.Combine(_root, "packages"), _device, _session, "exam"),
            _device, Store, Key.CandidateId);
        try
        {
            Assert.True(await runtime.InitializeLocalRecoveryAsync());
            Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
            Assert.True((await runtime.Manager.ConfirmParticipationAsync())?.IsSuccess);
            Assert.Equal(ExamPackageManager.Phase.Unlocked, runtime.Manager.CurrentPhase);
            await runtime.SynchronizeLocalAnswersAsync();
            return runtime;
        }
        catch { await runtime.DisposeAsync(); throw; }
    }

    /// <summary>
    /// 按学生端的解密路径还原明文包，供读取器直接核对。夹具已经把数据密钥随解锁材料下发，
    /// 这里不额外绕过任何校验。
    /// </summary>
    public async Task<byte[]> DecryptAsStudentAsync()
    {
        var material = new PackageUnlockMaterial(_envelope.PackageId, _envelope.PaperVersion, _envelope.ChunkSize,
            _envelope.NoncePrefix, PackageEncryptionService.UnwrapDataKey(_envelope, "node", _node));
        await using var plaintext = new MemoryStream();
        await using var ciphertext = new MemoryStream(_ciphertext, writable: false);
        await PackageCiphertext.DecryptAsync(ciphertext, plaintext, material);
        return plaintext.ToArray();
    }

    public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default) =>
        Task.FromResult(Ok(new EntryPreparationResult(request.RequestId, "exam", EntryPreparationStatus.Ready,
            new("/api/v1/resources/package", _ciphertext.Length, 1, 1), null, null, null, null, null, DateTimeOffset.UtcNow)));
    public Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(string preparationId, string sessionId, CancellationToken ct = default) =>
        Task.FromResult(Ok(new InstructionsDocument(1, ["合成接口测试说明。"] )));
    public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(ExamResourceDescriptor descriptor, string preparationId,
        string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return _ciphertext;
    }
    public Task<ApiEnvelope<ParticipationResult>> ConfirmParticipationAsync(ConfirmParticipationRequest request, CancellationToken ct = default) =>
        Task.FromResult(Ok(new ParticipationResult("participation", "exam", "candidate", ParticipationBusinessState.Answering,
            "device", 1, 1, 1, "resource-envelope:package", Lifecycle: ExamLifecycle.Running)));
    public Task<ApiEnvelope<ParticipationResult>> WithdrawParticipationAsync(WithdrawParticipationRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Not exercised by this synthetic fixture.");
    public Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(PackageUnlockRequest request, CancellationToken ct = default) =>
        Task.FromResult(Ok(new PackageUnlockMaterial(_envelope.PackageId, _envelope.PaperVersion, _envelope.ChunkSize,
            _envelope.NoncePrefix, PackageEncryptionService.UnwrapDataKey(_envelope, "node", _node))));
    public Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(AcknowledgeCommandRequest request, CancellationToken ct = default) =>
        Task.FromResult(Ok(new CommandAcknowledgement(request.EventId, request.RequestId, request.ProcessingResult,
            request.ActualPaperVersion, request.FailureReason, DateTimeOffset.UtcNow)));
    private static ApiEnvelope<T> Ok<T>(T value) => new("server", "node", 1, "synthetic-request", value, null);
    public void Dispose()
    {
        Store.Dispose();
        _node?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
