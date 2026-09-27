using System.IO.Compression;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;

namespace Exam.IntegrationTests.ServiceManagement;

internal static class SyntheticRecoveryPaper
{
    public static PaperStructure Structure(string questionId = "q1") => new([new(questionId, 1, "subject", "group", "single", ["A", "B"])]);
    public static byte[] Build(string examId = "synthetic-exam", long version = 1, string questionId = "q1",
        bool omitPdf = false, bool invalidPdf = false, string? extraPath = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new StudentPaperManifest(1, examId, "合成恢复测试", version,
                [new("subject", "合成科目")], [new("group", "subject", "合成题组", 1)],
                [new(Structure(questionId).Questions[0], "questions/synthetic.pdf", ["合成选项一", "合成选项二"])], []);
            using (var metadata = zip.CreateEntry(StudentPaperPackage.ManifestPath).Open())
                JsonSerializer.Serialize(metadata, manifest, ImportJson.Create());
            if (!omitPdf)
                using (var pdf = zip.CreateEntry("questions/synthetic.pdf").Open())
                    pdf.Write(invalidPdf ? "invalid synthetic display"u8 : "%PDF-1.4\n% synthetic recovery fixture only\n%%EOF"u8);
            if (extraPath is not null)
                using (var extra = zip.CreateEntry(extraPath).Open()) extra.Write("synthetic extra entry"u8);
        }
        return output.ToArray();
    }
}
