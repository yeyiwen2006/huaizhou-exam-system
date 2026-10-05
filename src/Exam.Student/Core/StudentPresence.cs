using System.IO;
using System.Net.Http;
using Exam.Contracts.Administration;

namespace Exam.Student.Core;

/// <summary>在线状态的页面取值；与服务端 <c>/student/presence</c> 的白名单一致。</summary>
public static class StudentPresencePages
{
    public const string Login = "登录";
    public const string SelectingExam = "选择考试";
    public const string Waiting = "等待考试";
    public const string Answering = "正在答题";
    public const string SubmitConfirmation = "交卷确认";
    public const string Collecting = "回收中";
    public const string SubmitFeedback = "交卷反馈";
    public const string Exited = "已退出";
}

/// <summary>试卷包下载状态取值；与服务端白名单一致。</summary>
public static class StudentPresenceDownloads
{
    public const string NotDownloaded = "未下载";
    public const string Downloading = "下载中";
    public const string Downloaded = "已下载";
    public const string Updating = "更新中";
    public const string Updated = "已更新";
    public const string Failed = "失败";

    /// <summary>把包管理器阶段与修订阶段折算成上报取值；没有包管理器时是「未下载」。</summary>
    public static string From(ExamPackageManager? manager, PaperUpdateCoordinator? updates) =>
        manager is null
            ? NotDownloaded
            : From(manager.CurrentPhase, manager.HasPackage,
                manager.Descriptor?.PaperVersion ?? 0, manager.NewestPaperVersion, updates?.Phase);

    /// <summary>纯折算：修订阶段优先（失败/更新中/已更新），其余按包阶段与是否有包判断。</summary>
    public static string From(ExamPackageManager.Phase? phase, bool hasPackage, long installedVersion,
        long newestVersion, PaperUpdatePhase? updatePhase) => updatePhase switch
    {
        PaperUpdatePhase.Failed => Failed,
        PaperUpdatePhase.Syncing or PaperUpdatePhase.Pending => Updating,
        PaperUpdatePhase.Applied => Updated,
        _ => phase switch
        {
            ExamPackageManager.Phase.Downloading => Downloading,
            ExamPackageManager.Phase.Downloaded or ExamPackageManager.Phase.ConfirmedWaiting or
                ExamPackageManager.Phase.Unlocked or ExamPackageManager.Phase.Ended => Downloaded,
            ExamPackageManager.Phase.Revoked or ExamPackageManager.Phase.Withdrawn => NotDownloaded,
            _ => hasPackage
                ? newestVersion > installedVersion ? Updating : Downloaded
                : NotDownloaded,
        },
    };
}

/// <summary>上报用的当前上下文；会话与考试为空表示尚未登录或尚未准入。</summary>
public readonly record struct StudentPresenceSnapshot(
    string? SessionId, string? ExamId, string? CurrentQuestionId, long InstalledPaperVersion, string DownloadState,
    bool Recovering = false)
{
    public static readonly StudentPresenceSnapshot None =
        new(null, null, null, 0, StudentPresenceDownloads.NotDownloaded);
}

/// <summary>
/// 学生端在线状态上报。本次运行建立一条连接（ConnectionId + Generation），之后用**严格递增**的 Sequence 上报
/// 页面、题号、试卷版本、下载状态与恢复中标记：
/// <list type="bullet">
/// <item>登录、进入退出、开始答题、交卷、裁决与切出**立即上报**；</item>
/// <item>答题改动与下载状态变化按**短窗口合并**（默认 500 毫秒，且不超过需求要求的 2 秒）后上报一次；</item>
/// <item>心跳由既有 60 秒校时循环驱动（同一内容也占用新序号，用于刷新服务端最后通信时间）；</item>
/// <item>网络失败保留同一序号与内容重试（服务端按序号幂等重放）；被业务拒绝（换连接、已退出、序号内容冲突）时
/// 重新建立连接再报一次，且限频，避免反复冲同一个失败。</item>
/// </list>
/// 上报失败不影响本地作答：所有失败只记录 <see cref="LastError"/>。
/// </summary>
public sealed class StudentPresenceReporter : IDisposable
{
    private static readonly TimeSpan ReopenCooldown = TimeSpan.FromSeconds(30);

    private readonly IStudentApi _api;
    private readonly Func<StudentPresenceSnapshot> _snapshot;
    private readonly TimeSpan _mergeWindow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly string _connectionId = Guid.NewGuid().ToString("N");
    private long _generation;
    private long _sequence;
    private UpdateDevicePresenceRequest? _lastAttempt;
    private UpdateDevicePresenceRequest? _lastAccepted;
    private CancellationTokenSource? _merge;
    private CancellationTokenSource? _lifetime = new();
    private DateTimeOffset _lastReopenAt = DateTimeOffset.MinValue;
    private string _page = StudentPresencePages.Login;
    private bool _stopped;
    private Task _pending = Task.CompletedTask;

