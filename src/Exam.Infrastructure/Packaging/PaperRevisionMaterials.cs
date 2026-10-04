using System.Text;
using System.Text.Json.Nodes;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.Infrastructure.Packaging;

/// <summary>Compose an operator's working copy without rewriting the immutable historical paper archive.</summary>
public static class PaperRevisionMaterials
{
    public static byte[] WithCurrentRules(byte[] archiveBytes, GradingMaterialSnapshot current,
        string examId, long paperVersion, long gradingVersion)
    {
        var archive = PaperMaterialsArchive.Read(archiveBytes);
        if (!archive.Succeeded || archive.Manifest is null || archive.Materials is null ||
            archive.Manifest.ExamId != examId || archive.Manifest.PaperVersion != paperVersion ||
            current.ExamId != examId || current.PaperVersion != paperVersion || current.GradingVersion != gradingVersion)
            throw new InvalidDataException("取回过程中试卷或规则版本发生变化，请重新取回；没有生成混合版本工作副本。");
        var source = archive.Materials;
        var manifestPath = source.Resolve("manifest.json") ?? throw new InvalidDataException("源归档缺少清单。");
        var answerPath = source.Resolve("server-only/answer-key.json") ?? throw new InvalidDataException("源归档缺少完整答案。");
        var originalManifest = Encoding.UTF8.GetString(source.Read(manifestPath));
        var originalAnswers = Encoding.UTF8.GetString(source.Read(answerPath));
        var originalRules = ImportedGradingRules.Create(originalManifest, originalAnswers);
        var currentRules = ImportedGradingRules.Create(current.ManifestJson, current.AnswerKeyJson);
        if (currentRules.ExamId != examId || !currentRules.Matches(originalRules.Structure))
            throw new InvalidDataException("当前规则与已归档试卷结构不一致，不能生成修订副本。");
        var original = JsonNode.Parse(originalManifest)!.AsObject();
        var latest = JsonNode.Parse(current.ManifestJson)!.AsObject();
        foreach (var collection in new[] { (Name: "subjects", Id: "subjectId"), (Name: "groups", Id: "groupId") })
        {
            var currentItems = latest[collection.Name]!.AsArray().ToDictionary(n => n![collection.Id]!.GetValue<string>(), StringComparer.Ordinal);
            foreach (var item in original[collection.Name]!.AsArray())
            {
                var replacement = currentItems[item![collection.Id]!.GetValue<string>()]!;
                item["maxScore"] = replacement["maxScore"]!.DeepClone();
                if (collection.Name == "groups") item["scoring"] = replacement["scoring"]!.DeepClone();
            }
        }
        var updated = original.ToJsonString();
        if (!EquivalentGrading(updated, current.AnswerKeyJson, current.ManifestJson, current.AnswerKeyJson))
            throw new InvalidDataException("工作副本没有完整保留当前计分规则。");
        // Preserve every TeX/style/image byte and original question/display metadata; only grading
        // fields and the separate complete answer file are replaced in this private working copy.
        return PaperMaterialsArchive.Build(examId, paperVersion, source.Paths.Select(path =>
            new KeyValuePair<string, byte[]>(path, path == manifestPath ? Encoding.UTF8.GetBytes(updated) :
                path == answerPath ? Encoding.UTF8.GetBytes(current.AnswerKeyJson) : source.Read(path))));
    }

    public static bool EquivalentGrading(string manifest, string answers, string otherManifest, string otherAnswers) =>
        JsonNode.DeepEquals(Projection(manifest, answers), Projection(otherManifest, otherAnswers));

    private static JsonObject Projection(string manifest, string answers)
    {
        _ = ImportedGradingRules.Create(manifest, answers);
        var source = JsonNode.Parse(manifest)!;
        var groups = new JsonArray();
        foreach (var item in source["groups"]!.AsArray().OrderBy(n => n!["groupId"]!.GetValue<string>(), StringComparer.Ordinal))
            groups.Add(new JsonObject
            {
                ["id"] = item!["groupId"]!.DeepClone(), ["subject"] = item["subjectId"]!.DeepClone(),
                ["type"] = item["type"]!.DeepClone(), ["maxScore"] = item["maxScore"]!.DeepClone(),
                ["scoring"] = item["scoring"]!.DeepClone()
            });
        var subjects = new JsonArray();
        foreach (var item in source["subjects"]!.AsArray().OrderBy(n => n!["subjectId"]!.GetValue<string>(), StringComparer.Ordinal))
            subjects.Add(new JsonObject { ["id"] = item!["subjectId"]!.DeepClone(), ["maxScore"] = item["maxScore"]!.DeepClone() });
        var keys = new JsonArray();
        foreach (var item in JsonNode.Parse(answers)!["answers"]!.AsArray().OrderBy(n => n!["questionId"]!.GetValue<string>(), StringComparer.Ordinal))
            keys.Add(new JsonObject
            {
                ["id"] = item!["questionId"]!.DeepClone(),
                ["correct"] = new JsonArray(item["correctOptionIds"]!.AsArray().Select(n => n!.GetValue<string>())
                    .OrderBy(value => value, StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            });
        return new JsonObject { ["groups"] = groups, ["subjects"] = subjects, ["answers"] = keys };
    }
}
