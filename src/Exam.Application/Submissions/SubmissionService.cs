using Exam.Application.Commands;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;

namespace Exam.Application.Submissions;

public sealed class SubmissionService : IDisposable
{
    private readonly ISubmissionStore _store;
    private readonly IGradingTaskQueue _gradingQueue;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _receiptIdFactory;
    private readonly SubmissionWriteAdmission _writeSlots;
    private readonly IParticipationAccessReader? _accessReader;

    public SubmissionService(
        ISubmissionStore store,
        IGradingTaskQueue gradingQueue,
        int maxConcurrentWrites = 32,
        TimeProvider? timeProvider = null,
        Func<string>? receiptIdFactory = null,
        IParticipationAccessReader? accessReader = null,
        int maxQueuedWritesPerClass = 300,
        TimeSpan? writeQueueTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gradingQueue);
        if (maxConcurrentWrites < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentWrites));
        }

        _store = store;
        _gradingQueue = gradingQueue;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _receiptIdFactory = receiptIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        _accessReader = accessReader;
        _writeSlots = new SubmissionWriteAdmission(maxConcurrentWrites, maxQueuedWritesPerClass,
            writeQueueTimeout ?? TimeSpan.FromSeconds(5));
    }

    public async ValueTask<SubmissionOperationResult<PersistedProgress>> SyncProgressAsync(
        SyncProgressRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = SubmissionPolicy.ValidateProgress(request);
        if (validationError is not null)
        {
            return SubmissionOperationResult<PersistedProgress>.Failure(
                SubmissionErrorCode.InvalidRequest,
                validationError);
        }

        if (!await _writeSlots.EnterAsync(final: false, cancellationToken))
        {
            return SubmissionOperationResult<PersistedProgress>.Failure(
                SubmissionErrorCode.ServiceRecovering,
                "持久化写入已达并发上限，请保留本地进度后重试。");
        }

        try
        {
            var result = await _store.SaveProgressAsync(
                request,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            return result.Status switch
            {
                ProgressPersistenceStatus.Saved => SubmissionOperationResult<PersistedProgress>.Success(result.Progress!),
                ProgressPersistenceStatus.RequestReplay => SubmissionOperationResult<PersistedProgress>.Success(
                    result.Progress!,
                    true,
                    "返回原进度同步结果。"),
                ProgressPersistenceStatus.VersionConflict => SubmissionOperationResult<PersistedProgress>.Failure(
                    SubmissionErrorCode.VersionConflict,
                    "答题版本不高于服务器已保存版本，旧进度未覆盖新进度。"),
                ProgressPersistenceStatus.AuthorizationRevoked => SubmissionOperationResult<PersistedProgress>.Failure(
                    SubmissionErrorCode.AuthorizationRevoked,
                    "设备授权或参试版本已失效。"),
                ProgressPersistenceStatus.InvalidState => SubmissionOperationResult<PersistedProgress>.Failure(
                    SubmissionErrorCode.VersionConflict,
                    "当前参试状态不允许写入答题进度。"),
                ProgressPersistenceStatus.AlreadySubmitted => SubmissionOperationResult<PersistedProgress>.Failure(
                    SubmissionErrorCode.AlreadySubmitted,
                    "最终答卷已经保存，不能再写入进度。"),
                _ => SubmissionOperationResult<PersistedProgress>.Failure(
                    SubmissionErrorCode.NotFound,
                    "未找到参试记录。")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SubmissionOperationResult<PersistedProgress>.Failure(
                SubmissionErrorCode.PersistenceFailed,
                $"进度未能持久保存：{exception.Message}");
        }
        finally
        {
            _writeSlots.Release();
        }
    }

    public async ValueTask<SubmissionOperationResult<SubmissionReceipt>> SubmitAsync(
        SubmitFinalAnswersRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = SubmissionPolicy.ValidateSubmission(request);
        if (validationError is not null)
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.InvalidRequest,
                validationError);
        }

        if (!await _writeSlots.EnterAsync(final: true, cancellationToken))
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.ServiceRecovering,
                "交卷写入已达并发上限，请沿用原 submissionId 重试。");
        }

        try
        {
            var result = await _store.SaveSubmissionAsync(
                request,
                _timeProvider.GetUtcNow(),
                _receiptIdFactory(),
                cancellationToken);
            if (result.Status == SubmissionPersistenceStatus.SubmissionIdConflict)
            {
                return SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.SubmissionIdConflict,
                    "submissionId 已被其他考生使用。");
            }

            if (result.Status == SubmissionPersistenceStatus.AuthorizationRevoked)
            {
                return SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.AuthorizationRevoked,
                    "设备授权或参试版本已失效。");
            }

            if (result.Status == SubmissionPersistenceStatus.VersionConflict)
            {
                return SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.VersionConflict,
                    "提交的试卷版本高于服务器当前有效版本。");
            }

            if (result.Status == SubmissionPersistenceStatus.InvalidState)
            {
                return SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.VersionConflict,
                    "当前参试状态不允许交卷。");
            }

            if (result.Status == SubmissionPersistenceStatus.ParticipationNotFound || result.Submission is null)
            {
                return SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.NotFound,
                    "未找到参试记录。");
            }

            // Replaying after a lost response also reaches this idempotent queue operation, which
            // repairs a crash between answer persistence and task publication without duplication.
            await _gradingQueue.EnqueueOnceAsync(
                result.Submission.SubmissionId,
                result.Submission.Receipt.GradingVersion,
                cancellationToken);

            return SubmissionOperationResult<SubmissionReceipt>.Success(
                result.Submission.Receipt,
                result.Status is SubmissionPersistenceStatus.RequestReplay or
                    SubmissionPersistenceStatus.ExistingSubmission,
                result.Status == SubmissionPersistenceStatus.Saved
                    ? "最终答卷与回执已持久保存。"
                    : "返回已持久保存的原回执，未覆盖最终答卷。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.PersistenceFailed,
                $"未确认交卷成功，请沿用原 submissionId 查询或重试：{exception.Message}");
        }
        finally
        {
            _writeSlots.Release();
        }
    }

    public async ValueTask<SubmissionOperationResult<SubmissionReceipt>> FindReceiptAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(examId) || string.IsNullOrWhiteSpace(candidateId))
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.InvalidRequest,
                "考试与考生标识不能为空。");
        }

        try
        {
            var submission = await _store.FindSubmissionAsync(examId, candidateId, cancellationToken);
            return submission is null
                ? SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.NotFound,
                    "尚未找到已持久保存的最终答卷。")
                : SubmissionOperationResult<SubmissionReceipt>.Success(
                    submission.Receipt,
                    true,
                    "已找到原交卷回执。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.PersistenceFailed,
                $"回执查询失败：{exception.Message}");
        }
    }

    public async ValueTask<SubmissionOperationResult<SubmissionReceipt>> FindReceiptForDeviceAsync(
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        CancellationToken cancellationToken = default)
    {
        if (_accessReader is null || string.IsNullOrWhiteSpace(participationId) ||
            string.IsNullOrWhiteSpace(deviceId) || entryVersion < 1 || authorizationVersion < 1)
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.InvalidRequest,
                "回执查询必须提供有效的参试和设备授权版本。");
        }

        try
        {
            var access = await _accessReader.FindAsync(participationId, cancellationToken);
            if (access is null || access.EntryVersion != entryVersion ||
                !string.Equals(access.AuthorizedDeviceId, deviceId, StringComparison.Ordinal) ||
                access.AuthorizationVersion != authorizationVersion)
            {
                return SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.AuthorizationRevoked,
                    "当前设备无权查询该参试记录的交卷回执。");
            }

            var submission = await _store.FindSubmissionByParticipationAsync(participationId, cancellationToken);
            return submission is null
                ? SubmissionOperationResult<SubmissionReceipt>.Failure(
                    SubmissionErrorCode.NotFound,
                    "尚未找到已持久保存的最终答卷。")
                : SubmissionOperationResult<SubmissionReceipt>.Success(
                    submission.Receipt,
                    true,
                    "已找到原交卷回执。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SubmissionOperationResult<SubmissionReceipt>.Failure(
                SubmissionErrorCode.PersistenceFailed,
                $"回执查询失败：{exception.Message}");
        }
    }

    public void Dispose() => _writeSlots.Dispose();
}