    public StudentPresenceReporter(IStudentApi api, Func<StudentPresenceSnapshot> snapshot, TimeSpan? mergeWindow = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _mergeWindow = mergeWindow ?? TimeSpan.FromMilliseconds(500);
        // 合并窗口就是最长滞后：超过 2 秒就不满足“正常联网监测滞后不超过 2 秒”。
        if (_mergeWindow <= TimeSpan.Zero || _mergeWindow > TimeSpan.FromSeconds(2))
        {
            throw new ArgumentOutOfRangeException(nameof(mergeWindow));
        }
    }

    /// <summary>本次运行的在线连接是否已建立（退出后为 false，重新登录会再建立）。</summary>
    public bool IsOpen
    {
        get { lock (_state) return _generation > 0; }
    }

    public string Page
    {
        get { lock (_state) return _page; }
    }

    /// <summary>最近一次上报的失败说明；上报成功时为 null。仅用于诊断显示，不影响作答。</summary>
    public string? LastError { get; private set; }

    /// <summary>建立本次运行的在线连接（幂等）。已退出后再调用会重新建立。</summary>
    public async Task<bool> EnsureOpenAsync(CancellationToken ct = default)
    {
        try
        {
            return await EnsureOpenCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            LastError = $"在线状态连接未建立：{error.GetType().Name}。";
            return false;
        }
    }

