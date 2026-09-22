using System.Collections.ObjectModel;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

/// <summary>会话状态：Active 可作答；其余状态均拒绝新增修改。</summary>
public enum AnswerSessionStatus
{
    NotStarted,
    Active,
    /// <summary>本地写入失败后：明确阻止新增修改（判据：写入失败明确阻止新增修改）。</summary>
    WriteBlocked,
    /// <summary>已固定提交：不生成新答案（判据：已固定提交不生成新答案）。</summary>
    Fixed,
    /// <summary>本地记录与当前参试授权不符：拒绝恢复且不可作答。</summary>
    PermissionDenied,
}

/// <summary>参试授权上下文（来自确认参试结果；启动恢复时据此核对权限）。</summary>
public sealed record AnswerSessionContext(
    string ParticipationId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion,
    string? DeviceId = null);

/// <summary>恢复结果。</summary>
public sealed record RestoreResult(AnswerSessionStatus Status, string? Message)
{
    public bool IsUsable => Status is AnswerSessionStatus.Active or AnswerSessionStatus.Fixed;
}

/// <summary>答案/标记修改结果。</summary>
public sealed record AnswerChangeResult(bool Succeeded, long AnswerVersion, string? Error)
{
    public static AnswerChangeResult Ok(long version) => new(true, version, null);

    public static AnswerChangeResult Fail(string error) => new(false, 0, error);
}

/// <summary>固定提交结果：IsReplay 表示沿用既有提交（不生成新答案）。</summary>
public sealed record FixSubmissionResult(
    bool Succeeded, bool IsReplay, FixedSubmissionRecord? Record, string? Error);

/// <summary>
/// 答题会话状态机（B04）：每次答案操作写入递增版本并同事务落盘（答案 + 待发送快照）；
/// 私人标记独立落盘（不进快照、不上报）；启动时按服务器、考试和考生恢复并核对权限；
/// 写入失败后明确阻止新增修改；已固定提交不生成新答案。
/// 崩溃后新建会话重新 StartAsync 即恢复"最后成功保存状态"。
/// </summary>
public sealed class AnswerSession
{
    private readonly ILocalStateStore _store;

    private LocalStateKey? _key;
    private AnswerSessionContext? _context;
    private Dictionary<string, AnswerValue> _answers = new(StringComparer.Ordinal);
    private HashSet<string> _marks = new(StringComparer.Ordinal);
    private bool _hasSavedState;
    private string? _fixedSubmissionId;

    public AnswerSession(ILocalStateStore store) => _store = store;

    public AnswerSessionStatus Status { get; private set; } = AnswerSessionStatus.NotStarted;

    public long AnswerVersion { get; private set; }

    public string? CurrentQuestionId { get; private set; }

    public string? FixedSubmissionId => _fixedSubmissionId;

