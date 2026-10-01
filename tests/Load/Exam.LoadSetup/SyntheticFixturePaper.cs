using System.IO.Compression;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;

internal static class SyntheticFixturePaper
{
    public static (byte[] Bytes, PaperStructure Structure, string ManifestJson, string AnswerKeyJson) Build(string examId)
    {
        var options = new[] { "A", "B", "C", "D" };
        var questions = Enumerable.Range(1, 120).Select(q => new ManifestQuestion("q" + q, q,
            "subject", "group", $"questions/合成负载考试_{q}.tex", options, null)).ToArray();
        var manifest = new ExamManifest(1, examId, "合成负载考试", [new("subject", "合成科目", "120")],
            [new("group", "subject", "合成题组", "single", 1, 120, false, "120",
                new(new("1", "0", "0"), null))], questions, []);
        var structure = new PaperStructure(questions.Select(q => new PaperQuestionStructure(q.QuestionId,
            q.QuestionNo, q.SubjectId, q.GroupId, "single", options)).ToArray());
        var display = new StudentPaperManifest(1, examId, manifest.ExamName, 1,
            [new("subject", "合成科目")], [new("group", "subject", "合成题组", 1)],
            structure.Questions.Select(q => new StudentPaperQuestion(q, $"questions/{q.QuestionId}.pdf", options)).ToArray(), []);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var metadata = zip.CreateEntry(StudentPaperPackage.ManifestPath).Open())
                JsonSerializer.Serialize(metadata, display, ImportJson.Create());
            foreach (var question in display.Questions)
            {
                using var pdf = zip.CreateEntry(question.PdfPath).Open();
                pdf.Write("%PDF-1.4\n% synthetic load fixture only\n%%EOF"u8);
            }
        }
        var bytes = output.ToArray();
        _ = StudentPaperPackage.ReadManifest(bytes, examId, 1);
        var key = new AnswerKeyFile(1, examId, questions.Select(q => new AnswerKeyEntry(q.QuestionId,
            q.QuestionNo, [options[q.QuestionNo % 4]])).ToArray());
        return (bytes, structure, JsonSerializer.Serialize(manifest, ImportJson.Create()), JsonSerializer.Serialize(key, ImportJson.Create()));
    }
}
