namespace Exam.Contracts.Administration;

public sealed record CandidateRosterEntry(string CandidateId, string Name, string ClassText, string AdmissionNo);
public sealed record CandidateRosterSnapshot(string ExamId, long Version, IReadOnlyList<CandidateRosterEntry> Entries);
public sealed record RosterImportRow(int RowNumber, string Name, string ClassText, string AdmissionNo);
public sealed record RosterRowIssue(int RowNumber, string Field, string Message);
public sealed record ImportCandidateRosterRequest(string RequestId, long ExpectedVersion, IReadOnlyList<RosterImportRow> Rows);
/// <summary>Applied=false 时整批未写入；Issues 可逐行展示，不能把请求返回等同于导入成功。</summary>
public sealed record RosterImportResult(bool Applied, int Added, int Duplicates,
    CandidateRosterSnapshot Snapshot, IReadOnlyList<RosterRowIssue> Issues);
public sealed record UpdateCandidateRosterRequest(string RequestId, long ExpectedVersion, RosterImportRow Entry);
public sealed record DeleteCandidateRosterRequest(string RequestId, long ExpectedVersion);
