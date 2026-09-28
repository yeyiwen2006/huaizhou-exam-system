using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;

namespace Exam.Student.Core;

/// <summary>已认证的候选包。恢复意图先持久化，答案事务提交后 Publish 只做内存发布。</summary>
internal sealed record PreparedPaperUpdate(StudentPaperManifest Manifest, Func<bool> CanCommit,
    Action<AnswerStateRecord, PendingSnapshot> SaveIntent, Action Publish);

internal static class PaperUpdateCompatibility
{
    public static bool SameStructure(PaperStructure first, PaperStructure next)
    {
        if (first.Questions is null || next.Questions is null || first.Questions.Count != next.Questions.Count) return false;
        for (var i = 0; i < first.Questions.Count; i++)
        {
            var a = first.Questions[i];
            var b = next.Questions[i];
            if (a is null || b is null || a.QuestionId != b.QuestionId || a.QuestionNo != b.QuestionNo ||
                a.SubjectId != b.SubjectId || a.GroupId != b.GroupId || a.QuestionType != b.QuestionType ||
                a.AllowAbstain != b.AllowAbstain || a.OptionIds is null || b.OptionIds is null ||
                !a.OptionIds.SequenceEqual(b.OptionIds, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    public static bool SameManifestStructure(StudentPaperManifest first, StudentPaperManifest next) =>
        SameStructure(first.Structure, next.Structure) &&
        first.Subjects.Select(x => x.SubjectId).SequenceEqual(next.Subjects.Select(x => x.SubjectId), StringComparer.Ordinal) &&
        first.Groups.Select(x => (x.GroupId, x.SubjectId, x.Order))
            .SequenceEqual(next.Groups.Select(x => (x.GroupId, x.SubjectId, x.Order)));
}