    /// <summary>当前答案（冻结副本；已固定提交时为固定快照）。</summary>
    public IReadOnlyDictionary<string, AnswerValue> Answers =>
        new ReadOnlyDictionary<string, AnswerValue>(
            _answers.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    /// <summary>私人标记（冻结副本；仅本机，不进待发送快照）。</summary>
    public IReadOnlySet<string> Marks => new HashSet<string>(_marks, StringComparer.Ordinal);

    /// <summary>写入失败时触发（页面据此明示"本地保存失败，已阻止新增修改"）。</summary>
    public event Action<string>? WriteBlocked;

    /// <summary>固定提交成功（含重放）时触发。</summary>
    public event Action<FixedSubmissionRecord>? SubmissionFixed;

    /// <summary>
    /// 启动/恢复：读取本地最后成功保存状态并核对权限。
    /// 参试记录（ParticipationId）、进入版本或授权版本与本地记录不符 → 拒绝恢复且不可作答
    /// （防旧授权/串号写进度）。试卷版本变更不阻止恢复（B06 在线更新沿用稳定题号与既有答案）。
    /// 无本地记录时写入初始状态（版本 1）。
    /// </summary>
    public async Task<RestoreResult> StartAsync(
        LocalStateKey key, AnswerSessionContext context, CancellationToken ct = default)
    {
        _key = key;
        _context = context;

        var saved = await _store.LoadAnswerStateAsync(key, ct).ConfigureAwait(false);
        if (saved is not null)
        {
            if (!string.Equals(saved.ParticipationId, context.ParticipationId, StringComparison.Ordinal) ||
                saved.AuthorizationVersion != context.AuthorizationVersion ||
                saved.EntryVersion != context.EntryVersion)
            {
                Status = AnswerSessionStatus.PermissionDenied;
                return new RestoreResult(
                    AnswerSessionStatus.PermissionDenied,
                    "本地记录与当前参试授权不符，已拒绝恢复并锁定作答（需监考员处理）。");
            }

            _answers = new Dictionary<string, AnswerValue>(saved.Answers, StringComparer.Ordinal);
            _marks = new HashSet<string>(saved.Marks, StringComparer.Ordinal);
            AnswerVersion = saved.AnswerVersion;
            CurrentQuestionId = saved.CurrentQuestionId;
            _hasSavedState = true;

            var fixedSubmission = await _store.LoadFixedSubmissionAsync(key, ct).ConfigureAwait(false);
            if (fixedSubmission is not null)
            {
                _answers = new Dictionary<string, AnswerValue>(fixedSubmission.FrozenAnswers, StringComparer.Ordinal);
                _fixedSubmissionId = fixedSubmission.SubmissionId;
                AnswerVersion = fixedSubmission.AnswerVersion;
                Status = AnswerSessionStatus.Fixed;
                return new RestoreResult(
                    AnswerSessionStatus.Fixed, "已恢复固定提交，答案不再变化。");
            }

            Status = AnswerSessionStatus.Active;
            return new RestoreResult(AnswerSessionStatus.Active, "已恢复最后成功保存的本地状态。");
        }

        // 无本地记录：写入初始状态（版本 1，空答案），使标记/切题始终有状态行
        AnswerVersion = 1;
        var initial = BuildRecord(AnswerVersion);
        var initialResult = await _store.SaveAnswerStateAsync(key, initial, BuildSnapshot(AnswerVersion), ct).ConfigureAwait(false);
        if (!initialResult.Succeeded)
        {
            Status = AnswerSessionStatus.WriteBlocked;
            var error = $"本地状态初始化失败：{initialResult.Error}（已阻止作答）";
            WriteBlocked?.Invoke(error);
            return new RestoreResult(AnswerSessionStatus.WriteBlocked, error);
        }

        _hasSavedState = true;
        Status = AnswerSessionStatus.Active;
        return new RestoreResult(AnswerSessionStatus.Active, "无本地记录，已初始化本地状态。");
    }

    /// <summary>
    /// 修改答案：版本 +1 并同事务落盘（答案 + 待发送快照）。
    /// 写失败 → 会话进入 WriteBlocked，阻止后续一切新增修改；内存回滚到落盘前状态。
    /// </summary>
    public async Task<AnswerChangeResult> SetAnswerAsync(
        string questionId, AnswerValue value, CancellationToken ct = default)
    {
        var guard = GuardWritable();
        if (guard is not null)
        {
            return AnswerChangeResult.Fail(guard);
        }

        var next = new Dictionary<string, AnswerValue>(_answers, StringComparer.Ordinal)
        {
            [questionId] = value,
        };
        var newVersion = _hasSavedState ? AnswerVersion + 1 : 1;
        var record = BuildRecord(newVersion, next);
        var snapshot = BuildSnapshot(newVersion, next);

        var result = await _store.SaveAnswerStateAsync(_key!, record, snapshot, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            BlockWrites(result.Error);
            return AnswerChangeResult.Fail("本地保存失败，已阻止新增修改。请监考员处理，答案保留在最后成功保存的状态。");
        }

        _answers = next;
        AnswerVersion = newVersion;
        _hasSavedState = true;
        return AnswerChangeResult.Ok(newVersion);
    }

    /// <summary>切换当前题（随答案状态同落盘；不改变答案版本）。</summary>
    public async Task<AnswerChangeResult> SetCurrentQuestionAsync(
        string? questionId, CancellationToken ct = default)
    {
        var guard = GuardWritable();
        if (guard is not null)
        {
            return AnswerChangeResult.Fail(guard);
        }

        CurrentQuestionId = questionId;
        var record = BuildRecord(AnswerVersion);
        var result = await _store.SaveAnswerStateAsync(_key!, record, BuildSnapshot(AnswerVersion), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            BlockWrites(result.Error);
            return AnswerChangeResult.Fail("本地保存失败，已阻止新增修改。");
        }

        _hasSavedState = true;
        return AnswerChangeResult.Ok(AnswerVersion);
    }

    /// <summary>切换私人标记（整体落盘；仅本机可见、按服务器/考试/考生隔离、重启保留）。</summary>
    public async Task<AnswerChangeResult> ToggleMarkAsync(
        string questionId, CancellationToken ct = default)
    {
        var guard = GuardWritable();
        if (guard is not null)
        {
            return AnswerChangeResult.Fail(guard);
        }

        var next = new HashSet<string>(_marks, StringComparer.Ordinal);
        if (!next.Remove(questionId))
        {
            next.Add(questionId);
        }

        var result = await _store.SaveMarksAsync(_key!, next, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            BlockWrites(result.Error);
            return AnswerChangeResult.Fail("私人标记保存失败，已阻止新增修改。");
        }

        _marks = next;
        return AnswerChangeResult.Ok(AnswerVersion);
    }

    /// <summary>
    /// 固定提交：以当前答案生成本地固定答卷（供 B05 上报与回执核对）。
    /// 已存在固定提交时返回原记录（IsReplay），不生成新答案；写失败可重试。
    /// </summary>
    public async Task<FixSubmissionResult> FixSubmissionAsync(
        string submissionId, CancellationToken ct = default)
    {
        if (_key is null || _context is null)
        {
            return new FixSubmissionResult(false, false, null, "会话未启动。");
        }

        if (Status == AnswerSessionStatus.PermissionDenied)
        {
            return new FixSubmissionResult(false, false, null, "当前参试授权与本地记录不符，不能提交。");
        }

        var record = new FixedSubmissionRecord(
            submissionId,
            _context.PaperVersion,
            AnswerVersion,
            FreezeAnswers(_answers),
            DateTimeOffset.Now);

        // 重放判定：本次调用前会话已知存在固定提交（同 ID 重试或换 ID 重放均保留第一次）
        var isReplay = _fixedSubmissionId is not null;
        var (result, stored) = await _store.TryFixSubmissionAsync(_key, record, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new FixSubmissionResult(false, false, null, $"固定提交保存失败：{result.Error}");
        }

        _fixedSubmissionId = stored?.SubmissionId ?? submissionId;
        if (stored is not null)
        {
            _answers = new Dictionary<string, AnswerValue>(stored.FrozenAnswers, StringComparer.Ordinal);
            AnswerVersion = stored.AnswerVersion;
        }

        Status = AnswerSessionStatus.Fixed;
        SubmissionFixed?.Invoke(stored ?? record);
        return new FixSubmissionResult(true, isReplay, stored ?? record, null);
    }

    private string? GuardWritable() => Status switch
    {
        AnswerSessionStatus.NotStarted => "会话未启动。",
        AnswerSessionStatus.WriteBlocked => "本地写入失败已锁定，不能新增修改。",
        AnswerSessionStatus.Fixed => "已固定提交，不能生成新答案。",
        AnswerSessionStatus.PermissionDenied => "参试授权与本地记录不符，不能作答。",
        _ => null,
    };

    private void BlockWrites(string? error)
    {
        Status = AnswerSessionStatus.WriteBlocked;
        var message = $"本地保存失败：{error ?? "未知磁盘错误"}。已阻止新增修改，答案保留在最后成功保存的状态。";
        WriteBlocked?.Invoke(message);
    }

    private AnswerStateRecord BuildRecord(long version, IReadOnlyDictionary<string, AnswerValue>? answers = null) =>
        new(
            _context!.ParticipationId,
            _context.EntryVersion,
            _context.AuthorizationVersion,
            _context.PaperVersion,
            version,
            CurrentQuestionId,
            DateTimeOffset.Now,
            FreezeAnswers(answers ?? _answers),
            new HashSet<string>(_marks, StringComparer.Ordinal));

    private PendingSnapshot BuildSnapshot(long version, IReadOnlyDictionary<string, AnswerValue>? answers = null) =>
        new(
            _key!.ExamId,
            _key.CandidateId,
            _context!.ParticipationId,
            _context.EntryVersion,
            _context.DeviceId,
            _context.AuthorizationVersion,
            version,
            _context.PaperVersion,
            FreezeAnswers(answers ?? _answers),
            CurrentQuestionId,
            DateTimeOffset.Now);

    private static IReadOnlyDictionary<string, AnswerValue> FreezeAnswers(
        IReadOnlyDictionary<string, AnswerValue> answers) =>
        PersistedProgress.FreezeAnswers(answers);
}
