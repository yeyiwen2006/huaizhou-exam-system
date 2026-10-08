using System.Collections.Concurrent;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;

namespace Exam.Application.Participation;

public sealed record CandidateRosterEntry(string ExamId, CandidateIdentity Candidate);

public interface ICandidateRosterDirectory
{
    ValueTask<IReadOnlyList<CandidateRosterEntry>> FindMatchesAsync(
        string name,
        string classText,
        string admissionNo,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsRegisteredAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryCandidateRosterDirectory : ICandidateRosterDirectory
{
    private readonly ConcurrentDictionary<string, CandidateRosterEntry> _entries = new(StringComparer.Ordinal);

    public void Add(CandidateRosterEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ExamId);
        ArgumentNullException.ThrowIfNull(entry.Candidate);
        _entries[Key(entry.ExamId, entry.Candidate.CandidateId)] = entry;
    }

    public ValueTask<IReadOnlyList<CandidateRosterEntry>> FindMatchesAsync(
        string name,
        string classText,
        string admissionNo,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CandidateRosterEntry> matches = _entries.Values
            .Where(entry =>
                StringComparer.Ordinal.Equals(entry.Candidate.Name, name) &&
                StringComparer.Ordinal.Equals(entry.Candidate.ClassText, classText) &&
                StringComparer.Ordinal.Equals(entry.Candidate.AdmissionNo, admissionNo))
            .OrderBy(entry => entry.ExamId, StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult(matches);
    }

    public ValueTask<bool> IsRegisteredAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_entries.ContainsKey(Key(examId, candidateId)));
    }

    private static string Key(string examId, string candidateId) => $"{examId.Length}:{examId}{candidateId}";
}

public sealed record ExamEntrySnapshot(
    string ExamId,
    DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd,
    ExamLifecycle Lifecycle,
    bool PackageReady,
    bool InstructionsReady,
    string EncryptedPackageUri,
    long PackageLength,
    long PaperVersion,
    long InstructionsVersion,
    string? UnlockReference)
{
    public long ScheduleVersion { get; init; }
    public long ReopenedAtScheduleVersion { get; init; }
}

public interface IExamEntryDirectory
{
    ValueTask<ExamEntrySnapshot?> FindAsync(
        string examId,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryExamEntryDirectory : IExamEntryDirectory
{
    private readonly ConcurrentDictionary<string, ExamEntrySnapshot> _exams = new(StringComparer.Ordinal);

    public void Set(ExamEntrySnapshot exam)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exam.ExamId);
        _exams[exam.ExamId] = exam;
    }

    public ValueTask<ExamEntrySnapshot?> FindAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_exams.GetValueOrDefault(examId));
    }
}
