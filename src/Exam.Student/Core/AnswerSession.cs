using System.IO;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.Student.Core;

public enum AnswerSessionStatus
{
    NotStarted,
    Active,
    WriteBlocked,
    Fixed,
    PermissionDenied,
    /// <summary>已立即冻结，但完整固定答卷尚未确认落盘。</summary>
    FixPending,
    /// <summary>已停止新增作答，保留待恢复的本地内容，不等于已交卷。</summary>
    Suspended,
}

/// <summary>必须由当前服务器授权结果构造；本地恢复记录不能自行提供授权。</summary>
public sealed record AnswerSessionContext(
    string ParticipationId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion,
    string? DeviceId = null,
    bool IsServerAuthorized = false);

public sealed record RestoreResult(AnswerSessionStatus Status, string? Message)
{
    public bool IsUsable => Status is AnswerSessionStatus.Active or AnswerSessionStatus.Fixed;
}

public sealed record AnswerChangeResult(bool Succeeded, long AnswerVersion, string? Error)
{
    public static AnswerChangeResult Ok(long version) => new(true, version, null);
    public static AnswerChangeResult Fail(string error) => new(false, 0, error);
}

public sealed record FixSubmissionResult(
    bool Succeeded, bool IsReplay, FixedSubmissionRecord? Record, string? Error);

/// <summary>
/// 串行保存答案、标记和导航。可靠状态与待保存修改分开保存；固定失败时仍使用第一次的提交快照。
/// 存储负责在工作线程执行 SQLite 事务，本类不读取本机墙钟来决定考试状态。
/// </summary>
public sealed class AnswerSession
{
    private readonly ILocalStateStore _store;
    private readonly TimeProvider _time;
    private readonly PaperStructure? _paper;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalStateKey? _key;
    private AnswerSessionContext? _context;
    private AnswerStateRecord? _reliable;
    private PendingWrite? _pendingWrite;
    private FixedSubmissionRecord? _frozenIntent;
    private FixedSubmissionRecord? _fixed;
    private string? _requestedSubmissionId;
    private AnswerSessionStatus _status = AnswerSessionStatus.NotStarted;
    private bool _loadFailed;
    private int _authorizationInvalid;
    private int _freezeRequested;
    private int _suspendRequested;

    private sealed record PendingWrite(AnswerStateRecord State, PendingSnapshot Snapshot);

    public AnswerSession(ILocalStateStore store, TimeProvider? timeProvider = null, PaperStructure? paper = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
        _paper = paper is null ? null : new(Array.AsReadOnly(paper.Questions.Select(q =>
            q with { OptionIds = Array.AsReadOnly(q.OptionIds.ToArray()) }).ToArray()));
    }

    public AnswerSessionStatus Status =>
        Volatile.Read(ref _authorizationInvalid) != 0 ? AnswerSessionStatus.PermissionDenied :
        _fixed is not null ? AnswerSessionStatus.Fixed :
        Volatile.Read(ref _freezeRequested) != 0 ? AnswerSessionStatus.FixPending :
        _status == AnswerSessionStatus.Active && Volatile.Read(ref _suspendRequested) != 0 ? AnswerSessionStatus.Suspended :
        _status;

    public long AnswerVersion => _reliable?.AnswerVersion ?? 0;
    public string? CurrentQuestionId => _reliable?.CurrentQuestionId;
    public string? FixedSubmissionId => _fixed?.SubmissionId ?? _frozenIntent?.SubmissionId ?? _requestedSubmissionId;
    public string? LastError { get; private set; }
    public bool HasUnsavedChanges => _pendingWrite is not null;
    public bool HasPendingSubmission => Volatile.Read(ref _freezeRequested) != 0 && _fixed is null;
    public IReadOnlyDictionary<string, AnswerValue> Answers => FreezeAnswers(_reliable?.Answers ?? EmptyAnswers());
    public IReadOnlySet<string> Marks => new HashSet<string>(_reliable?.Marks ?? new HashSet<string>(), StringComparer.Ordinal);
    public IReadOnlyDictionary<string, AnswerValue>? PendingAnswers =>
        _pendingWrite is { } pending ? FreezeAnswers(pending.State.Answers) : null;
    public IReadOnlySet<string>? PendingMarks =>
        _pendingWrite is { } pending ? new HashSet<string>(pending.State.Marks, StringComparer.Ordinal) : null;
    public string? PendingCurrentQuestionId => _pendingWrite?.State.CurrentQuestionId;
    public FixedSubmissionRecord? FixedSubmission => _fixed is { } saved ? FreezeSubmission(saved) : null;
    public event Action<string>? WriteBlocked;
    public event Action? StateSaved;
    public event Action<FixedSubmissionRecord>? SubmissionFixed;

