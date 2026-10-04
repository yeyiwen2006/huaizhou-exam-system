using System.Text;
using System.Text.Json.Nodes;
using Exam.Contracts.Grading;
using Exam.Infrastructure.Packaging;

namespace Exam.UnitTests.Packaging;

public sealed class PaperRevisionMaterialsTests
{
    private const string ExamId = "synthetic-rule-materials";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CurrentRulesPreserveOptionalGroupTotalWithoutChangingArchivedSource(bool previousTotal, bool currentTotal)
    {
        var before = Manifest(1, previousTotal);
        var after = Manifest(2, currentTotal);
        var oldAnswers = Answers("A");
        var newAnswers = Answers("B");
        var source = Encoding.UTF8.GetBytes("% synthetic TeX source; never altered by grading updates");
        var archive = PaperMaterialsArchive.Build(ExamId, 1, new Dictionary<string, byte[]>
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(before),
            ["server-only/answer-key.json"] = Encoding.UTF8.GetBytes(oldAnswers),
            ["questions/合成规则材料_1.tex"] = source
        });
        var originalBytes = archive.ToArray();
        var current = new GradingMaterialSnapshot(ExamId, 1, 2, "current-rules", after, newAnswers);
        var working = PaperMaterialsArchive.Read(PaperRevisionMaterials.WithCurrentRules(archive, current, ExamId, 1, 2));
        Assert.True(working.Succeeded);
        var result = Encoding.UTF8.GetString(working.Materials!.Read("manifest.json"));
        var parsed = JsonNode.Parse(result)!;
        Assert.Equal(currentTotal, parsed["groups"]![0]!.AsObject().ContainsKey("maxScore"));
        Assert.Equal("2", parsed["subjects"]![0]!["maxScore"]!.GetValue<string>());
        Assert.Equal("2", parsed["groups"]![0]!["scoring"]!["oneCorrect"]!["correct"]!.GetValue<string>());
        Assert.Equal(source, working.Materials.Read("questions/合成规则材料_1.tex"));
        Assert.Equal(newAnswers, Encoding.UTF8.GetString(working.Materials.Read("server-only/answer-key.json")));
        Assert.True(PaperRevisionMaterials.EquivalentGrading(result, newAnswers, after, newAnswers));
        Assert.False(PaperRevisionMaterials.EquivalentGrading(before, oldAnswers, after, newAnswers));
        Assert.Equal(originalBytes, archive);
        Assert.Equal(oldAnswers, Encoding.UTF8.GetString(PaperMaterialsArchive.Read(archive).Materials!.Read("server-only/answer-key.json")));
    }

    private static string Manifest(int score, bool declaredTotal)
    {
        var manifest = JsonNode.Parse("""
            {"schemaVersion":1,"examId":"synthetic-rule-materials","examName":"合成规则材料",
             "subjects":[{"subjectId":"s","name":"合成科目","maxScore":"1"}],
             "groups":[{"groupId":"g","subjectId":"s","name":"单选","type":"single","order":1,"questionCount":1,
               "scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
             "questions":[{"questionId":"q-001","questionNo":1,"subjectId":"s","groupId":"g",
               "source":"questions/合成规则材料_1.tex","options":["A","B"]}],"resourceRoots":[]}
            """)!;
        var value = score.ToString(System.Globalization.CultureInfo.InvariantCulture);
        manifest["subjects"]![0]!["maxScore"] = value;
        manifest["groups"]![0]!["scoring"]!["oneCorrect"]!["correct"] = value;
        if (declaredTotal) manifest["groups"]![0]!["maxScore"] = value;
        return manifest.ToJsonString();
    }

    private static string Answers(string option) => new JsonObject
    {
        ["schemaVersion"] = 1, ["examId"] = ExamId,
        ["answers"] = new JsonArray(new JsonObject
        { ["questionId"] = "q-001", ["questionNo"] = 1, ["correctOptionIds"] = new JsonArray(option) })
    }.ToJsonString();
}
