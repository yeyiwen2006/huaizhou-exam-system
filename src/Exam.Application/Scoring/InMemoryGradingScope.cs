using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.Application.Scoring;

/// <summary>重算范围的内存参考实现，供单元测试与状态重放参考。</summary>
public sealed class InMemoryGradingScope : IGradingScope
{
    private readonly Dictionary<string, List<SubmissionGradingState>> _scopes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>设置某场考试的全部最终答卷状态（测试与状态重放用）。</summary>
    public void SetScope(string examId, IEnumerable<SubmissionGradingState> states)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(states);

        lock (_gate)
        {
            _scopes[examId] = states.ToList();
        }
    }

    public ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);

        lock (_gate)
        {
            IReadOnlyList<SubmissionGradingState> states =
                _scopes.TryGetValue(examId, out var stored) ? stored.ToList() : [];
            return ValueTask.FromResult(states);
        }
    }
}
