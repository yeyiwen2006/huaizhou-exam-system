using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Resources;

namespace Exam.Student.Core;

/// <summary>一场考试的包与指令连接，在等待页转到答题页时继续保留。</summary>
public sealed class StudentExamRuntime(
    ExamPackageManager manager,
    DeviceRecord device,
    ILocalStateStore? localState = null,
    string? candidateId = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _answerGate = new(1, 1);
    private readonly LocalStateKey? _localKey = localState is null ? null : new(
        device.ServerId ?? throw new ArgumentException("本地答题记录必须绑定可信逻辑服务器。", nameof(device)),
        manager.ExamId,
        !string.IsNullOrWhiteSpace(candidateId) && candidateId == manager.CandidateId ? candidateId :
            throw new ArgumentException("本地答题记录必须使用当前考试的考生标识。", nameof(candidateId)));
    private IExamCommandFeed? _feed;
    private ParticipationResult? _connectedParticipation;
    private ServerNodeEnvelope? _connectedServer;
    private bool _disposed;
    private string? _handlingError;
    private AnswerSessionContext? _answerContext;
    private FixedSubmissionRecord? _recoveredSubmission;
    private bool _localRecoveryChecked;
    private string? _localStateError;
    private StudentPaperManifest? _paper;

    public ExamPackageManager Manager { get; } = manager;
    public AnswerSession? Answers { get; private set; }
    public StudentPaperManifest? Paper => !_disposed && !Manager.ExamEnded &&
        Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked ? _paper : null;

    /// <summary>B03 显示层读取 PDF 的入口。调用方负责释放显示副本；终止后禁止再次读取。</summary>
    public byte[] ReadQuestionPdf(string questionId)
    {
        var paper = Paper ?? throw new InvalidOperationException("试题尚未解锁或已经停止访问。");
        var payload = Manager.UnlockedPayload ?? throw new InvalidOperationException("试题已停止访问。");
        var bytes = StudentPaperPackage.ReadQuestionPdf(payload, paper, questionId);
        if (Paper != paper || !ReferenceEquals(payload, Manager.UnlockedPayload))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException("读取期间试题已停止访问。");
        }
        return bytes;
    }
    public bool LocalRecoveryChecked => _localRecoveryChecked;
    public bool HasFixedSubmission => _recoveredSubmission is not null || Answers?.Status == AnswerSessionStatus.Fixed ||
        Answers?.HasPendingSubmission == true;
    public string? LocalStateError => _localStateError ?? Answers?.LastError;
    public bool CanRetryLocalSave => localState is not null && Answers?.Status != AnswerSessionStatus.PermissionDenied &&
        (!_localRecoveryChecked || Answers is { HasUnsavedChanges: true } ||
         Answers?.Status is AnswerSessionStatus.WriteBlocked or AnswerSessionStatus.FixPending);
    public string LocalStateSummary => Answers?.HasPendingSubmission == true
        ? "答卷已经冻结，但固定内容尚未确认落盘；原答案和提交标识在内存中保留，请重试本地保存。"
        : HasFixedSubmission
        ? "本机已有固定答卷，原答案和提交标识已保留；尚不能据此视为服务器交卷成功。"
        : Answers?.Status switch
        {
            AnswerSessionStatus.Active => $"本地保存已就绪，可靠版本 {Answers.AnswerVersion}；已保存 {Answers.Answers.Count} 题记录、{Answers.Marks.Count} 个私人标记。",
            AnswerSessionStatus.WriteBlocked => "本地保存失败，已阻止新增修改。最后可靠状态和内存待保存内容均保留，请恢复写入后重试。",
            AnswerSessionStatus.Suspended => "答题已停止，原答案和私人标记保留；尚未取得服务器交卷成功回执。",
            AnswerSessionStatus.PermissionDenied => "本地记录或当前授权不允许继续作答，原文件已保留，请联系考务人员。",
            _ when _localRecoveryChecked => "本地恢复检查已完成，等待本次服务器授权和解锁后恢复答案。",
            _ => "本地答题恢复尚未完成，不能开始修改答案。"
        };
    public string? ConnectionError => (_feed as SignalRExamCommandFeed)?.LastError ?? _handlingError;
    public bool NotificationsStarted => _feed is not null;
    public event EventHandler? StateChanged;

    /// <summary>Read existing fixed work before any new entry/unlock; this never authorizes editing.</summary>
    public async Task<bool> InitializeLocalRecoveryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _answerGate.WaitAsync(ct);
        try { return await InitializeLocalRecoveryCoreAsync(ct); }
        finally { _answerGate.Release(); NotifyStateChanged(); }
    }

    private async Task<bool> InitializeLocalRecoveryCoreAsync(CancellationToken ct)
    {
        if (_localRecoveryChecked) { return true; }
        if (localState is null || _localKey is null)
        {
            _localStateError = "本地答题存储尚未配置，不能开始作答。";
            return false;
        }
        try
        {
            _recoveredSubmission = await localState.LoadFixedSubmissionAsync(_localKey, ct);
            if (_recoveredSubmission is null && Manager.RequiresLocalAnswerRecovery &&
                await localState.LoadAnswerStateAsync(_localKey, ct) is null)
            {
                _localStateError = "本机保留了上次已经开考的参试记录，但原本地答案缺失。不能重建空答卷或重新开始，请联系考务人员恢复原记录。";
                return false;
            }
            _localRecoveryChecked = true;
            _localStateError = null;
            return true;
        }
        catch (Exception error) when (IsLocalStorageError(error))
        {
            _localStateError = "本地恢复记录无法安全读取，原文件已保留。请修复本机存储后重试，不能清空记录重新开始。";
            return false;
        }
    }

    /// <summary>B02's live unlock is the only runtime entry to an editable B04 session.</summary>
    public async Task SynchronizeLocalAnswersAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _answerGate.WaitAsync(ct);
        try
        {
            if (!await InitializeLocalRecoveryCoreAsync(ct) || _disposed) { return; }
            if (Manager.CurrentPhase == ExamPackageManager.Phase.Revoked)
            {
                _paper = null;
                if (Answers is { } revoked) { await revoked.InvalidateAuthorizationAsync(ct); }
                return;
            }
            if (Manager.ExamEnded)
            {
                _paper = null;
                if (Answers is { } ended) { await ended.SuspendAsync("考试已结束，答案已停止修改，原记录等待后续核对和补传。", ct); }
                return;
            }
            if (HasFixedSubmission) { return; }
            if (Manager.CurrentPhase != ExamPackageManager.Phase.Unlocked || Manager.UnlockedPayload is null ||
                Manager.Participation is not { BusinessState: ParticipationBusinessState.Answering } participation ||
                participation.Lifecycle == ExamLifecycle.Ended || Manager.Descriptor is not { } descriptor ||
                Manager.Server is not { } server || _localKey is null ||
                server.ServerId != _localKey.ServerKey || server.ServerEpoch < Math.Max(1, device.ServerEpoch) ||
                string.IsNullOrWhiteSpace(server.NodeId) || participation.CandidateId != _localKey.CandidateId ||
                participation.ExamId != _localKey.ExamId || participation.AuthorizedDeviceId != device.DeviceId)
            {
                return;
            }

            if (_paper is null || _paper.PaperVersion != descriptor.PaperVersion)
            {
                try { _paper = StudentPaperPackage.ReadManifest(Manager.UnlockedPayload!, Manager.ExamId, descriptor.PaperVersion); }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
                {
                    _paper = null;
                    _localStateError = "试题包清单、PDF 或版本核对失败，原答案保留，不能开始作答。";
                    if (Answers is { } previous) await previous.SuspendAsync(_localStateError, ct);
                    return;
                }
            }
            var context = new AnswerSessionContext(participation.ParticipationId, participation.EntryVersion,
                participation.AuthorizationVersion, descriptor.PaperVersion, device.DeviceId, IsServerAuthorized: true);
            if (Answers is not null)
            {
                if (_answerContext != context)
                {
                    await Answers.InvalidateAuthorizationAsync(ct);
                    _localStateError = "参试、授权或试卷版本已变化，原答案未被覆盖；请重新核对恢复记录。";
                }
                return;
            }

            var answers = new AnswerSession(localState!, paper: _paper.Structure);
            Answers = answers;
            _answerContext = context;
            answers.WriteBlocked += OnAnswerWriteBlocked;
            answers.SubmissionFixed += OnSubmissionFixed;
            var restored = await answers.StartAsync(_localKey, context, ct);
            _recoveredSubmission ??= answers.FixedSubmission;
            _localStateError = restored.IsUsable ? null : restored.Message;
            // END/revocation can arrive while SQLite recovery awaits its worker. Never let
            // a completed load reactivate a runtime that has already entered a terminal state.
            if (Manager.CurrentPhase == ExamPackageManager.Phase.Revoked)
                await answers.InvalidateAuthorizationAsync(ct);
            else if (_disposed || Manager.ExamEnded)
                await answers.SuspendAsync("考试已结束，原答案保留，不能新增修改。", ct);
        }
        catch (Exception error) when (IsLocalStorageError(error))
        {
            _localStateError = "本地答案恢复尚未完成，原记录已保留。请修复本机存储后重试。";
            if (Answers is { } answers) { await answers.SuspendAsync(_localStateError, CancellationToken.None); }
        }
        finally { _answerGate.Release(); NotifyStateChanged(); }
    }

    public async Task RetryLocalSaveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_localRecoveryChecked)
        {
            await InitializeLocalRecoveryAsync(ct);
            return;
        }
        if (Answers is not { } answers) { return; }
        var result = await answers.RetrySaveAsync(ct);
        _localStateError = result.Succeeded ? null : result.Error;
        NotifyStateChanged();
    }

    private void OnAnswerWriteBlocked(string message)
    {
        _localStateError = message;
        NotifyStateChanged();
    }

    private void OnSubmissionFixed(FixedSubmissionRecord record)
    {
        _recoveredSubmission = record;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (_disposed) { return; }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { StateChanged?.Invoke(this, EventArgs.Empty); }
        else if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() => { if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty); }));
    }

    private static bool IsLocalStorageError(Exception error) => error is IOException or
        UnauthorizedAccessException or System.Data.Common.DbException or System.Text.Json.JsonException or
        CryptographicException or InvalidOperationException or ArgumentException;

    public async Task StartNotificationsAsync(ParticipationResult participation, ServerNodeEnvelope server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_feed is SignalRExamCommandFeed { IsConnected: true } &&
            _connectedServer == server && _connectedParticipation is { } previous &&
            previous.ExamId == participation.ExamId && previous.CandidateId == participation.CandidateId &&
            previous.ParticipationId == participation.ParticipationId && previous.EntryVersion == participation.EntryVersion &&
            previous.AuthorizedDeviceId == participation.AuthorizedDeviceId && previous.AuthorizationVersion == participation.AuthorizationVersion)
        {
            return;
        }

        if (_feed is not null)
        {
            await _feed.DisposeAsync();
            _feed = null;
        }

        var feed = new SignalRExamCommandFeed(device, server)
        {
            AuthorizationLost = InvalidateAuthorizationAsync,
        };
        try
        {
            await feed.StartAsync(participation, ReceiveCommandAsync, _lifetime.Token);
            _feed = feed;
            _connectedParticipation = participation;
            _connectedServer = server;
            _handlingError = null;
        }
        catch
        {
            await feed.DisposeAsync();
            _handlingError = "指令连接尚未建立，请重试连接；原参试登记保持不变。";
            throw;
        }
    }

    private Task ReceiveCommandAsync(CommandDelivery delivery, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("学生端界面尚未启动。");
        return dispatcher.CheckAccess()
            ? ApplyCommandAsync(delivery, ct)
            : dispatcher.InvokeAsync(() => ApplyCommandAsync(delivery, ct), DispatcherPriority.Normal, ct).Task.Unwrap();
    }

    private Task InvalidateAuthorizationAsync(CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("学生端界面尚未启动。");
        // 连接自己的取消信号可能正是由撤销触发，不能因此跳过本地停用。
        return dispatcher.CheckAccess()
            ? InvalidateAuthorizationOnUiAsync()
            : dispatcher.InvokeAsync(InvalidateAuthorizationOnUiAsync, DispatcherPriority.Send).Task.Unwrap();
    }

    private async Task InvalidateAuthorizationOnUiAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Both calls stop new work synchronously at their entry. Start both before
            // awaiting either persistence gate so a slow package operation cannot leave answers editable.
            var packageRevocation = Manager.InvalidateAuthorizationAsync(_lifetime.Token);
            var answerRevocation = Answers?.InvalidateAuthorizationAsync(CancellationToken.None) ?? Task.CompletedTask;
            NotifyStateChanged();
            await Task.WhenAll(packageRevocation, answerRevocation);
            _handlingError = "当前设备的答题授权已失效，已停止访问试题。请联系考务人员核对授权。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
        {
            _handlingError = "当前设备授权已失效，已停止访问试题；本机停用记录保存失败，请联系考务人员恢复。";
        }

        // 此回调仍属于指令连接，不在其中 Dispose/Stop 该连接，避免等待自身结束。
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyCommandAsync(CommandDelivery delivery, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var packageCommand = Manager.HandleCommandAsync(delivery, ct);
            // The manager validates END's full identity before synchronously marking Ended.
            // Merely receiving an END-shaped message is not sufficient to suspend this exam.
            var answerStop = Manager.ExamEnded && Answers is { } answers
                ? answers.SuspendAsync("考试已结束，答案已停止修改；固定提交与回执核对尚未完成。", CancellationToken.None)
                : Task.CompletedTask;
            NotifyStateChanged();
            await Task.WhenAll(packageCommand, answerStop);
            await SynchronizeLocalAnswersAsync(ct);
            _handlingError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TimeoutException
            or OperationCanceledException or CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            _handlingError = "服务器指令尚未处理完成，将继续使用原事件重试。请保持客户端运行。";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _paper = null;
        var stopAnswers = Answers?.SuspendAsync("考试会话已关闭，原记录保留。", CancellationToken.None) ?? Task.CompletedTask;
        _lifetime.Cancel();
        try
        {
            if (_feed is not null)
            {
                await _feed.DisposeAsync();
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            // 服务端已确认退出后的本地连接清理不应重新阻止退出；持久登记由管理器处理。
        }
        finally
        {
            try
            {
                await stopAnswers;
                await _answerGate.WaitAsync();
                try
                {
                    if (Answers is { } answers)
                    {
                        // A recovery may have been awaiting SQLite when disposal started.
                        await answers.SuspendAsync("考试会话已关闭，原记录保留。", CancellationToken.None);
                        answers.WriteBlocked -= OnAnswerWriteBlocked;
                        answers.SubmissionFixed -= OnSubmissionFixed;
                    }
                }
                finally { _answerGate.Release(); }
            }
            finally
            {
                _feed = null;
                _connectedParticipation = null;
                _connectedServer = null;
                Manager.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
