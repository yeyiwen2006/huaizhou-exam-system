using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Infrastructure.Packaging;

namespace Exam.IntegrationTests.Importing;

/// <summary>All contents, identities and certificates are synthetic. CompileFixtureAsync is not a XeLaTeX claim.</summary>
internal sealed class SyntheticImportFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "c05-synthetic-" + Guid.NewGuid().ToString("N"));
    public string Name { get; } = "合成导入考试" + Guid.NewGuid().ToString("N")[..8];
    public string Source => Path.Combine(Root, "source");
    public string ManifestPath => Path.Combine(Source, "manifest.json");
    public string AnswerPath => Path.Combine(Source, "server-only", "answer-key.json");
    public ExamSettings Settings => new([new("subject-current", "合成科目", "1",
        [new("group-current", "单项选择题", "single", 1, 1, "1", true, new("1", "0", "0"), null)])]);
    public ExamManifest Manifest => new(1, "source-exam", Name,
        [new("subject-source", "合成科目", "1")],
        [new("group-source", "subject-source", "单项选择题", "single", 1, 1, true, "1", new(new("1", "0", "0"), null))],
        [new("q1", 1, "subject-source", "group-source", $"questions/{Name}_1.tex", ["A", "B"], null)], []);
    public AnswerKeyFile Answers => new(1, "source-exam", [new("q1", 1, ["A"])]);

    public SyntheticImportFixture()
    {
        Directory.CreateDirectory(Path.Combine(Source, "questions"));
        Directory.CreateDirectory(Path.Combine(Source, "server-only"));
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, ImportJson.Create()), new UTF8Encoding(false));
        File.WriteAllText(AnswerPath, JsonSerializer.Serialize(Answers, ImportJson.Create()), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Source, "private-note.txt"), "synthetic-private-answer-note");
        File.WriteAllText(Path.Combine(Source, "questions", Name + "_1.tex"), """
            \documentclass[UTF8]{ctexart}
            \pagestyle{empty}
            \usepackage{hyperref}
            \hypersetup{hidelinks}
            \DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
            \begin{document}
            合成题：请选择一个选项。
            \begin{enumerate}
            \item[\optmark{q1}{A}{A.}] 合成选项甲
            \item[\optmark{q1}{B}{B.}] 合成选项乙
            \end{enumerate}
            \end{document}
            """, new UTF8Encoding(false));
    }

    public static Task<TeXCompileResult> CompileFixtureAsync(TeXCompileRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folder = Directory.CreateDirectory(Path.Combine(request.OutputDirectory, "fixture-" + Guid.NewGuid().ToString("N"))).FullName;
        var pdf = Path.Combine(folder, Path.GetFileNameWithoutExtension(request.SourceRelativePath) + ".pdf");
        File.WriteAllBytes(pdf, MarkedPdf());
        return Task.FromResult(new TeXCompileResult(true, 0, false, new(true, 1, Path.GetFileName(pdf), [], 0), pdf, []));
    }

    private static byte[] MarkedPdf()
    {
        // Deterministic PDF test double with the same hidden named-link contract as D03.
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(opt:q1:A) [3 0 R /XYZ 60 675 null] (opt:q1:B) [3 0 R /XYZ 60 635 null]] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R /Annots [5 0 R 6 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Link /Rect [60 660 90 675] /Border [0 0 0] /A << /S /GoTo /D (opt:q1:A) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [60 620 90 635] /Border [0 0 0] /A << /S /GoTo /D (opt:q1:B) >> >>"
        };
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        { offsets.Add(Encoding.ASCII.GetByteCount(text.ToString())); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}

internal sealed class SyntheticPublicationClient : IExamPublicationClient, IDisposable
{
    private readonly X509Certificate2 _certificate;
    public long Generation { get; set; }
    public string? ServerId { get; set; } = "synthetic-server";
    public ExamVersionSnapshot Current { get; set; }
    public ExamDetail Detail { get; set; }
    public int Reads { get; private set; }
    public int Uploads { get; private set; }
    public bool NoRecipients { get; set; }
    public List<UploadExamBundleRequest> Requests { get; } = [];
    public List<byte[]> Ciphertexts { get; } = [];

    /// <summary>源材料归档的密文（逐次上传）；明文归档不在表单里，这一点也要能被断言。</summary>
    public List<byte[]> MaterialsCiphertexts { get; } = [];
    public Func<ExamVersionSnapshot, Task<ExamVersionSnapshot>>? OnUpload { get; set; }
    public Action? OnDetail { get; set; }
    public SyntheticPublicationClient(SyntheticImportFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        Current = new("server-exam", new(1, 0, 0), new(now.AddHours(1), now.AddHours(2)),
            ExamLifecycle.Scheduled, null, null, null, null, new Dictionary<string, ParticipantVersionStatus>());
        Detail = new(new(Current.ExamId, fixture.Name, Current.Schedule.StartsAt, Current.Schedule.EndsAt,
            ExamLifecycle.Scheduled, false, false, 0, 0, 1), fixture.Settings, 1, []);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
    }
    public Task<ExamVersionSnapshot> GetExamAsync(string examId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Current); }
    public Task<ExamDetail> GetExamDetailAsync(string examId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); OnDetail?.Invoke(); return Task.FromResult(Detail); }
    public Task<PublicationRecipients> GetPublicationRecipientsAsync(CancellationToken ct = default) =>
        Task.FromResult(new PublicationRecipients(NoRecipients ? [] : [new("node-a", Convert.ToBase64String(_certificate.RawData))]));
    public async Task<ExamVersionSnapshot> UploadPublicationAsync(string examId, UploadExamBundleRequest upload,
        string encryptedPath, string materialsPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Uploads++;
        Requests.Add(upload);
        Ciphertexts.Add(await File.ReadAllBytesAsync(encryptedPath, ct));
        MaterialsCiphertexts.Add(await File.ReadAllBytesAsync(materialsPath, ct));
        var publication = upload.Publication;
        var structure = Exam.Domain.Scoring.ImportedGradingRules.Create(publication.ManifestJson, publication.AnswerKeyJson).Structure;
        Current = Current with
        {
            Versions = new(Current.Versions.ScheduleVersion, publication.ExpectedPaperVersion + 1, publication.ExpectedGradingVersion + 1),
            Paper = new(publication.ExpectedPaperVersion + 1, publication.ResourceId, structure, publication.ChangedQuestionIds,
                DateTimeOffset.UtcNow, "synthetic-operator", publication.Reason),
            Grading = new(publication.ExpectedGradingVersion + 1, "bundle-rules-" + publication.ResourceId,
                DateTimeOffset.UtcNow, "synthetic-operator", publication.Reason)
        };
        return OnUpload is null ? Current : await OnUpload(Current);
    }
    public void Dispose() => _certificate.Dispose();
}