    /// <summary>立即上报指定页面（登录/进入退出/开始答题/交卷/裁决/切出等）。</summary>
    public Task ReportPageAsync(string page, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);
        lock (_state) _page = page;
        return RunAsync(() => SendAsync(force: true, ct), ct);
    }

    /// <summary>页面代码用的即时版本：不阻塞 UI，可等待 <see cref="Dispose"/> 保证回合收尾。</summary>
    public void ReportPage(string page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);
        lock (_state) _page = page;
        _pending = RunAsync(() => SendAsync(force: true, CancellationToken.None), CancellationToken.None);
    }

    /// <summary>答题改动或下载状态变化：按短窗口合并后上报一次。</summary>
    public void ReportProgress()
    {
        CancellationTokenSource source;
        lock (_state)
        {
            if (_stopped || _merge is not null) return;
            source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
            _merge = source;
        }
        _pending = RunMergedAsync(source);
    }

    /// <summary>心跳：由既有 60 秒校时循环调用；内容未变也占用新序号刷新最后通信时间。</summary>
    public Task HeartbeatAsync(CancellationToken ct = default) => RunAsync(() => SendAsync(force: true, ct), ct);

    /// <summary>切出/退出：上报「已退出」并断开本次运行的连接。</summary>
    public Task ExitAsync(CancellationToken ct = default) => RunAsync(() => ExitCoreAsync(ct), ct);

    public void Dispose()
    {
        CancellationTokenSource? merge;
        lock (_state)
        {
            _stopped = true;
            merge = _merge;
            _merge = null;
        }
        merge?.Cancel();
        _lifetime?.Cancel();
        try
        {
            // 等待已接纳的回合收尾，避免 Dispose 后仍在写状态（与同步服务的约定一致）。
            _pending.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        _lifetime?.Dispose();
        _lifetime = null;
        merge?.Dispose();
        _gate.Dispose();
    }

    private async Task RunMergedAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(_mergeWindow, source.Token).ConfigureAwait(false);
            lock (_state)
            {
                if (!ReferenceEquals(_merge, source)) return;
                _merge = null;
            }
            await SendAsync(force: false, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_state)
            {
                if (ReferenceEquals(_merge, source)) _merge = null;
            }
            source.Dispose();
        }
    }

    private async Task RunAsync(Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            LastError = $"在线状态上报未完成：{error.GetType().Name}。";
        }
    }

    private async Task<bool> EnsureOpenCoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped) _stopped = false;          // 退出后重新登录：需要重新建立连接
            else if (_generation > 0) return true;    // 已建立：幂等返回，不重复 open
            return await OpenLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> OpenLockedAsync(CancellationToken ct)
    {
        try
        {
            var envelope = await _api.OpenPresenceAsync(
                new OpenDevicePresenceRequest(RequestIds.New(), _connectionId), ct).ConfigureAwait(false);
            if (envelope.Error is not null || envelope.Result is null ||
                !StringComparer.Ordinal.Equals(envelope.Result.ConnectionId, _connectionId) || envelope.Result.Generation < 1)
            {
                LastError = "服务器未接受本次运行的在线连接。";
                return false;
            }
            lock (_state)
            {
                _generation = envelope.Result.Generation;
                _sequence = 0;
                _lastAttempt = null;
                _lastAccepted = null;
            }
            return true;
        }
        catch (Exception error) when (IsTransient(error))
        {
            LastError = "无法连接服务器，稍后重试在线状态。";
            return false;
        }
    }

    private async Task SendAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            if (_generation <= 0 && !await OpenLockedAsync(ct).ConfigureAwait(false)) return;
            var request = BuildRequest(force);
            if (request is null) return;
            if (await SendOnceAsync(request, ct).ConfigureAwait(false)) return;
            // 被业务拒绝（换连接 / 已退出 / 同一序号不同内容）或回执不可信：重建连接后立刻补报一次；
            // 冷却期内不重建，避免反复冲同一个失败。
            if (!await ReopenLockedAsync(ct).ConfigureAwait(false)) return;
            var retry = BuildRequest(force: true);
            if (retry is not null) await SendOnceAsync(retry, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>发送一次。返回 false 表示需要重建连接；网络失败返回 true（保留原序号幂等重试）。</summary>
    private async Task<bool> SendOnceAsync(UpdateDevicePresenceRequest request, CancellationToken ct)
    {
        try
        {
            var envelope = await _api.UpdatePresenceAsync(request, ct).ConfigureAwait(false);
            if (envelope.Error is not null || envelope.Result is null)
            {
                LastError = "服务器未接受当前在线状态。";
                return false;
            }
            if (envelope.Result.Sequence != request.Sequence || envelope.Result.Generation != request.Generation)
            {
                LastError = "在线状态回执与本次请求不匹配，未采用该结果。";
                return false;
            }
            lock (_state)
            {
                _sequence = request.Sequence;
                _lastAttempt = request;
                _lastAccepted = request;
            }
            LastError = null;
            return true;
        }
        catch (Exception error) when (IsTransient(error))
        {
            lock (_state)
            {
                // 保留同一序号与内容：服务端按序号幂等重放，下一次触发直接用原请求重试。
                _lastAttempt = request;
            }
            LastError = "网络暂不可用，稍后重试在线状态。";
            return true;
        }
    }

    private UpdateDevicePresenceRequest? BuildRequest(bool force)
    {
        lock (_state)
        {
            var context = _snapshot();
            // 先按页面判断上下文：登录页与已退出不得夹带考生信息（服务端同样拒绝）。
            var anonymous = _page is StudentPresencePages.Login or StudentPresencePages.Exited;
            var content = new UpdateDevicePresenceRequest(_connectionId, _generation, 0,
                anonymous ? null : context.SessionId, anonymous ? null : context.ExamId,
                _page, anonymous ? null : context.CurrentQuestionId,
                anonymous ? 0 : context.InstalledPaperVersion,
                anonymous ? StudentPresenceDownloads.NotDownloaded : context.DownloadState,
                context.Recovering);
            if (!force && SameContent(_lastAccepted, content)) return null;   // 内容已被服务端接受：合并窗口内不再上报
            var sequence = _lastAccepted?.Sequence ?? 0;
            if (_lastAttempt is { } attempt && attempt.Sequence > sequence)
            {
                // 上一次发送尚未确认：内容相同就沿用同序号重试，内容不同才占用下一个序号。
                sequence = SameContent(attempt, content) ? attempt.Sequence : attempt.Sequence + 1;
            }
            else
            {
                sequence += 1;
            }
            return content with { Sequence = sequence };
        }
    }

    /// <summary>重建连接（限频）；返回 false 表示冷却期内没有重建。</summary>
    private async Task<bool> ReopenLockedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_state)
        {
            if (now - _lastReopenAt < ReopenCooldown) return false;
            _lastReopenAt = now;
            _generation = 0;
        }
        return await OpenLockedAsync(ct).ConfigureAwait(false);
    }

    private async Task ExitCoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_generation > 0)
            {
                lock (_state) _page = StudentPresencePages.Exited;
                var request = new UpdateDevicePresenceRequest(_connectionId, _generation, _sequence + 1, null, null,
                    StudentPresencePages.Exited, null, 0, StudentPresenceDownloads.NotDownloaded, false, Disconnect: true);
                try
                {
                    await _api.UpdatePresenceAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception error) when (IsTransient(error))
                {
                    LastError = "退出时的在线状态未送达服务器。";
                }
            }
        }
        finally
        {
            lock (_state)
            {
                _stopped = true;
                _generation = 0;
                _lastAttempt = null;
                _lastAccepted = null;
                _page = StudentPresencePages.Exited;
            }
            _gate.Release();
        }
    }

    private static bool SameContent(UpdateDevicePresenceRequest? left, UpdateDevicePresenceRequest right) =>
        left is { } previous && previous.SessionId == right.SessionId && previous.ExamId == right.ExamId &&
        previous.Page == right.Page && previous.CurrentQuestionId == right.CurrentQuestionId &&
        previous.InstalledPaperVersion == right.InstalledPaperVersion && previous.DownloadState == right.DownloadState &&
        previous.Recovering == right.Recovering && previous.Generation == right.Generation &&
        previous.ConnectionId == right.ConnectionId;

    private static bool IsTransient(Exception error) =>
        error is HttpRequestException or TimeoutException or IOException or OperationCanceledException or TaskCanceledException;
}
