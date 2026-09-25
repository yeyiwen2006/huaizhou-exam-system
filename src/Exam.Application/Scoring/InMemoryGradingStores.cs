using Exam.Contracts.Submissions;
using Exam.Domain.Scoring;

namespace Exam.Application.Scoring;

/// <summary>
/// 计分结果的内存参考实现，供单元测试与状态重放参考。生产使用 PostgreSQL 实现；
/// 内存锁不能当作跨进程一致性机制（见 docs/architecture.md）。
/// </summary>
public sealed class InMemoryGradingResultStore : IGradingResultStore
{
    private readonly Dictionary<string, StoredGradingResult> _results = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ValueTask SaveAsync(StoredGradingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        result = result.Snapshot();
        lock (_gate)
        {
            if (_results.TryGetValue(result.SubmissionId, out var previous))
            {
                result.EnsureCanReplace(previous);
                if (result.GradingVersion == previous.GradingVersion)
                    return ValueTask.CompletedTask;
            }
            _results[result.SubmissionId] = result;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<StoredGradingResult?> FindAsync(
        string submissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_results.TryGetValue(submissionId, out var result) ? result : null);
        }
    }

    public ValueTask<IReadOnlyList<StoredGradingResult>> ListAsync(
        string examId,
        long gradingVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentOutOfRangeException.ThrowIfLessThan(gradingVersion, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StoredGradingResult> results = _results.Values
                .Where(item => string.Equals(item.ExamId, examId, StringComparison.Ordinal)
                               && item.GradingVersion == gradingVersion)
                .OrderBy(item => item.Grade.CandidateId, StringComparer.Ordinal)
                .ThenBy(item => item.SubmissionId, StringComparer.Ordinal)
                .ToList();

            return ValueTask.FromResult(results);
        }
    }
}

/// <summary>
/// 计分任务的内存参考实现。语义与 PostgreSQL 实现一致：租约领取、重复领取不重复累计、
/// 失败只记录错误并保留任务。
/// </summary>
public sealed class InMemoryGradingTaskSource : IGradingTaskSource
{
    private sealed record TaskState(string SubmissionId, long GradingVersion, int Sequence,
        int AttemptCount, DateTimeOffset? ClaimedAt, DateTimeOffset? LeaseExpiresAt, bool Completed, string? LastError);
    private readonly List<TaskState> _tasks = [];
    private readonly object _gate = new();
    private int _sequence;

    public void Enqueue(string submissionId, long gradingVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(gradingVersion, 1);
        lock (_gate)
        {
            if (_tasks.Any(task => task.SubmissionId == submissionId && task.GradingVersion == gradingVersion)) return;
            _tasks.Add(new(submissionId, gradingVersion, _sequence++, 0, null, null, false, null));
        }
    }

    public IReadOnlyList<PendingGradingTask> Snapshot()
    {
        lock (_gate)
            return _tasks.OrderBy(task => task.Sequence)
                .Select(task => new PendingGradingTask(task.SubmissionId, task.GradingVersion, task.AttemptCount)).ToArray();
    }

    public string? LastErrorOf(string submissionId)
    {
        lock (_gate) return _tasks.FirstOrDefault(task => task.SubmissionId == submissionId)?.LastError;
    }

    public ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(int maxCount, DateTimeOffset now,
        TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = now + lease;
        lock (_gate)
        {
            // 优先未尝试/少尝试任务，避免持续失败的队首任务饿死后续答卷。
            var candidates = _tasks.Select((task, index) => (task, index))
                .Where(item => !item.task.Completed &&
                    (item.task.ClaimedAt is null || item.task.LeaseExpiresAt <= now))
                .OrderBy(item => item.task.AttemptCount).ThenBy(item => item.task.Sequence)
                .Take(maxCount).ToArray();
            var claimed = new List<PendingGradingTask>();
            foreach (var (task, index) in candidates)
            {
                var attempt = checked(task.AttemptCount + 1);
                _tasks[index] = task with { ClaimedAt = now, LeaseExpiresAt = expiresAt, AttemptCount = attempt };
                claimed.Add(new(task.SubmissionId, task.GradingVersion, attempt));
            }
            return ValueTask.FromResult<IReadOnlyList<PendingGradingTask>>(claimed);
        }
    }

    public ValueTask<bool> CompleteAsync(PendingGradingTask task, CancellationToken cancellationToken = default) =>
        Update(task, state => state with { Completed = true, ClaimedAt = null, LeaseExpiresAt = null, LastError = null }, cancellationToken);

    public ValueTask<bool> FailAsync(PendingGradingTask task, string error, CancellationToken cancellationToken = default) =>
        Update(task, state => state with { ClaimedAt = null, LeaseExpiresAt = null, LastError = PendingGradingTask.LimitError(error) }, cancellationToken);

    private ValueTask<bool> Update(PendingGradingTask claim, Func<TaskState, TaskState> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        claim.ValidateClaim();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            for (var index = 0; index < _tasks.Count; index++)
            {
                var task = _tasks[index];
                if (task.SubmissionId == claim.SubmissionId && task.GradingVersion == claim.GradingVersion &&
                    task.AttemptCount == claim.AttemptCount && task.ClaimedAt is not null && !task.Completed)
                {
                    _tasks[index] = change(task);
                    return ValueTask.FromResult(true);
                }
            }
            return ValueTask.FromResult(false);
        }
    }
}

/// <summary>计分输入的内存参考实现。</summary>
public sealed class InMemoryGradingInputs : IGradingInputs
{
    private readonly Dictionary<string, FinalSubmission> _submissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublishedGradingRules> _rules = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void SetSubmission(FinalSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        lock (_gate)
        {
            _submissions[submission.SubmissionId] = submission with
            {
                FrozenAnswers = PersistedProgress.FreezeAnswers(submission.FrozenAnswers)
            };
        }
    }

    public void SetRules(string examId, PublishedGradingRules rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfLessThan(rules.GradingVersion, 1);
        if (rules.Rules.ExamId != examId) throw new ArgumentException("规则与考试不匹配。", nameof(rules));

        lock (_gate)
        {
            _rules[examId] = rules;
        }
    }

    public ValueTask<FinalSubmission?> LoadSubmissionAsync(
        string submissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_submissions.TryGetValue(submissionId, out var submission) ? submission : null);
        }
    }

    public ValueTask<PublishedGradingRules?> LoadRulesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_rules.TryGetValue(examId, out var rules) ? rules : null);
        }
    }
}
