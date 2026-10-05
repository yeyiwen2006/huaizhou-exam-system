using System.Net;
using System.Net.Http;
using Exam.Contracts.Exams;
using Exam.Contracts.Versioning;

namespace Exam.Admin.Services;

// 考试目录：主页卡片、新建考试、考试设置与学生端显示开关。请求标识由 SendAsync 统一生成并在重试时沿用。
public sealed partial class AdminApiClient
{
    public async Task<IReadOnlyList<ExamSummary>> ListExamsAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<ExamSummary[]>(HttpMethod.Get, "api/v1/exams", null, cancellationToken);

    public async Task<ExamDetail> GetExamDetailAsync(string examId, CancellationToken cancellationToken = default)
    {
        var detail = await SendAsync<ExamDetail>(HttpMethod.Get,
            $"api/v1/exams/{Uri.EscapeDataString(examId)}", null, cancellationToken);
        RequireExamDetail(examId, detail);
        return detail;
    }

    /// <summary>
    /// 返回前校验完毕：考试身份、科目题组与服务器生成的考生说明必须完整，
    /// 否则界面会把“缺少说明”误显示成“尚未保存到服务器”。
    /// </summary>
    internal static void RequireExamDetail(string examId, ExamDetail? detail)
    {
        if (detail is null || !StringComparer.Ordinal.Equals(detail.Summary?.ExamId, examId) ||
            detail.Settings?.Subjects is null || detail.InstructionLines is null ||
            detail.InstructionLines.Count == 0 || detail.InstructionLines.Any(string.IsNullOrWhiteSpace))
            throw new AdminApiException("INVALID_RESPONSE",
                "考试设置响应不属于当前考试或缺少考生说明，未应用该结果。", HttpStatusCode.BadGateway);
    }

    public async Task<ExamDetail> CreateExamAsync(string name, ScheduleWindow schedule, ExamSettings settings,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ExamDetail>(HttpMethod.Post, "api/v1/exams",
            new CreateExamRequest(string.Empty, name, schedule, settings), cancellationToken);

    public async Task<ExamDetail> UpdateExamSettingsAsync(string examId, long expectedVersion, string name, ExamSettings settings,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ExamDetail>(HttpMethod.Put, $"api/v1/exams/{Uri.EscapeDataString(examId)}/settings",
            new UpdateExamSettingsRequest(string.Empty, expectedVersion, name, settings), cancellationToken);

    public async Task<ExamSummary> SetExamVisibilityAsync(string examId, bool visible, CancellationToken cancellationToken = default) =>
        await SendAsync<ExamSummary>(HttpMethod.Put, $"api/v1/exams/{Uri.EscapeDataString(examId)}/visibility",
            new SetExamVisibilityRequest(string.Empty, visible), cancellationToken);
}
