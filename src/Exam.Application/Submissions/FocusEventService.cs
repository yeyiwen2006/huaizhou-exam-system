using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;

namespace Exam.Application.Submissions;

/// <summary>
/// 失焦事件上报边界：先验证请求与授权元组，再交由事务存储按 (ParticipationId, EventId) 幂等落库。
/// 校验规则与进度同步共用同一授权语义；已交卷后的补报不拒绝。
/// </summary>
public sealed class FocusEventService(
    IFocusEventStore store,
    TimeProvider? timeProvider = null)
{
    private readonly IFocusEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async ValueTask<SubmissionOperationResult<FocusEventsReceipt>> ReportAsync(
        ReportFocusEventsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return SubmissionOperationResult<FocusEventsReceipt>.Failure(
                SubmissionErrorCode.InvalidRequest,
                validationError);
        }

        try
        {
            var result = await _store.SaveAsync(request, _time.GetUtcNow(), cancellationToken);
            return result.Status switch
            {
                FocusEventPersistenceStatus.Saved => SubmissionOperationResult<FocusEventsReceipt>.Success(
                    result.Receipt!,
                    message: "失焦事件已由服务器留痕。"),
                FocusEventPersistenceStatus.AuthorizationRevoked =>
                    SubmissionOperationResult<FocusEventsReceipt>.Failure(
                        SubmissionErrorCode.AuthorizationRevoked,
                        "设备授权或参加考试版本已失效，失焦事件未留痕。"),
                _ => SubmissionOperationResult<FocusEventsReceipt>.Failure(
                    SubmissionErrorCode.NotFound,
                    "未找到参加考试记录。")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SubmissionOperationResult<FocusEventsReceipt>.Failure(
                SubmissionErrorCode.PersistenceFailed,
                "失焦事件未能持久保存，请保留原记录并稍后重试。");
        }
    }

    public ValueTask<SubmissionOperationResult<IReadOnlyList<FocusLossEventSummary>>> ListSummariesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(examId))
        {
            return ValueTask.FromResult(
                SubmissionOperationResult<IReadOnlyList<FocusLossEventSummary>>.Failure(
                    SubmissionErrorCode.InvalidRequest,
                    "考试标识不能为空。"));
        }

        return ListSummariesCoreAsync(examId, cancellationToken);
    }

    private async ValueTask<SubmissionOperationResult<IReadOnlyList<FocusLossEventSummary>>> ListSummariesCoreAsync(
        string examId,
        CancellationToken cancellationToken)
    {
        try
        {
            var summaries = await _store.ListSummariesAsync(examId, cancellationToken);
            return SubmissionOperationResult<IReadOnlyList<FocusLossEventSummary>>.Success(summaries);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SubmissionOperationResult<IReadOnlyList<FocusLossEventSummary>>.Failure(
                SubmissionErrorCode.PersistenceFailed,
                "失焦监测查询失败，请保留原记录并稍后重试。");
        }
    }

    private static string? Validate(ReportFocusEventsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.ExamId) ||
            string.IsNullOrWhiteSpace(request.CandidateId) ||
            string.IsNullOrWhiteSpace(request.ParticipationId) ||
            string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return "考试、考生、参加考试、设备或请求标识不能为空。";
        }

        if (request.EntryVersion < 1 || request.AuthorizationVersion < 1 || request.PaperVersion < 1)
        {
            return "参加考试或试卷版本无效。";
        }

        if (request.Events is null || request.Events.Count == 0)
        {
            return "失焦事件列表不能为空。";
        }

        if (request.Events.Count > 200)
        {
            return "单次上报的失焦事件过多，请分批补报。";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request.Events)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.EventId) || !seen.Add(item.EventId))
            {
                return "失焦事件标识为空或重复。";
            }

            if (item.DurationMilliseconds < 0 || item.LostAt > item.RegainedAt)
            {
                return "失焦事件的时间或时长无效。";
            }
        }

        return null;
    }
}

/// <summary>
/// 失焦留痕的线程安全内存实现：模拟生产适配器的唯一键与授权检查，供测试与内存运行时使用。
/// </summary>
public sealed class InMemoryFocusEventStore : IFocusEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SubmissionAuthorizationState> _authorizations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string ParticipationId, string EventId), ReportedFocusLossEvent> _events =
        new();

    public ValueTask SetAuthorizationAsync(
        SubmissionAuthorizationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _authorizations[state.ParticipationId] = state;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<FocusEventPersistenceResult> SaveAsync(
        ReportFocusEventsRequest request,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_authorizations.TryGetValue(request.ParticipationId, out var authorization))
            {
                return ValueTask.FromResult(new FocusEventPersistenceResult(
                    FocusEventPersistenceStatus.ParticipationNotFound, null));
            }

            if (!IsAuthorized(request, authorization))
            {
                return ValueTask.FromResult(new FocusEventPersistenceResult(
                    FocusEventPersistenceStatus.AuthorizationRevoked, null));
            }

            // 起始记录可补齐一次时长；已完成事件与乱序起始重试不会覆盖最终时长。
            foreach (var item in request.Events)
            {
                var key = (request.ParticipationId, item.EventId);
                if (!_events.TryGetValue(key, out var previous) ||
                    (previous.DurationMilliseconds == 0 && previous.LostAt == item.LostAt && item.DurationMilliseconds > 0))
                    _events[key] = item;
            }

            var receipt = new FocusEventsReceipt(
                request.ExamId,
                request.CandidateId,
                request.ParticipationId,
                request.PaperVersion,
                request.Events.Select(item => item.EventId).ToArray().AsReadOnly(),
                persistedAt);
            return ValueTask.FromResult(new FocusEventPersistenceResult(
                FocusEventPersistenceStatus.Saved, receipt));
        }
    }

    public ValueTask<IReadOnlyList<FocusLossEventSummary>> ListSummariesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var summaries = _events
                .GroupBy(pair => pair.Key.ParticipationId, StringComparer.Ordinal)
                .Where(group => _authorizations.TryGetValue(group.Key, out var authorization) &&
                    string.Equals(authorization.ExamId, examId, StringComparison.Ordinal))
                .Select(group =>
                {
                    var authorization = _authorizations[group.Key];
                    var items = group.Select(pair => pair.Value).ToArray();
                    return new FocusLossEventSummary(
                        examId,
                        authorization.CandidateId,
                        group.Key,
                        items.Length,
                        items.Sum(item => item.DurationMilliseconds),
                        items.Max(item => item.LostAt));
                })
                .OrderBy(item => item.CandidateId, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<FocusLossEventSummary>>(summaries);
        }
    }

    private static bool IsAuthorized(ReportFocusEventsRequest request, SubmissionAuthorizationState authorization) =>
        string.Equals(request.ExamId, authorization.ExamId, StringComparison.Ordinal) &&
        string.Equals(request.CandidateId, authorization.CandidateId, StringComparison.Ordinal) &&
        request.EntryVersion == authorization.EntryVersion &&
        string.Equals(request.DeviceId, authorization.AuthorizedDeviceId, StringComparison.Ordinal) &&
        request.AuthorizationVersion == authorization.AuthorizationVersion;
}
