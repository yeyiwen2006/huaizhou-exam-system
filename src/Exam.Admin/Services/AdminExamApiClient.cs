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
    /// 返回前校验完毕：考试身份与科目题组必须完整。
    /// **考生说明允许为空**：服务器可能还没生成（例如学生端说明尚未就绪），那是“还没有服务器副本”，
    /// 由界面如实说明并改显示本地填写内容的预览；不能当成读取失败——否则连设置、时间与可见性都改不了。
    /// </summary>
    internal static void RequireExamDetail(string examId, ExamDetail? detail)
    {
        if (detail is null || !StringComparer.Ordinal.Equals(detail.Summary?.ExamId, examId))
            throw InvalidExamDetail();
        RequireExamDetailStructure(detail);
    }

    /// <summary>新建与保存的回执同样要结构完整（考试身份由调用方按各自回执核对）。</summary>
    internal static void RequireExamDetailStructure(ExamDetail? detail)
    {
        if (detail?.Summary is null || detail.Settings?.Subjects is null) throw InvalidExamDetail();
    }

    private static AdminApiException InvalidExamDetail() => new("INVALID_RESPONSE",
        "考试设置响应无效，未应用该结果。", HttpStatusCode.BadGateway);

    public async Task<ExamDetail> CreateExamAsync(string name, ScheduleWindow schedule, ExamSettings settings,
        CancellationToken cancellationToken = default)
    {
        // 回执也校验结构：否则响应缺字段会在页面接上时抛空引用，而服务器那次写入其实已经提交。
        var detail = await SendAsync<ExamDetail>(HttpMethod.Post, "api/v1/exams",
            new CreateExamRequest(string.Empty, name, schedule, settings), cancellationToken);
        RequireExamDetailStructure(detail);
        return detail;
    }

    public async Task<ExamDetail> UpdateExamSettingsAsync(string examId, long expectedVersion, string name, ExamSettings settings,
        CancellationToken cancellationToken = default)
    {
        var detail = await SendAsync<ExamDetail>(HttpMethod.Put, $"api/v1/exams/{Uri.EscapeDataString(examId)}/settings",
            new UpdateExamSettingsRequest(string.Empty, expectedVersion, name, settings), cancellationToken);
        RequireExamDetailStructure(detail);
        return detail;
    }

    public async Task<ExamSummary> SetExamVisibilityAsync(string examId, bool visible, CancellationToken cancellationToken = default) =>
        await SendAsync<ExamSummary>(HttpMethod.Put, $"api/v1/exams/{Uri.EscapeDataString(examId)}/visibility",
            new SetExamVisibilityRequest(string.Empty, visible), cancellationToken);
}
