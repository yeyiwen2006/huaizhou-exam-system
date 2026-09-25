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

        lock (_gate)
        {
            // 按答卷去重：重算时覆盖同一份结果，不产生第二行。
            _results[result.SubmissionId] = result;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<StoredGradingResult?> FindAsync(
        string submissionId,
        CancellationToken cancellationToken = default)
    {
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
    private sealed record TaskState(
        string SubmissionId,
        long GradingVersion,
        int Sequence,
        int AttemptCount,
        DateTimeOffset? ClaimedAt,
        bool Completed,
        string? LastError);

    private readonly List<TaskState> _tasks = [];
    private readonly object _gate = new();
    private int _sequence;

    /// <summary>测试与状态重放用：登记一项待计分任务，与 A05 的 outbox 语义一致。</summary>
    public void Enqueue(string submissionId, long gradingVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(gradingVersion, 1);

        lock (_gate)
        {
            if (_tasks.Any(task => task.SubmissionId == submissionId && task.GradingVersion == gradingVersion))
            {
                return;
            }

            _tasks.Add(new TaskState(submissionId, gradingVersion, _sequence++, 0, null, Completed: false, null));
        }
    }

    /// <summary>测试用：查看仍未被删除的任务（含失败待重试的）。</summary>
    public IReadOnlyList<PendingGradingTask> Snapshot()
    {
        lock (_gate)
        {
            return _tasks
                .OrderBy(task => task.Sequence)
                .Select(task => new PendingGradingTask(task.SubmissionId, task.GradingVersion, task.AttemptCount))
                .ToList();
        }
    }

    /// <summary>测试用：读取某任务的最近错误。</summary>
    public string? LastErrorOf(string submissionId)
    {
        lock (_gate)
        {
            return _tasks.FirstOrDefault(task => task.SubmissionId == submissionId)?.LastError;
        }
    }

    public ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(
        int maxCount,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }

        lock (_gate)
        {
            var cutoff = now - lease;
            var claimed = new List<PendingGradingTask>();

            for (var index = 0; index < _tasks.Count && claimed.Count < maxCount; index++)
            {
                var task = _tasks[index];
                if (task.Completed)
                {
                    continue;
                }

                if (task.ClaimedAt is { } claimedAt && claimedAt >= cutoff)
                {
                    continue;
                }

                _tasks[index] = task with { ClaimedAt = now, AttemptCount = task.AttemptCount + 1 };
                claimed.Add(new PendingGradingTask(task.SubmissionId, task.GradingVersion, task.AttemptCount + 1));
            }

            return ValueTask.FromResult<IReadOnlyList<PendingGradingTask>>(claimed);
        }
    }

    public ValueTask CompleteAsync(
        string submissionId,
        long gradingVersion,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Update(submissionId, gradingVersion, task => task with
            {
                Completed = true,
                ClaimedAt = null,
                LastError = null
            });
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FailAsync(
        string submissionId,
        long gradingVersion,
        string error,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // 失败不删除任务：释放租约并记录错误，等待下次重试。
            Update(submissionId, gradingVersion, task => task with
            {
                ClaimedAt = null,
                LastError = error
            });
        }

        return ValueTask.CompletedTask;
    }

    private void Update(string submissionId, long gradingVersion, Func<TaskState, TaskState> change)
    {
        for (var index = 0; index < _tasks.Count; index++)
        {
            if (_tasks[index].SubmissionId == submissionId && _tasks[index].GradingVersion == gradingVersion)
            {
                _tasks[index] = change(_tasks[index]);
                return;
            }
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
            _submissions[submission.SubmissionId] = submission;
        }
    }

    public void SetRules(string examId, PublishedGradingRules rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(rules);

        lock (_gate)
        {
            _rules[examId] = rules;
        }
    }

    public ValueTask<FinalSubmission?> LoadSubmissionAsync(
        string submissionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_submissions.TryGetValue(submissionId, out var submission) ? submission : null);
        }
    }

    public ValueTask<PublishedGradingRules?> LoadRulesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_rules.TryGetValue(examId, out var rules) ? rules : null);
        }
    }
}
