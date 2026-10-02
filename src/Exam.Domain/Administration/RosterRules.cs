using Exam.Contracts.Administration;

namespace Exam.Domain.Administration;

public sealed record RosterImportPlan(IReadOnlyList<RosterImportRow> NewRows, int Duplicates,
    IReadOnlyList<RosterRowIssue> Issues);

public static class RosterRules
{
    public const int MaximumRows = 10000;
    public static IReadOnlyList<RosterRowIssue> Validate(RosterImportRow? row)
    {
        if (row is null) return [new(0, "row", "记录不能为空。")];
        var issues = new List<RosterRowIssue>();
        if (row.RowNumber < 1) issues.Add(new(row.RowNumber, "rowNumber", "行号必须大于零。"));
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Trim().Length > 120 || row.Name.Any(char.IsControl))
            issues.Add(new(row.RowNumber, "name", "姓名不能为空、超过120字或包含控制字符。"));
        if (!Digits(row.ClassText,32)) issues.Add(new(row.RowNumber,"classText","班级须为1至32位数字，前导零按文本保留。"));
        if (!Digits(row.AdmissionNo,64)) issues.Add(new(row.RowNumber,"admissionNo","准考证号须为1至64位数字，前导零按文本保留。"));
        return issues;
    }
    public static RosterImportPlan Plan(IReadOnlyList<CandidateRosterEntry> existing, IReadOnlyList<RosterImportRow>? rows)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (rows is null || rows.Count is < 1 or > MaximumRows)
            return new([],0,[new(0,"rows",$"每批须有1至{MaximumRows}条记录。")]);
        var issues = new List<RosterRowIssue>();
        var byNumber = existing.ToDictionary(x=>x.AdmissionNo,x=>new RosterImportRow(0,x.Name,x.ClassText,x.AdmissionNo),StringComparer.Ordinal);
        var added = new List<RosterImportRow>();
        var rowNumbers = new HashSet<int>();
        var duplicates = 0;
        foreach (var source in rows)
        {
            var invalid = Validate(source);
            if (invalid.Count>0) { issues.AddRange(invalid); continue; }
            var row=source with {Name=source.Name.Trim(),ClassText=source.ClassText.Trim(),AdmissionNo=source.AdmissionNo.Trim()};
            if (!rowNumbers.Add(row.RowNumber)) issues.Add(new(row.RowNumber,"rowNumber","同一批中的行号不能重复。"));
            if (byNumber.TryGetValue(row.AdmissionNo,out var prior))
            {
                if (prior.Name==row.Name && prior.ClassText==row.ClassText) duplicates++;
                else issues.Add(new(row.RowNumber,"admissionNo",prior.RowNumber==0
                    ? "准考证号与服务器名单冲突，整批未导入。" : $"准考证号与本批第{prior.RowNumber}行冲突，整批未导入。"));
            }
            else { byNumber.Add(row.AdmissionNo,row); added.Add(row); }
        }
        return new(added,duplicates,issues);
    }
    private static bool Digits(string? value,int limit) => value?.Trim() is {Length:>0} s && s.Length<=limit && s.All(c=>c is >= '0' and <= '9');
}
