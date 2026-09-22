using System.IO;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;

namespace Exam.Student.Core;

/// <summary>
/// 考试概览（学生端展示用）。
/// 契约缺口：StudentSessionResult 只含 ExamIds，尚无考试名称/时间/科目/lifecycle 的设备侧查询契约，
/// 已记录于 docs/b01-implementation-status.md，待 A 补充后由真实接口替换本展示层。
/// </summary>
public sealed record ExamOverview(
    string ExamId,
    string Name,
    DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd,
    IReadOnlyList<string> Subjects,
    ExamLifecycle Lifecycle);

/// <summary>
/// 学生端服务接口：请求/响应全部使用 Exam.Contracts 类型，
/// 与服务器 API 封套（ApiEnvelope）同构，A01 契约的 HTTP 客户端就绪后仅替换实现，页面不变。
/// B02 新增：说明获取、包分块下载、确认/退出参试、解锁密钥换取。
/// 契约缺口（待 A×D 定稿）：包传输与完整性校验方式、解锁引用格式——见 docs/b01-implementation-status.md。
/// </summary>
public interface IStudentApi
{
    /// <summary>提交/轮询激活请求（同 RequestId 重复提交为幂等查询，见执行指南设备激活接口）。</summary>
    Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(
        SubmitActivationRequest request, CancellationToken ct = default);

    /// <summary>考生登录（核对名册）。</summary>
    Task<ApiEnvelope<StudentSessionResult>> LoginAsync(
        LoginStudentRequest request, CancellationToken ct = default);

    /// <summary>进入准备（等待考试 / 已开始进入）。</summary>
    Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(
        PrepareExamEntryRequest request, CancellationToken ct = default);

    /// <summary>考试概览列表（B01 展示层，见契约缺口说明）。</summary>
    Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(CancellationToken ct = default);

    /// <summary>获取考试说明（按说明版本）。</summary>
    Task<InstructionsDocument?> GetInstructionsAsync(
        string sessionId, string examId, long instructionsVersion, CancellationToken ct = default);

    /// <summary>分块下载加密包（顺序流；传输契约待 A×D 定稿，现为 Mock 本地流）。</summary>
    IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(
        ExamResourceDescriptor descriptor, CancellationToken ct = default);

    /// <summary>确认参试（上报已下载的试卷与说明版本）。</summary>
    Task<ApiEnvelope<ParticipationResult>?> ConfirmParticipationAsync(
        ConfirmParticipationRequest request, CancellationToken ct = default);

    /// <summary>退出参试（等待页退出）。</summary>
    Task<ApiEnvelope<ParticipationResult>?> WithdrawParticipationAsync(
        WithdrawParticipationRequest request, CancellationToken ct = default);

    /// <summary>以解锁引用换取内容密钥（解锁引用格式为 A04×D03 契约缺口，现为 Mock 直换）。</summary>
    Task<byte[]?> ResolveUnlockKeyAsync(string unlockReference, CancellationToken ct = default);
}

public static class RequestIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}

/// <summary>服务器指令订阅（B02 仅消费 Start；A04 SignalR 通道就绪后替换实现）。</summary>
public interface IExamCommandFeed
{
    event EventHandler<CommandDelivery>? CommandReceived;
    Task StartAsync(string sessionId, string examId);
    Task StopAsync();
}

/// <summary>
/// Mock 实现：固定延迟 + 确定性规则，覆盖 B01/B02 页面需要的成功与错误路径。
/// 数据均为独立合成样例，不含任何真实试卷；联调后由真实 HTTP 客户端替换。
/// 情景（examId）：
///  demo-exam          —— 正常流程：下载→确认→（模拟开考指令）→解锁；
///  demo-late          —— 已开始的考试：同一流程进入，确认后立即解锁；
///  demo-package-timeout —— 包下载失败（模拟超时）：不可确认参试；
///  demo-closed        —— 已结束/不可进入。
/// </summary>
public sealed class MockStudentApi : IStudentApi
{
    private static readonly string[] SampleInstructionLines =
    {
        "数学（满分100分）",
        "一、不定项选择题：本题共30小题，每小题3分或4分，共计100分。若有一个正确答案，选对得3分，不选得0分，选错得0分；若有多个正确答案，全部选对得4分，选对但不全得2分，不选得0分，选错得0分。",
        "物理（满分60分）",
        "一、不定项选择题：本题共20小题，每小题3分，共计60分。若有一个正确答案，选对得3分，不选得0分，选错得0分；若有多个正确答案，全部选对得3分，选对但不全得1分，不选得0分，选错得0分。",
        "化学（满分40分）",
        "一、单项选择题：本题共24小题，每小题{正确选项总数}分，共计40分。若有一个正确答案，选对得1分，不选得0分，选错得0分；若有多个正确答案，全部选对得{正确选项总数}分，选对但不全得{选择的正确选项数}分，不选得0分，选错得0分。",
    };