public sealed class CollectionCompletionService
{
    private readonly ISubmissionStore _submissionStore;
    private readonly ICollectionCompletionStore _completionStore;
    private readonly TimeProvider _timeProvider;
    private readonly IAuthoritativeCollectionCompletionStore? _authoritativeStore;

    public CollectionCompletionService(
        ISubmissionStore submissionStore,
        ICollectionCompletionStore completionStore,
        TimeProvider? timeProvider = null,
        IAuthoritativeCollectionCompletionStore? authoritativeStore = null)
    {
        _submissionStore = submissionStore ?? throw new ArgumentNullException(nameof(submissionStore));
        _completionStore = completionStore ?? throw new ArgumentNullException(nameof(completionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _authoritativeStore = authoritativeStore;
    }

    public ValueTask<CollectionCompletedNotification?> EvaluateAsync(
        string examId, CancellationToken cancellationToken = default) =>
        (_authoritativeStore ?? throw new InvalidOperationException("权威收齐存储未配置。"))
        .EvaluateAsync(examId, cancellationToken);

    public ValueTask<IReadOnlyList<CollectionCompletedNotification>> ReadEventsAsync(
        string examId, long afterRosterVersion, int limit = 100, CancellationToken cancellationToken = default) =>
        (_authoritativeStore ?? throw new InvalidOperationException("权威收齐存储未配置。"))
        .ReadEventsAsync(examId, afterRosterVersion, limit, cancellationToken);

    public async ValueTask<CollectionCompletedNotification?> EvaluateAsync(
        string examId,
        long rosterVersion,
        IReadOnlyCollection<string> actualParticipationIds,
        CancellationToken cancellationToken = default)
    {
        // Compatibility for old internal callers: supplied rosters never control production decisions.
        if (_authoritativeStore is not null)
        {
            return await _authoritativeStore.EvaluateAsync(examId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(examId))
        {
            throw new ArgumentException("考试标识不能为空。", nameof(examId));
        }

        if (rosterVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rosterVersion));
        }

        ArgumentNullException.ThrowIfNull(actualParticipationIds);
        var participantIds = actualParticipationIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (participantIds.Length == 0)
        {
            return null;
        }

        foreach (var participationId in participantIds)
        {
            var submission = await _submissionStore.FindSubmissionByParticipationAsync(
                participationId,
                cancellationToken);
            if (submission is null || !string.Equals(submission.ExamId, examId, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return await _completionStore.TryRecordAsync(
            examId,
            rosterVersion,
            participantIds.Length,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }
}
