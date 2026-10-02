using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Domain.Exams;
using Exam.Domain.Scoring;

namespace Exam.Domain.Importing;

/// <summary>按已保存的考务设置核对源材料，并仅在私有副本中绑定科目、题组编号。</summary>
public static class ImportCatalogBinding
{
    public static string Bind(string manifestJson, string answerJson, ExamSettings settings)
    {
        ImportedGradingRules.Create(manifestJson, answerJson);
        var json = ImportJson.Create();
        var source = JsonSerializer.Deserialize<ExamManifest>(manifestJson, json)!;
        var answers = JsonSerializer.Deserialize<AnswerKeyFile>(answerJson, json)!.Answers
            .ToDictionary(x => x.QuestionId, StringComparer.Ordinal);
        if (settings?.Subjects is null || source.Subjects.Count != settings.Subjects.Count)
            throw Mismatch("科目数量");
        var subjects = new List<ManifestSubject>();
        var groups = new List<ManifestGroup>();
        var subjectIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedSubjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in source.Subjects)
        {
            var matches = settings.Subjects.Where(s => SameName(s.Name, subject.Name)).ToArray();
            if (matches.Length != 1 || !usedSubjects.Add(matches[0].SubjectId)) throw Mismatch("科目名称");
            var configured = matches[0];
            if (!SameScore(subject.MaxScore, configured.MaxScore)) throw Mismatch("科目满分");
            subjectIds.Add(subject.SubjectId, configured.SubjectId);
            subjects.Add(subject with { SubjectId = configured.SubjectId, Name = configured.Name, MaxScore = configured.MaxScore });
            var sourceGroups = source.Groups.Where(g => g.SubjectId == subject.SubjectId).OrderBy(g => g.Order).ToArray();
            var targetGroups = configured.Groups.OrderBy(g => g.Order).ToArray();
            if (sourceGroups.Length != targetGroups.Length) throw Mismatch("题组数量");
            for (var index = 0; index < sourceGroups.Length; index++)
            {
                var group = sourceGroups[index];
                var target = targetGroups[index];
                if (!SameName(group.Name, target.Name) || group.Order != target.Order || group.Type != target.Type ||
                    group.QuestionCount != target.QuestionCount || group.AllowAbstain != target.AllowAbstain)
                    throw Mismatch("题组名称、顺序、题型、题量或弃权设置");
                if (!SameOne(group.Scoring?.OneCorrect, target.OneCorrect) ||
                    !SameMultiple(group.Scoring?.MultipleCorrect, target.MultipleCorrect))
                    throw Mismatch("题组计分规则");
                var total = ExactRational.Zero;
                foreach (var question in source.Questions.Where(q => q.GroupId == group.GroupId))
                {
                    var count = answers[question.QuestionId].CorrectOptionIds!.Count;
                    var expression = count == 1 ? group.Scoring?.OneCorrect?.Correct : group.Scoring?.MultipleCorrect?.AllCorrect;
                    var checkedExpression = ScoreExpressionParser.Check(expression);
                    if (checkedExpression.Root is null) throw Mismatch("逐题满分");
                    total += ScoreExpressionParser.Evaluate(checkedExpression.Root, count, count);
                }
                if (!ScoreConstantRules.TryParse(target.MaxScore, out var configuredTotal) || total != configuredTotal)
                    throw Mismatch("题组满分与逐题求和");
                groupIds.Add(group.GroupId, target.GroupId);
                groups.Add(group with { SubjectId = configured.SubjectId, GroupId = target.GroupId,
                    Name = target.Name, MaxScore = target.MaxScore });
            }
        }
        var bound = source with
        {
            Subjects = subjects,
            Groups = groups,
            Questions = source.Questions.Select(q => q with
            { SubjectId = subjectIds[q.SubjectId], GroupId = groupIds[q.GroupId] }).ToArray()
        };
        var result = JsonSerializer.Serialize(bound, json);
        ImportedGradingRules.Create(result, answerJson);
        return result;
    }

    /// <summary>服务器不得替上传方悄悄改编号；当前材料必须已经与目录完全绑定。</summary>
    public static void RequireMatch(string manifestJson, string answerJson, string examName, ExamSettings settings)
    {
        var json = ImportJson.Create();
        var source = JsonSerializer.Deserialize<ExamManifest>(manifestJson, json)
            ?? throw Mismatch("材料清单");
        if (!SameName(source.ExamName, examName)) throw Mismatch("考试名称");
        var bound = JsonSerializer.Deserialize<ExamManifest>(Bind(manifestJson, answerJson, settings), json)!;
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(source, json), JsonSerializer.SerializeToNode(bound, json)))
            throw Mismatch("科目和题组编号或设置");
    }

    private static bool SameName(string left, string right) => ExamSettingsRules.NameKey(left) == ExamSettingsRules.NameKey(right);
    private static bool SameScore(string? left, string? right) => ScoreConstantRules.TryParse(left, out var a) &&
        ScoreConstantRules.TryParse(right, out var b) && a == b;
    private static bool SameOne(ManifestOneCorrect? a, ManifestOneCorrect? b) => a is null || b is null
        ? a is null && b is null
        : SameExpression(a.Correct, b.Correct) && SameExpression(a.Blank, b.Blank) && SameExpression(a.Wrong, b.Wrong);
    private static bool SameMultiple(ManifestMultipleCorrect? a, ManifestMultipleCorrect? b) => a is null || b is null
        ? a is null && b is null
        : SameExpression(a.AllCorrect, b.AllCorrect) && SameExpression(a.PartialCorrect, b.PartialCorrect) &&
          SameExpression(a.Blank, b.Blank) && SameExpression(a.Wrong, b.Wrong);

    private static bool SameExpression(string left, string right)
    {
        if (left == right) return true;
        var a = ScoreExpressionParser.Check(left).Root;
        var b = ScoreExpressionParser.Check(right).Root;
        if (a is null || b is null) return false;
        // The import contract bounds options to 26. Compare exact arithmetic over the whole supported
        // integer input domain, not floating-point strings or only the correct-answer case.
        try
        {
            for (var correct = 1; correct <= 26; correct++)
                for (var selected = 0; selected <= correct; selected++)
                    if (ScoreExpressionParser.Evaluate(a, correct, selected) != ScoreExpressionParser.Evaluate(b, correct, selected))
                        return false;
            return true;
        }
        catch (Exception error) when (error is ArithmeticException or ArgumentException) { return false; }
    }

    private static ArgumentException Mismatch(string field) => new($"试题包的{field}与已保存的考试设置不一致，请先核对材料和考试设置。");
}
