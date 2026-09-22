using System.IO;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// 受控假服务：不依赖 MockStudentApi 的演示延迟与魔法考试号，
/// 每个用例显式配置成功/失败路径。
/// </summary>
public sealed class FakeStudentApi : IStudentApi
{
    public byte[] PackageBytes { get; set; } = Array.Empty<byte>();

    public byte[] Key { get; set; } = SyntheticExamPackage.CreateKey();

    public InstructionsDocument? Instructions { get; set; } =
        new(1, new[] { "说明第一行", "说明第二行" });

    /// <summary>下载至该字节数后抛出 IO 异常（模拟中断/超时）；null 表示正常完成。</summary>
    public int? FailDownloadAtBytes { get; set; }

    /// <summary>确认参试时服务器侧返回“考试已结束”。</summary>
    public bool ExamEndedOnServer { get; set; }

    /// <summary>确认参试时返回答题态（迟到进入情景）。</summary>
    public bool ConfirmAsAnswering { get; set; }

    public long ExpectedPaperVersion { get; set; } = 1;

    public byte[]? PlaintextUsedToSeal { get; private set; }

    public ExamResourceDescriptor Descriptor => BuildDescriptor();

    public ExamResourceDescriptor BuildDescriptor(long? packageLengthOverride = null)
    {
        var plaintext = SyntheticExamPackage.CreatePlaintext("fake-exam", ExpectedPaperVersion);
        PlaintextUsedToSeal = plaintext;
        PackageBytes = SyntheticExamPackage.Seal(plaintext, Key);
        return new ExamResourceDescriptor(
            "https://fake.local/pkg.bin",
            packageLengthOverride ?? PackageBytes.Length,
            ExpectedPaperVersion,
            1);
    }

