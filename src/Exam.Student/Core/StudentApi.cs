using Exam.Contracts.Activation;
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
/// 与服务器 API 封套（ApiEnvelope）同构，B02+ 接入 HTTP 客户端时仅替换实现，页面不变。
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
}

public static class RequestIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Mock 实现：固定延迟 + 确定性规则，覆盖 B01 页面需要的成功与错误路径。
/// 数据均为独立合成样例，不含任何真实试卷；联调后由真实 HTTP 客户端替换。
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
    private readonly Dictionary<string, string> _admissionByExam = new()
    {
        ["demo-exam"] = "2026001",
        ["demo-package-timeout"] = "2026001",
        ["demo-closed"] = "2026001",
    };

    public static IReadOnlyList<string> InstructionsFor(string examId) =>
        examId == "demo-exam" ? SampleInstructionLines : Array.Empty<string>();

    public async Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(
        SubmitActivationRequest request, CancellationToken ct = default)
    {
        await Task.Delay(900, ct);

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
        await Task.Delay(700, ct);

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
            ExamIds: new[] { "demo-exam", "demo-package-timeout", "demo-closed" },
            AuthenticatedAt: DateTimeOffset.Now));
    }

    public async Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(
        PrepareExamEntryRequest request, CancellationToken ct = default)
    {
        await Task.Delay(900, ct);

        return request.ExamId switch
        {
            "demo-exam" => Ok(new EntryPreparationResult(
                PreparationId: $"prep-{Guid.NewGuid().ToString("N")[..10]}",
                ExamId: request.ExamId,
                Status: EntryPreparationStatus.Ready,
                Resources: new ExamResourceDescriptor(
                    EncryptedPackageUri: $"https://{request.SessionId[..8]}.local/synthetic-minimal.v1.bin",
                    PackageLength: 2048,
                    PaperVersion: 1,
                    InstructionsVersion: 1),
                ParticipationId: null,
                AuthorizedDeviceId: null,
                PendingDeviceId: null,
                EntryVersion: null,
                AuthorizationVersion: null,
                PreparedAt: DateTimeOffset.Now)),

            // 同意进入但试题包超时 → “试题获取失败！”
            "demo-package-timeout" => Fail<EntryPreparationResult>(
                ParticipationErrorCodes.ResourcesNotReady,
                "试题获取失败！"),

            // 不允许进入 → “当前不可进入该考试！”
            _ => Fail<EntryPreparationResult>(
                ParticipationErrorCodes.EntryNotAllowed,
                "当前不可进入该考试！"),
        };
    }

    public async Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(CancellationToken ct = default)
    {
        await Task.Delay(500, ct);
        var now = DateTimeOffset.Now;
        return new[]
        {
            new ExamOverview("demo-exam", "演示考试（合成样例）",
                now.AddMinutes(10), now.AddMinutes(130),
                new[] { "数学", "物理", "化学" }, ExamLifecycle.Scheduled),
            new ExamOverview("demo-package-timeout", "试题包超时模拟（合成样例）",
                now.AddMinutes(30), now.AddMinutes(120),
                new[] { "数学" }, ExamLifecycle.Scheduled),
            new ExamOverview("demo-closed", "已结束考试（合成样例）",
                now.AddHours(-3), now.AddHours(-1),
                new[] { "数学" }, ExamLifecycle.Ended),
        };
    }

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