    private readonly Dictionary<string, int> _activationPollCount = new();
    private readonly Dictionary<string, byte[]> _unlockKeys = new();
    private readonly TimeSpan _latency;

    /// <summary>确认参试成功后触发（Mock 指令源用于调度开考指令）。</summary>
    public event Action<ParticipationResult>? ParticipationConfirmed;

    public MockStudentApi(TimeSpan? latency = null) => _latency = latency ?? TimeSpan.FromMilliseconds(700);

    public static IReadOnlyList<string> InstructionsFor(string examId) =>
        examId == "demo-exam" || examId == "demo-late" ? SampleInstructionLines : Array.Empty<string>();

    /// <summary>生成（或复用）某考试的加密包，返回包字节与解锁引用。</summary>
    public (byte[] Package, string UnlockReference) GetOrBuildPackage(string examId, long paperVersion)
    {
        var key = _unlockKeys.TryGetValue($"key:{examId}", out var existing)
            ? existing
            : SyntheticExamPackage.CreateKey();
        _unlockKeys[$"key:{examId}"] = key;
        var package = SyntheticExamPackage.Seal(SyntheticExamPackage.CreatePlaintext(examId, paperVersion), key);
        return (package, $"unlock:{examId}");
    }

    public async Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(
        SubmitActivationRequest request, CancellationToken ct = default)
    {
        await Task.Delay(_latency, ct);

        var polls = _activationPollCount.TryGetValue(request.RequestId, out var n) ? n + 1 : 1;
        _activationPollCount[request.RequestId] = polls;

        // 前两次返回待批准，第三次起批准（模拟考务端批准，含轮询/重发路径）
        var status = polls < 3 ? ActivationStatus.Pending : ActivationStatus.Active;
        var clientNumber = status == ActivationStatus.Active ? "042" : null;

        return Ok(new ActivationRequestResult(
            ActivationRequestId: $"act-{request.RequestId[..8]}",
            DeviceId: request.DeviceId,
            Status: status,
            ClientNumber: clientNumber,
            CredentialVersion: 1,
            RequestedAt: DateTimeOffset.Now));
    }

    public async Task<ApiEnvelope<StudentSessionResult>> LoginAsync(
        LoginStudentRequest request, CancellationToken ct = default)
    {
        await Task.Delay(_latency, ct);

        // 名册核对：准考证号 9999 不在任何名册 → ROSTER_MISMATCH（演示错误路径）
        if (request.AdmissionNo == "9999")
        {
            return Fail<StudentSessionResult>(
                ParticipationErrorCodes.RosterMismatch,
                "姓名、班级或准考证号与名册不符！");
        }

        return Ok(new StudentSessionResult(
            SessionId: $"sess-{Guid.NewGuid().ToString("N")[..12]}",
            DeviceId: request.DeviceId,
            Candidate: new CandidateIdentity(
                CandidateId: $"cand-{request.AdmissionNo}",
                Name: request.Name,
                ClassText: request.ClassText,
                AdmissionNo: request.AdmissionNo),
            ExamIds: new[] { "demo-exam", "demo-late", "demo-package-timeout", "demo-closed" },
            AuthenticatedAt: DateTimeOffset.Now));
    }

