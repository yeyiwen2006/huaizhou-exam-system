using Exam.Contracts.Resources;
using Exam.Student.Core;

namespace Exam.Student.Core;

public sealed partial class StudentExamRuntime
{
    public bool IsPaperUpdateInProgress => Volatile.Read(ref _paperUpdateCommitting) != 0;

    internal bool IsPaperVersionReady(long version) => !_disposed && !Manager.ExamEnded && !HasFixedSubmission &&
        Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked && Manager.UnlockedPayload is not null &&
        Manager.Descriptor?.PaperVersion == version && _paper?.PaperVersion == version &&
        Answers is { Status: AnswerSessionStatus.Active } answers && answers.PaperVersion == version;

    /// <summary>生产更新入口：下载时保留旧版作答，只在提交阶段串行化答案写入和题包发布。</summary>
    public async Task<PaperUpdateResult> ApplyPaperUpdateAsync(IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _answerGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Answers is not { } answers || _answerContext is null || !_localRecoveryChecked || HasFixedSubmission)
                return PaperUpdateResult.Fail("本地答卷尚未可用或已固定，不能安装新版题包。");
            var result = await Manager.ApplyPaperUpdateWithAnswersAsync(async (update, cancellation) =>
            {
                Interlocked.Exchange(ref _paperUpdateCommitting, 1);
                return await answers.CommitPaperUpdateAsync(update, () =>
                {
                    _paper = update.Manifest;
                    _answerContext = _answerContext with { PaperVersion = update.Manifest.PaperVersion };
                    _lastInstalledUpdate = Manager.LastPaperInstallation;
                    _localStateError = null;
                }, cancellation).ConfigureAwait(false);
            }, progress, linked.Token).ConfigureAwait(false);
            if (!result.Succeeded) _localStateError = answers.LastError;
            return result;
        }
        finally
        {
            try
            {
                if (Manager.CurrentPhase == ExamPackageManager.Phase.Revoked && Answers is { } revoked)
                {
                    _paper = null;
                    await revoked.InvalidateAuthorizationAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else if (Manager.ExamEnded && Answers is { } ended)
                {
                    _paper = null;
                    await FreezeEndedAnswersAsync(ended, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _paperUpdateCommitting, 0);
                _answerGate.Release();
                NotifyStateChanged();
            }
        }
    }

    internal async Task<PaperUpdateInstallation?> EnsureInstalledReviewAsync(long version, DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        if (!IsPaperVersionReady(version)) return null;
        var installation = await Manager.EnsurePaperInstallationAsync(version, receivedAt, ct).ConfigureAwait(false);
        if (installation is not null)
        {
            _lastInstalledUpdate = installation;
            NotifyStateChanged();
        }
        return installation;
    }

    /// <summary>只接受管理器已经持久记录的实际安装，不让 UI 任意制造待复核版本。</summary>
    public void RecordInstalledPaperUpdate(long version, DateTimeOffset receivedAt, DateTimeOffset installedAt)
    {
        if (Manager.LastPaperInstallation is not { } record || record.PaperVersion != version ||
            record.ReceivedAt != receivedAt || record.InstalledAt != installedAt || !IsPaperVersionReady(version)) return;
        _lastInstalledUpdate = record;
        NotifyStateChanged();
    }

    public long? PendingReviewVersion => _lastInstalledUpdate is { } installed &&
        !_confirmedReviewVersions.ContainsKey(installed.PaperVersion) ? installed.PaperVersion : null;

    public Task<StoreWriteResult?> ConfirmPaperUpdateReviewAsync(CancellationToken ct = default) =>
        _lastInstalledUpdate?.PaperVersion is { } version ? ConfirmPaperUpdateReviewAsync(version, ct) :
            Task.FromResult<StoreWriteResult?>(null);

    /// <summary>确认的是考生看见的确切版本，旧对话框不能确认后来才安装的版本。</summary>
    public async Task<StoreWriteResult?> ConfirmPaperUpdateReviewAsync(long expectedVersion, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var installed = _lastInstalledUpdate;
        if (localState is null || _localKey is null || installed is null) return null;
        if (installed.PaperVersion != expectedVersion || !IsPaperVersionReady(expectedVersion))
            return StoreWriteResult.Fail("待复核版本或授权已变化，请查看当前试题后重新确认。");
        try
        {
            var result = await localState.SavePaperUpdateReviewAsync(_localKey,
                new(expectedVersion, installed.ReceivedAt, installed.InstalledAt, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            if (result.Succeeded) _confirmedReviewVersions.TryAdd(expectedVersion, 0);
            else _localStateError = $"复核确认保存失败：{result.Error}";
            NotifyStateChanged();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsLocalStorageError(error))
        {
            _localStateError = "复核确认尚未保存，待复核状态保留，请恢复存储后重试。";
            NotifyStateChanged();
            return StoreWriteResult.Fail(_localStateError);
        }
    }
}