    public Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(
        SubmitActivationRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("B02 状态机不使用激活接口。");

    public Task<ApiEnvelope<StudentSessionResult>> LoginAsync(
        LoginStudentRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("B02 状态机不使用登录接口。");

    public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(
        PrepareExamEntryRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("B02 状态机不使用准备接口（由页面驱动）。");

    public Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("B02 状态机不使用概览接口。");

    public Task<InstructionsDocument?> GetInstructionsAsync(
        string sessionId, string examId, long instructionsVersion, CancellationToken ct = default)
        => Task.FromResult(Instructions);

    public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(
        ExamResourceDescriptor descriptor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var sent = 0;
        const int chunkSize = 64;
        while (sent < PackageBytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (FailDownloadAtBytes is int limit && sent >= limit)
            {
                throw new IOException("模拟下载中断。");
            }

            var size = Math.Min(chunkSize, PackageBytes.Length - sent);
            yield return PackageBytes[sent..(sent + size)];
            sent += size;
            await Task.Delay(1, ct);
        }
    }

    public Task<ApiEnvelope<ParticipationResult>?> ConfirmParticipationAsync(
        ConfirmParticipationRequest request, CancellationToken ct = default)
    {
        if (ExamEndedOnServer)
        {
            return Task.FromResult<ApiEnvelope<ParticipationResult>?>(
                Fail<ParticipationResult>(ParticipationErrorCodes.EntryNotAllowed, "考试已结束，不能新增参试。"));
        }

        if (request.DownloadedPaperVersion != ExpectedPaperVersion)
        {
            return Task.FromResult<ApiEnvelope<ParticipationResult>?>(
                Fail<ParticipationResult>(ParticipationErrorCodes.PreparationVersionMismatch, "试题包或考试说明未就绪，不能确认参试。"));
        }

        return Task.FromResult<ApiEnvelope<ParticipationResult>?>(new ApiEnvelope<ParticipationResult>(
            "fake", "node", 1, "corr",
            new ParticipationResult(
                "part-1", request.ExamId, "cand-1",
                ConfirmAsAnswering ? ParticipationBusinessState.Answering : ParticipationBusinessState.Waiting,
                "device-1", 1, 1, 1, "unlock:fake"),
            null));
    }

    public Task<ApiEnvelope<ParticipationResult>?> WithdrawParticipationAsync(
        WithdrawParticipationRequest request, CancellationToken ct = default)
        => Task.FromResult<ApiEnvelope<ParticipationResult>?>(new ApiEnvelope<ParticipationResult>(
            "fake", "node", 1, "corr",
            new ParticipationResult(
                request.ParticipationId, string.Empty, "cand-1",
                ParticipationBusinessState.NotParticipating, "device-1", 1, 1, 1, null),
            null));

    public Task<byte[]?> ResolveUnlockKeyAsync(string unlockReference, CancellationToken ct = default)
        => Task.FromResult(unlockReference == "unlock:fake" ? Key : null);

    private static ApiEnvelope<T> Fail<T>(string code, string message) => new(
        "fake", "node", 1, "corr", default, new ApiError(code, message));
}

public sealed class ExamPackageManagerTests : IDisposable
{
    private readonly FakeStudentApi _api = new();
    private readonly string _root;
    private readonly ExamPackageManager _manager;

    public ExamPackageManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hz-exam-tests", Guid.NewGuid().ToString("N"));
        _manager = new ExamPackageManager(_api, _root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task<bool> DownloadNormalAsync() =>
        _manager.DownloadAsync("sess-1", "fake-exam", _api.Descriptor);

    [Fact]
    public async Task Download_Completes_AtomicEncryptedFileAndInstructions()
    {
        var ok = await DownloadNormalAsync();

        Assert.True(ok);
        Assert.True(_manager.HasPackage);
        Assert.True(_manager.HasInstructions);
        Assert.True(File.Exists(_manager.PackageFilePath));
        Assert.Equal(ExamPackageManager.Phase.Downloaded, _manager.CurrentPhase);
        Assert.Equal(2, _manager.Instructions!.Lines.Count);
    }

    [Fact]
    public async Task ConfirmBlocked_WhenPackageMissing()
    {
        _api.FailDownloadAtBytes = 32; // 下载中途失败

        var ok = await DownloadNormalAsync();

        Assert.False(ok);
        Assert.False(_manager.HasPackage);
        Assert.False(_manager.CanConfirmParticipation);
        Assert.False(File.Exists(_manager.PackageFilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
        Assert.Empty(Directory.GetFiles(_root, "*.pkg"));
    }

    [Fact]
    public async Task ConfirmBlocked_WhenInstructionsMissing()
    {
        _api.Instructions = null;

        var ok = await DownloadNormalAsync();

        Assert.False(ok); // 说明缺失视为准备未完成
        Assert.True(_manager.HasPackage);
        Assert.False(_manager.HasInstructions);
        Assert.False(_manager.CanConfirmParticipation);
    }

    [Fact]
    public async Task ConfirmBlocked_WhenExamEnded_AfterDownloadComplete()
    {
        await DownloadNormalAsync();

        _manager.OnExamEnded(); // 下载完成后考试结束（本地/服务器判定）

        Assert.False(_manager.CanConfirmParticipation);

        var result = await _manager.ConfirmParticipationAsync("sess-1", "fake-exam");
        Assert.Null(result); // 状态机直接拒绝，不发起请求
    }

    [Fact]
    public async Task ConfirmFailsOnServer_WhenExamEndedDuringDownload()
    {
        _api.ExamEndedOnServer = true; // 服务器侧：结束后下载才完成不能新增参试
        await DownloadNormalAsync();

        var result = await _manager.ConfirmParticipationAsync("sess-1", "fake-exam");

        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
        Assert.Equal(ParticipationErrorCodes.EntryNotAllowed, result.Error!.Code);
        Assert.Null(_manager.ParticipationId);
    }

    [Fact]
    public async Task NoPlaintext_BeforeStartCommand_EvenAfterConfirm()
    {
        await DownloadNormalAsync();
        var confirm = await _manager.ConfirmParticipationAsync("sess-1", "fake-exam");
        Assert.True(confirm!.IsSuccess);

        // 磁盘：只有密文（不含明文标记）
        var onDisk = await File.ReadAllBytesAsync(_manager.PackageFilePath);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes("synthetic-student-package"), onDisk);

        // 内存：开考前无明文，且提前解锁被硬约束拒绝
        Assert.Null(_manager.UnlockedPayload);
        Assert.False(_manager.ExamStarted);
        Assert.False(await _manager.UnlockAsync());
    }

    [Fact]
    public async Task Unlock_Succeeds_AfterStartCommand()
    {
        await DownloadNormalAsync();
        await _manager.ConfirmParticipationAsync("sess-1", "fake-exam");

        var ok = await _manager.HandleStartCommandAsync(StartDelivery());

        Assert.True(ok);
        Assert.Equal(ExamPackageManager.Phase.Unlocked, _manager.CurrentPhase);
        Assert.NotNull(_manager.UnlockedPayload);
        Assert.Equal(_api.PlaintextUsedToSeal, _manager.UnlockedPayload);
    }

    [Fact]
    public async Task Unlock_Succeeds_Immediately_WhenAlreadyAnswering()
    {
        _api.ConfirmAsAnswering = true; // 迟到进入：服务器确认时已告知答题态
        await DownloadNormalAsync();

        var confirm = await _manager.ConfirmParticipationAsync("sess-1", "fake-exam");
        Assert.Equal(ParticipationBusinessState.Answering, confirm!.Result!.BusinessState);

        var ok = await _manager.UnlockAsync();
        Assert.True(ok);
        Assert.Equal(_api.PlaintextUsedToSeal, _manager.UnlockedPayload);
    }

    [Fact]
    public async Task StartCommand_Ignored_BeforeConfirm()
    {
        await DownloadNormalAsync();

        var ok = await _manager.HandleStartCommandAsync(StartDelivery());

        Assert.False(ok); // 未确认参试：开考指令不产生明文
        Assert.Null(_manager.UnlockedPayload);
    }

    [Fact]
    public async Task LengthMismatch_TreatedAsFailure_NoFinalFile()
    {
        var descriptor = _api.BuildDescriptor(packageLengthOverride: _api.PackageBytes.Length + 99);

        var ok = await _manager.DownloadAsync("sess-1", "fake-exam", descriptor);

        Assert.False(ok);
        Assert.False(_manager.HasPackage);
        Assert.Empty(Directory.GetFiles(_root, "*.pkg"));
    }

    [Fact]
    public async Task CanceledDownload_ThrowsAndCleansPartialFile()
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _manager.DownloadAsync("sess-1", "fake-exam", _api.Descriptor, null, cts.Token));

        Assert.Empty(Directory.GetFiles(_root, "*.part"));
        Assert.Empty(Directory.GetFiles(_root, "*.pkg"));
        Assert.False(_manager.HasPackage);
    }

    [Fact]
    public async Task WrongUnlockReference_FailsToUnlock()
    {
        await DownloadNormalAsync();
        await _manager.ConfirmParticipationAsync("sess-1", "fake-exam");

        // 开考指令携带错误的解锁引用 → 服务器换不出密钥 → 解锁失败
        var ok = await _manager.HandleStartCommandAsync(StartDelivery("unlock:unknown"));

        Assert.False(ok);
        Assert.Null(_manager.UnlockedPayload);
    }

    private static CommandDelivery StartDelivery(string unlockReference = "unlock:fake") => new(
        Server: new ServerNodeEnvelope("fake", "node", 1),
        EventId: "evt-1",
        ExamId: "fake-exam",
        ParticipationId: "part-1",
        EntryVersion: 1,
        EventType: CommandEventType.Start,
        StateVersion: 1,
        TargetDeviceId: "device-1",
        TargetAuthorizationVersion: 1,
        ResourceVersion: 1,
        ProtectedUnlockReference: unlockReference,
        Metadata: new Dictionary<string, string>(),
        CreatedAt: DateTimeOffset.Now,
        DeliveryAttempt: 1);
}

public sealed class SyntheticExamPackageTests
{
    [Fact]
    public void RoundTrip_Succeeds()
    {
        var key = SyntheticExamPackage.CreateKey();
        var plaintext = SyntheticExamPackage.CreatePlaintext("demo-exam", 3);

        var package = SyntheticExamPackage.Seal(plaintext, key);

        Assert.True(SyntheticExamPackage.TryUnseal(package, key, out var decrypted));
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void WrongKey_Fails()
    {
        var key = SyntheticExamPackage.CreateKey();
        var other = SyntheticExamPackage.CreateKey();
        var package = SyntheticExamPackage.Seal(
            SyntheticExamPackage.CreatePlaintext("demo-exam", 1), key);

        Assert.False(SyntheticExamPackage.TryUnseal(package, other, out _));
    }

    [Fact]
    public void TamperedPackage_FailsAuthentication()
    {
        var key = SyntheticExamPackage.CreateKey();
        var package = SyntheticExamPackage.Seal(
            SyntheticExamPackage.CreatePlaintext("demo-exam", 1), key);

        package[^3] ^= 0xFF; // 篡改密文/标签

        Assert.False(SyntheticExamPackage.TryUnseal(package, key, out _));
    }

    [Fact]
    public void SealedPackage_ContainsNoPlaintextMarker()
    {
        var key = SyntheticExamPackage.CreateKey();
        var plaintext = SyntheticExamPackage.CreatePlaintext("demo-exam", 1);

        var package = SyntheticExamPackage.Seal(plaintext, key);

        Assert.DoesNotContain(Encoding.UTF8.GetBytes("synthetic-student-package"), package);
    }
}
