using Exam.Contracts.Importing;

namespace Exam.Admin.ViewModels;

// 仅投影已通过 D01 校验的清单结构；不保留源文件、计分表达式或独立答案。
public sealed record ManifestSubjectPreview(string SubjectId, string Name, string DeclaredMaxScore, int QuestionCount);

public sealed record ManifestGroupPreview(
    string SubjectName, string Name, string Type, int Order, int QuestionCount,
    string DeclaredMaxScore, bool AllowAbstain);

public sealed record ManifestQuestionPreview(
    int QuestionNo, string QuestionId, string SubjectName, string GroupName, string Type, string OptionIds);

public sealed record ManifestStructurePreview(
    int SchemaVersion, string ExamName,
    IReadOnlyList<ManifestSubjectPreview> Subjects,
    IReadOnlyList<ManifestGroupPreview> Groups,
    IReadOnlyList<ManifestQuestionPreview> Questions)
{
    public int SubjectCount => Subjects.Count;
    public int GroupCount => Groups.Count;
    public int QuestionCount => Questions.Count;

    public static ManifestStructurePreview FromValidatedManifest(ExamManifest manifest)
    {
        var subjectNames = manifest.Subjects.ToDictionary(item => item.SubjectId, item => item.Name, StringComparer.Ordinal);
        var subjectOrder = manifest.Subjects.Select((item, index) => (item.SubjectId, index))
            .ToDictionary(item => item.SubjectId, item => item.index, StringComparer.Ordinal);
        var groupNames = manifest.Groups.ToDictionary(item => item.GroupId, item => item.Name, StringComparer.Ordinal);
        var groupTypes = manifest.Groups.ToDictionary(item => item.GroupId, item => item.Type!, StringComparer.Ordinal);

        return new ManifestStructurePreview(
            manifest.SchemaVersion,
            manifest.ExamName,
            Array.AsReadOnly(manifest.Subjects.Select(subject => new ManifestSubjectPreview(
                subject.SubjectId, subject.Name, subject.MaxScore,
                manifest.Questions.Count(question => question.SubjectId == subject.SubjectId))).ToArray()),
            Array.AsReadOnly(manifest.Groups.OrderBy(group => subjectOrder[group.SubjectId])
                .ThenBy(group => group.Order)
                .Select(group => new ManifestGroupPreview(
                    subjectNames[group.SubjectId], group.Name, group.Type!, group.Order, group.QuestionCount,
                    group.MaxScore ?? "未声明", group.AllowAbstain)).ToArray()),
            Array.AsReadOnly(manifest.Questions.OrderBy(question => question.QuestionNo)
                .Select(question => new ManifestQuestionPreview(
                    question.QuestionNo, question.QuestionId, subjectNames[question.SubjectId],
                    groupNames[question.GroupId], groupTypes[question.GroupId],
                    string.Join("、", question.Options!))).ToArray()));
    }
}
