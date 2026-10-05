using Exam.Contracts.Administration;
using Exam.Contracts.Common;

namespace Exam.Student.Core;

public interface IStudentPresenceApi
{
    Task<ApiEnvelope<DevicePresenceLease>> OpenPresenceAsync(OpenDevicePresenceRequest request, CancellationToken ct);
    Task<ApiEnvelope<DevicePresenceReceipt>> ReportPresenceAsync(UpdateDevicePresenceRequest request, CancellationToken ct);
}

/// <summary>仅报告界面和连接状态；答案是否保存始终以独立的进度/交卷回执为准。</summary>
public sealed record StudentPresenceState(string? SessionId, string? ExamId, string Page,
    string? CurrentQuestionId = null, long InstalledPaperVersion = 0,
    string DownloadState = "未下载", bool Recovering = false);

/// <summary>每个进程仅开一次运行租约。串行发送状态，旧进程被替换后不能重新抢占租约。</summary>
public sealed class StudentPresenceReporter(IStudentPresenceApi api, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly OpenDevicePresenceRequest _open = new(RequestIds.New(), RequestIds.New());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DevicePresenceLease? _lease;
    private StudentPresenceState? _saved;
    private long _sequence;
    private long _savedAt;
    private bool _replaced;

    public string? Error { get; private set; }

    public async Task ReportAsync(StudentPresenceState state, CancellationToken ct = default, bool disconnect = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_replaced) return;
            if (_lease is null)
            {
                // 回执丢失时使用相同请求重试，不重复增加服务端代次。
                var response = await api.OpenPresenceAsync(_open, ct);
                var lease = HttpStudentApi.RequireResult(response);
                if (lease.ConnectionId != _open.ConnectionId || lease.Generation < 1 || lease.AcceptedAt == default)
                    throw new InvalidOperationException("服务器返回的设备连接回执不匹配。");
                _lease = lease;
            }
            if (!disconnect && state == _saved && _time.GetElapsedTime(_savedAt) < TimeSpan.FromSeconds(60)) return;
            var request = new UpdateDevicePresenceRequest(_lease.ConnectionId, _lease.Generation, checked(++_sequence),
                state.SessionId, state.ExamId, state.Page, state.CurrentQuestionId,
                state.InstalledPaperVersion, state.DownloadState, state.Recovering, disconnect);
            var responseUpdate = await api.ReportPresenceAsync(request, ct);
            if (!responseUpdate.IsSuccess && responseUpdate.Error?.Code == "VERSION_CONFLICT") _replaced = true;
            var receipt = HttpStudentApi.RequireResult(responseUpdate);
            if (receipt.Generation != request.Generation || receipt.Sequence != request.Sequence || receipt.AcceptedAt == default)
                throw new InvalidOperationException("服务器返回的设备状态回执不匹配。");
            _saved = state;
            _savedAt = _time.GetTimestamp();
            Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or InvalidOperationException or
            System.IO.IOException or TimeoutException or OperationCanceledException)
        {
            Error = _replaced ? "本机状态连接已被替换，请联系监考人员核对。已保存答案仍保留。" :
                "设备状态暂未送达考务端，正在重试。请留意答题区的保存提示。";
        }
        finally { _gate.Release(); }
    }
}