    public async Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(
        PrepareExamEntryRequest request, CancellationToken ct = default)
    {
        await Task.Delay(_latency, ct);

        return request.ExamId switch
        {
            "demo-exam" or "demo-late" or "demo-package-timeout" => Ok(new EntryPreparationResult(
                PreparationId: $"prep-{Guid.NewGuid().ToString("N")[..10]}",
                ExamId: request.ExamId,
                Status: EntryPreparationStatus.Ready,
                Resources: new ExamResourceDescriptor(
                    EncryptedPackageUri: $"https://{request.ExamId}.local/synthetic.v1.bin",
                    PackageLength: GetOrBuildPackage(request.ExamId, 1).Package.Length,
                    PaperVersion: 1,
                    InstructionsVersion: 1),
                ParticipationId: null,
                AuthorizedDeviceId: null,
                PendingDeviceId: null,
                EntryVersion: null,
                AuthorizationVersion: null,
                PreparedAt: DateTimeOffset.Now)),

            // 不允许进入 → “当前不可进入该考试！”
            _ => Fail<EntryPreparationResult>(
                ParticipationErrorCodes.EntryNotAllowed,
                "当前不可进入该考试！"),
        };
    }

    public async Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(CancellationToken ct = default)
    {
        await Task.Delay(_latency, ct);
        var now = DateTimeOffset.Now;
        return new[]
        {
            new ExamOverview("demo-exam", "演示考试（合成样例）",
                now.AddMinutes(10), now.AddMinutes(130),
                new[] { "数学", "物理", "化学" }, ExamLifecycle.Scheduled),
            new ExamOverview("demo-late", "已开考进入演示（合成样例）",
                now.AddMinutes(-5), now.AddMinutes(115),
                new[] { "数学" }, ExamLifecycle.Running),
            new ExamOverview("demo-package-timeout", "试题包下载失败模拟（合成样例）",
                now.AddMinutes(30), now.AddMinutes(120),
                new[] { "数学" }, ExamLifecycle.Scheduled),
            new ExamOverview("demo-closed", "已结束考试（合成样例）",
                now.AddHours(-3), now.AddHours(-1),
                new[] { "数学" }, ExamLifecycle.Ended),
        };
    }

    public async Task<InstructionsDocument?> GetInstructionsAsync(
        string sessionId, string examId, long instructionsVersion, CancellationToken ct = default)
    {
        await Task.Delay(_latency, ct);
        var lines = InstructionsFor(examId);
        return lines.Count == 0
            ? null
            : new InstructionsDocument(instructionsVersion, lines);
    }

    public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(
        ExamResourceDescriptor descriptor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var examId = ExtractExamId(descriptor.EncryptedPackageUri);
        var (package, _) = GetOrBuildPackage(examId, descriptor.PaperVersion);

        // 试题包下载失败情景：抛出模拟超时，验证客户端不产生半截文件、不可确认参试
        if (examId == "demo-package-timeout")
        {
            await Task.Delay(_latency, ct);
            yield return package[..64];
            await Task.Delay(_latency, ct);
            throw new IOException("试题包下载超时（合成模拟）。");
        }

        var offset = 0;
        const int chunkSize = 256;
        while (offset < package.Length)
        {
            await Task.Delay(40, ct);
            var size = Math.Min(chunkSize, package.Length - offset);
            yield return package[offset..(offset + size)];
            offset += size;
        }
    }

    public async Task<ApiEnvelope<ParticipationResult>?> ConfirmParticipationAsync(
        ConfirmParticipationRequest request, CancellationToken ct = default)
    {
        await Task.Delay(_latency, ct);

        // 服务器侧校验：包或说明未齐（版本上报不符）→ 不可确认参试
        if (request.DownloadedPaperVersion <= 0 || request.DownloadedInstructionsVersion <= 0)
        {
            return Fail<ParticipationResult>(
                ParticipationErrorCodes.PreparationVersionMismatch,
                "试题包或考试说明未就绪，不能确认参试。");
        }

        // 已结束的考试：下载完成也不能新增参试
        if (request.ExamId == "demo-closed")
        {
            return Fail<ParticipationResult>(
                ParticipationErrorCodes.EntryNotAllowed,
                "考试已结束，不能新增参试。");
        }

        var (package, unlockReference) = GetOrBuildPackage(request.ExamId, request.DownloadedPaperVersion);

        var result = new ParticipationResult(
            ParticipationId: $"part-{Guid.NewGuid().ToString("N")[..10]}",
            ExamId: request.ExamId,
            CandidateId: "cand-mock",
            BusinessState: request.ExamId == "demo-late"
                ? ParticipationBusinessState.Answering
                : ParticipationBusinessState.Waiting,
            AuthorizedDeviceId: "device-mock",
            EntryVersion: 1,
            AuthorizationVersion: 1,
            Version: 1,
            UnlockReference: unlockReference);

        ParticipationConfirmed?.Invoke(result);
        return Ok(result);
    }

