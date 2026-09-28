using Exam.Contracts.Common;
using Exam.Contracts.Resources;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.Student.Core;

/// <summary>左侧题号方框的显示状态。已作答（普通选项或"不作答"）变绿；私人标记右上角红点；当前题高亮。</summary>
public sealed record QuestionNavBox(
    string QuestionId, int QuestionNo, bool IsAnswered, bool IsMarked, bool IsCurrent);

/// <summary>一个题组的导航分区：科目标题 + 题组标题 + 题号方框（按题号数值升序）。</summary>
public sealed record GroupNavSection(
    string SubjectId, string SubjectName, string GroupId, string GroupTitle,
    IReadOnlyList<QuestionNavBox> Boxes);

/// <summary>
/// B03 左侧导航：按科目、题组显示题号方框，点击跳题。
/// 全卷题号唯一、跨科不重置；科目按清单顺序，题组按 Order，题目按题号数值升序。
/// </summary>
public sealed class PaperNavigator
{
    private readonly IReadOnlyDictionary<string, PaperQuestionStructure> _byId;

    public PaperNavigator(StudentPaperManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Manifest = manifest;
        Questions = manifest.Questions
            .Select(q => q.Structure)
            .OrderBy(q => q.QuestionNo, Comparer<int>.Create((a, b) => a.CompareTo(b)))
            .ToArray();
        _byId = Questions.ToDictionary(q => q.QuestionId, StringComparer.Ordinal);
    }

    public StudentPaperManifest Manifest { get; }

    /// <summary>全卷题目按题号升序排列；首题即进入考试时的缺省当前题。</summary>
    public IReadOnlyList<PaperQuestionStructure> Questions { get; }

    public PaperQuestionStructure? Find(string questionId) =>
        questionId is not null && _byId.TryGetValue(questionId, out var question) ? question : null;

    public PaperQuestionStructure FirstQuestion() =>
        Questions.Count > 0 ? Questions[0] :
            throw new InvalidOperationException("学生清单没有题目。");

    /// <summary>构建导航分区。answers/marks 为当前可靠快照（可为 null 表示尚无作答）。</summary>
    public IReadOnlyList<GroupNavSection> BuildSections(
        IReadOnlyDictionary<string, AnswerValue>? answers,
        IReadOnlySet<string>? marks,
        string? currentQuestionId)
    {
        var groupsBySubject = Manifest.Groups
            .GroupBy(g => g.SubjectId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Order).ToArray(), StringComparer.Ordinal);
        var questionsByGroup = Questions.GroupBy(q => q.GroupId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var sections = new List<GroupNavSection>();
        foreach (var subject in Manifest.Subjects)
        {
            if (!groupsBySubject.TryGetValue(subject.SubjectId, out var groups)) { continue; }
            foreach (var group in groups)
            {
                if (!questionsByGroup.TryGetValue(group.GroupId, out var questions) || questions.Length == 0)
                {
                    continue;
                }
                var boxes = questions.Select(q => new QuestionNavBox(
                        q.QuestionId, q.QuestionNo,
                        IsAnswered(answers, q.QuestionId),
                        marks is not null && marks.Contains(q.QuestionId),
                        string.Equals(q.QuestionId, currentQuestionId, StringComparison.Ordinal)))
                    .ToArray();
                sections.Add(new GroupNavSection(subject.SubjectId, subject.Name, group.GroupId, group.Name, boxes));
            }
        }
        return sections;
    }

    /// <summary>选中普通选项或"不作答"后题号变绿；取消全部选择恢复未作答颜色。</summary>
    public static bool IsAnswered(IReadOnlyDictionary<string, AnswerValue>? answers, string questionId) =>
        answers is not null && answers.TryGetValue(questionId, out var answer) && answer.Kind != AnswerKind.Empty;
}