    public Task<RestoreResult> StartAsync(LocalStateKey key, AnswerSessionContext context, CancellationToken ct = default) =>
        SerializedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(context);
            if (_key is not null)
            {
                if (_key != key || _context != context)
                    return new RestoreResult(AnswerSessionStatus.PermissionDenied, "不能复用原答题会话切换考生、设备或授权，请新建会话并核对服务器。");
                return new RestoreResult(Status, LastError ?? "原会话继续保留，不重新读取或覆盖待保存修改。");
            }
            _key = key;
            _context = context;
            if (!ValidKey(key) || !context.IsServerAuthorized || string.IsNullOrWhiteSpace(context.DeviceId) ||
                string.IsNullOrWhiteSpace(context.ParticipationId) || context.EntryVersion < 1 ||
                context.AuthorizationVersion < 1 || context.PaperVersion < 1)
                return DenyRestore("尚未取得服务器当前有效授权，不能从本地缓存启动作答。");

            AnswerStateRecord? saved;
            FixedSubmissionRecord? fixedRecord;
            try
            {
                // 固定答卷优先检查。存储层同时验证其与答案状态的一致性，绝不创建空记录绕过恢复。
                fixedRecord = await _store.LoadFixedSubmissionAsync(key, ct).ConfigureAwait(false);
                saved = await _store.LoadAnswerStateAsync(key, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _loadFailed = true;
                BlockWrites("本地恢复记录读取失败，原数据已保留。请修复存储后重新启动，不能创建空状态覆盖。");
                return new RestoreResult(Status, LastError);
            }
            if (saved is not null && !MatchesContext(saved))
                return DenyRestore("本地记录的设备或参试授权与当前服务器不一致，已拒绝恢复。");
            if (saved is not null && _paper is not null &&
                (AnswerSheetContract.Validate(_paper, saved.Answers) is not null ||
                 saved.Marks.Any(id => !KnownQuestion(id)) ||
                 (saved.CurrentQuestionId is { } current && !KnownQuestion(current))))
                return DenyRestore("本地答卷与试题清单不一致，原记录已保留；不能把缺失题目补成空答案。");
            if (fixedRecord is not null)
            {
                if (saved is null || !ValidFixedRecord(fixedRecord, saved))
                    return DenyRestore("固定答卷的身份、版本或答案记录不一致，原文件已保留，请联系考务人员。");
                _reliable = FreezeState(saved);
                _fixed = FreezeSubmission(fixedRecord);
                _frozenIntent = _fixed;
                _requestedSubmissionId = _fixed.SubmissionId;
                Interlocked.Exchange(ref _freezeRequested, 1);
                _status = AnswerSessionStatus.Fixed;
                return new RestoreResult(Status, "已恢复原固定答卷，尚不代表服务器已成功收卷。");
            }
            if (saved is not null)
            {
                _reliable = FreezeState(saved);
                if (saved.PaperVersion == context.PaperVersion)
                {
                    _status = AnswerSessionStatus.Active;
                    return new RestoreResult(Status, "已恢复最后成功保存的本地状态，并核对当前服务器授权。");
                }
                _pendingWrite = BuildPending(checked(saved.AnswerVersion + 1), saved.Answers, saved.Marks, saved.CurrentQuestionId);
            }
            else
            {
                var initial = _paper is null ? EmptyAnswers() : _paper.Questions.ToDictionary(
                    q => q.QuestionId, _ => AnswerValue.Empty(), StringComparer.Ordinal);
                _pendingWrite = BuildPending(1, initial, new HashSet<string>(StringComparer.Ordinal), null);
            }
            await PersistPendingAsync(ct).ConfigureAwait(false);
            return new RestoreResult(Status, LastError ?? "本地状态已可靠保存。");
        }, ct);

    public Task<AnswerChangeResult> SetAnswerAsync(string questionId, AnswerValue value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(questionId) || !ValidAnswer(value))
            return Task.FromResult(ChangeFailure("题目标识或答案格式无效。"));
        if (_paper is not null && (_paper.Questions.SingleOrDefault(q => q.QuestionId == questionId) is not { } question ||
            AnswerSheetContract.ValidateAnswer(question, value) is not null))
            return Task.FromResult(ChangeFailure("题目或选项不符合本套试卷的作答约定。"));
        var frozenValue = new AnswerValue(value.Kind, Array.AsReadOnly(value.OptionIds.ToArray()));
        return ChangeAsync(state =>
        {
            var answers = state.Answers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            answers[questionId] = frozenValue;
            return BuildPending(checked(state.AnswerVersion + 1), answers, state.Marks, state.CurrentQuestionId);
        }, ct);
    }

    public Task<AnswerChangeResult> SetCurrentQuestionAsync(string? questionId, CancellationToken ct = default)
    {
        if (questionId is not null && (string.IsNullOrWhiteSpace(questionId) || !KnownQuestion(questionId)))
            return Task.FromResult(ChangeFailure("题目标识无效。"));
        return ChangeAsync(state => BuildPending(checked(state.AnswerVersion + 1), state.Answers, state.Marks, questionId), ct);
    }

    public Task<AnswerChangeResult> ToggleMarkAsync(string questionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(questionId) || !KnownQuestion(questionId))
            return Task.FromResult(ChangeFailure("题目标识无效。"));
        return ChangeAsync(state =>
        {
            var marks = new HashSet<string>(state.Marks, StringComparer.Ordinal);
            if (!marks.Remove(questionId)) marks.Add(questionId);
            return BuildPending(checked(state.AnswerVersion + 1), state.Answers, marks, state.CurrentQuestionId);
        }, ct);
    }

    public Task<AnswerChangeResult> RetrySaveAsync(CancellationToken ct = default) =>
        SerializedAsync(async () =>
        {
            if (_context is null || Volatile.Read(ref _authorizationInvalid) != 0)
                return ChangeFailure("当前设备没有有效授权，不能继续保存新状态。");
            if (_loadFailed)
                return ChangeFailure("恢复记录读取失败，请修复存储并重新启动，不能覆盖现有证据。");
            if (Volatile.Read(ref _freezeRequested) != 0)
            {
                var result = await PersistFixedAsync(ct).ConfigureAwait(false);
                return result.Succeeded ? AnswerChangeResult.Ok(AnswerVersion) : ChangeFailure(result.Error!);
            }
            if (_pendingWrite is null) return AnswerChangeResult.Ok(AnswerVersion);
            return await PersistPendingAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task InvalidateAuthorizationAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _authorizationInvalid, 1);
        return SerializedAsync(() =>
        {
            _status = AnswerSessionStatus.PermissionDenied;
            LastError = "当前设备授权已失效，已停止新增作答；原答案和固定答卷继续保留。";
            return Task.FromResult(true);
        }, ct);
    }

    public Task SuspendAsync(string reason, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _suspendRequested, 1);
        return SerializedAsync(() =>
        {
            LastError = string.IsNullOrWhiteSpace(reason) ? "当前作答已停止，原本地记录继续保留。" : reason;
            return Task.FromResult(true);
        }, ct);
    }

    public Task<FixSubmissionResult> FixSubmissionAsync(string submissionId, CancellationToken ct = default)
    {
        if (_key is null || _context is null || _loadFailed || Volatile.Read(ref _authorizationInvalid) != 0 ||
            string.IsNullOrWhiteSpace(submissionId))
            return Task.FromResult(new FixSubmissionResult(false, false, null, "会话尚未启动或提交标识无效。"));
        // 在等待当前持久写入结束前立即封锁之后排队的修改。先到的标识永远保留。
        Interlocked.CompareExchange(ref _requestedSubmissionId, submissionId, null);
        Interlocked.Exchange(ref _freezeRequested, 1);
        return SerializedAsync(() => PersistFixedAsync(ct), ct);
    }

    private Task<AnswerChangeResult> ChangeAsync(Func<AnswerStateRecord, PendingWrite> change, CancellationToken ct) =>
        SerializedAsync(async () =>
        {
            var error = WritableError();
            if (error is not null) return ChangeFailure(error);
            _pendingWrite = change(_reliable!);
            return await PersistPendingAsync(ct).ConfigureAwait(false);
        }, ct);

    private async Task<AnswerChangeResult> PersistPendingAsync(CancellationToken ct)
    {
        if (_pendingWrite is not { } pending) return AnswerChangeResult.Ok(AnswerVersion);
        StoreWriteResult result;
        try
        {
            result = await _store.SaveAnswerStateAsync(_key!, pending.State, pending.Snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = StoreWriteResult.Fail("保存结果尚未确认，已保留原操作，请重试。");
        }
        if (!result.Succeeded)
        {
            BlockWrites(result.Error ?? "本地保存失败，已保留未落盘修改。");
            return ChangeFailure(LastError!);
        }
        _reliable = pending.State;
        _pendingWrite = null;
        _status = AnswerSessionStatus.Active;
        LastError = null;
        StateSaved?.Invoke();
        return AnswerChangeResult.Ok(AnswerVersion);
    }

    private async Task<FixSubmissionResult> PersistFixedAsync(CancellationToken ct)
    {
        if (_context is null || !_context.IsServerAuthorized || Volatile.Read(ref _authorizationInvalid) != 0 || _loadFailed)
            return new FixSubmissionResult(false, _fixed is not null, null, "当前身份或恢复记录未经有效核对，不能固定新的答卷。");
        if (_fixed is not null)
            return new FixSubmissionResult(true, true, FreezeSubmission(_fixed), null);
        var isReplay = _frozenIntent is not null;
        var effective = _pendingWrite?.State ?? _reliable;
        if (effective is null || _requestedSubmissionId is null)
            return new FixSubmissionResult(false, isReplay, null, "没有可固定的完整本地状态。");
        if (_paper is not null && AnswerSheetContract.Validate(_paper, effective.Answers) is not null)
            return new FixSubmissionResult(false, isReplay, null, "答卷与题目清单不完整对应，不能固定或补造缺失记录。");
        _frozenIntent ??= new FixedSubmissionRecord(_requestedSubmissionId, effective.PaperVersion, effective.AnswerVersion,
            FreezeAnswers(effective.Answers), _time.GetUtcNow(), RequestIds.New(), _context.ParticipationId,
            _context.EntryVersion, _context.DeviceId, _context.AuthorizationVersion);
        (StoreWriteResult Result, FixedSubmissionRecord? Record) result;
        try
        {
            // 未保存修改与固定记录必须同一事务提交。中途故障不能落下可重新作答的半份状态。
            result = _pendingWrite is { } pending
                ? await _store.TryFixSubmissionAsync(_key!, _frozenIntent, pending.State, pending.Snapshot, ct).ConfigureAwait(false)
                : await _store.TryFixSubmissionAsync(_key!, _frozenIntent, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = (StoreWriteResult.Fail("固定答卷保存结果尚未确认，原标识与快照已保留，请重试。"), null);
        }
        if (!result.Result.Succeeded || result.Record is null)
        {
            BlockWrites(result.Result.Error ?? "存储未返回已持久保存的固定答卷。");
            return new FixSubmissionResult(false, isReplay, FreezeSubmission(_frozenIntent), LastError);
        }
        if (!ValidFixedRecord(result.Record, effective))
        {
            Interlocked.Exchange(ref _authorizationInvalid, 1);
            LastError = "固定答卷回读与当前身份或可靠状态不一致，已停止操作并保留原记录。";
            return new FixSubmissionResult(false, isReplay, null, LastError);
        }
        _reliable = effective;
        _pendingWrite = null;
        _fixed = FreezeSubmission(result.Record);
        _frozenIntent = _fixed;
        _status = AnswerSessionStatus.Fixed;
        LastError = null;
        SubmissionFixed?.Invoke(FreezeSubmission(_fixed));
        return new FixSubmissionResult(true, isReplay || _fixed.SubmissionId != _requestedSubmissionId, FreezeSubmission(_fixed), null);
    }

    private PendingWrite BuildPending(long version, IReadOnlyDictionary<string, AnswerValue> answers,
        IReadOnlySet<string> marks, string? currentQuestion)
    {
        var now = _time.GetUtcNow();
        var state = new AnswerStateRecord(_context!.ParticipationId, _context.EntryVersion, _context.AuthorizationVersion,
            _context.PaperVersion, version, currentQuestion, now, FreezeAnswers(answers),
            new HashSet<string>(marks, StringComparer.Ordinal), _context.DeviceId);
        var snapshot = new PendingSnapshot(_key!.ExamId, _key.CandidateId, _context.ParticipationId,
            _context.EntryVersion, _context.DeviceId, _context.AuthorizationVersion, version, _context.PaperVersion,
            FreezeAnswers(answers), currentQuestion, now, RequestIds.New());
        return new PendingWrite(state, snapshot);
    }

    private string? WritableError() => Status switch
    {
        AnswerSessionStatus.Active when _reliable is not null && _context?.IsServerAuthorized == true => null,
        AnswerSessionStatus.WriteBlocked => "本地保存失败，未落盘修改仍保留，请先重试保存。",
        AnswerSessionStatus.FixPending or AnswerSessionStatus.Fixed => "答卷已冻结，不能新增或修改答案。",
        AnswerSessionStatus.PermissionDenied => "当前设备没有有效答题授权。",
        AnswerSessionStatus.Suspended => LastError ?? "当前作答已停止。",
        _ => "会话未完成初始化。",
    };

    private void BlockWrites(string message)
    {
        _status = AnswerSessionStatus.WriteBlocked;
        LastError = message;
        WriteBlocked?.Invoke(message);
    }

    private RestoreResult DenyRestore(string message)
    {
        Interlocked.Exchange(ref _authorizationInvalid, 1);
        _status = AnswerSessionStatus.PermissionDenied;
        LastError = message;
        return new RestoreResult(Status, message);
    }

    private bool MatchesContext(AnswerStateRecord state) =>
        state.ParticipationId == _context!.ParticipationId && state.EntryVersion == _context.EntryVersion &&
        state.AuthorizationVersion == _context.AuthorizationVersion && state.DeviceId == _context.DeviceId &&
        state.AnswerVersion > 0 && state.PaperVersion > 0 && state.Answers is not null && state.Marks is not null;

    private bool ValidFixedRecord(FixedSubmissionRecord record, AnswerStateRecord state) =>
        !string.IsNullOrWhiteSpace(record.SubmissionId) && !string.IsNullOrWhiteSpace(record.RequestId) &&
        record.ParticipationId == _context!.ParticipationId && record.EntryVersion == _context.EntryVersion &&
        record.AuthorizationVersion == _context.AuthorizationVersion && record.DeviceId == _context.DeviceId &&
        record.AnswerVersion == state.AnswerVersion && record.PaperVersion == state.PaperVersion &&
        record.FrozenAnswers is not null &&
        SameAnswers(record.FrozenAnswers, state.Answers);

    private static bool SameAnswers(IReadOnlyDictionary<string, AnswerValue> first, IReadOnlyDictionary<string, AnswerValue> next) =>
        first.Count == next.Count && first.All(pair => next.TryGetValue(pair.Key, out var value) &&
            pair.Value.Kind == value.Kind && pair.Value.OptionIds.SequenceEqual(value.OptionIds));

    private AnswerChangeResult ChangeFailure(string message) => new(false, AnswerVersion, message);
    private bool KnownQuestion(string id) => _paper is null || _paper.Questions.Any(q => q.QuestionId == id);
    private static bool ValidKey(LocalStateKey key) => !string.IsNullOrWhiteSpace(key.ServerKey) &&
        !string.IsNullOrWhiteSpace(key.ExamId) && !string.IsNullOrWhiteSpace(key.CandidateId);
    private static bool ValidAnswer(AnswerValue value) => value is not null && Enum.IsDefined(value.Kind) &&
        value.OptionIds is not null && (value.Kind == AnswerKind.Selected
            ? value.OptionIds.Count > 0 && value.OptionIds.All(option => !string.IsNullOrWhiteSpace(option)) &&
                value.OptionIds.Distinct(StringComparer.Ordinal).Count() == value.OptionIds.Count
            : value.OptionIds.Count == 0);
    private static IReadOnlyDictionary<string, AnswerValue> EmptyAnswers() => new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, AnswerValue> FreezeAnswers(IReadOnlyDictionary<string, AnswerValue> answers) =>
        PersistedProgress.FreezeAnswers(answers);
    private static AnswerStateRecord FreezeState(AnswerStateRecord state) => state with
    {
        Answers = FreezeAnswers(state.Answers),
        Marks = new HashSet<string>(state.Marks, StringComparer.Ordinal),
    };
    private static FixedSubmissionRecord FreezeSubmission(FixedSubmissionRecord record) =>
        record with { FrozenAnswers = FreezeAnswers(record.FrozenAnswers) };

    private async Task<T> SerializedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