    public Task<ApiEnvelope<ParticipationResult>?> WithdrawParticipationAsync(
        WithdrawParticipationRequest request, CancellationToken ct = default)
        => Task.FromResult<ApiEnvelope<ParticipationResult>?>(
            Ok(new ParticipationResult(
                ParticipationId: request.ParticipationId,
                ExamId: string.Empty,
                CandidateId: "cand-mock",
                BusinessState: ParticipationBusinessState.NotParticipating,
                AuthorizedDeviceId: "device-mock",
                EntryVersion: 1,
                AuthorizationVersion: 1,
                Version: request.ExpectedVersion,
                UnlockReference: null)));

    public Task<byte[]?> ResolveUnlockKeyAsync(string unlockReference, CancellationToken ct = default)
    {
        var examId = unlockReference.StartsWith("unlock:", StringComparison.Ordinal)
            ? unlockReference["unlock:".Length..]
            : string.Empty;
        return Task.FromResult(_unlockKeys.TryGetValue($"key:{examId}", out var key) ? key : null);
    }

    private static string ExtractExamId(string uri) =>
        uri.Contains("demo-late", StringComparison.Ordinal) ? "demo-late"
        : uri.Contains("demo-package-timeout", StringComparison.Ordinal) ? "demo-package-timeout"
        : uri.Contains("demo-closed", StringComparison.Ordinal) ? "demo-closed"
        : "demo-exam";

    private static ApiEnvelope<T> Ok<T>(T result) => new(
        ServerId: "mock-server",
        NodeId: "mock-node-1",
        ServerEpoch: 1,
        CorrelationId: RequestIds.New(),
        Result: result,
        Error: null);

    private static ApiEnvelope<T> Fail<T>(string code, string message) => new(
        ServerId: "mock-server",
        NodeId: "mock-node-1",
        ServerEpoch: 1,
        CorrelationId: RequestIds.New(),
        Result: default,
        Error: new ApiError(code, message));
}

/// <summary>
/// Mock 指令源：确认参试后按情景派发开考指令（demo-exam 延迟派发 Start；
/// demo-late 已是答题态无需指令，由确认结果直接驱动解锁）。
/// </summary>
public sealed class MockExamCommandFeed : IExamCommandFeed
{
    private readonly MockStudentApi _api;
    private CancellationTokenSource? _cts;

    public event EventHandler<CommandDelivery>? CommandReceived;

    public MockExamCommandFeed(MockStudentApi api)
    {
        _api = api;
        _api.ParticipationConfirmed += OnParticipationConfirmed;
    }

    public Task StartAsync(string sessionId, string examId) => Task.CompletedTask;

    public Task StopAsync()
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async void OnParticipationConfirmed(ParticipationResult result)
    {
        if (result.BusinessState != ParticipationBusinessState.Waiting)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        try
        {
            // 开考延迟（模拟等待开考 → 收到开始指令）
            await Task.Delay(TimeSpan.FromSeconds(6), _cts.Token);

            CommandReceived?.Invoke(this, new CommandDelivery(
                Server: new ServerNodeEnvelope("mock-server", "mock-node-1", 1),
                EventId: $"evt-{Guid.NewGuid().ToString("N")[..10]}",
                ExamId: result.ExamId,
                ParticipationId: result.ParticipationId,
                EntryVersion: result.EntryVersion,
                EventType: CommandEventType.Start,
                StateVersion: result.Version,
                TargetDeviceId: result.AuthorizedDeviceId,
                TargetAuthorizationVersion: result.AuthorizationVersion,
                ResourceVersion: 1,
                ProtectedUnlockReference: result.UnlockReference,
                Metadata: new Dictionary<string, string>(),
                CreatedAt: DateTimeOffset.Now,
                DeliveryAttempt: 1));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
