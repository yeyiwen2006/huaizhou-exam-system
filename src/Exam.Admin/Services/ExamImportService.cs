using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Administration;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Contracts.Common;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Admin.Services;

public sealed record ImportPreviewQuestion(string QuestionId, int QuestionNo, string Subject,
    string Group, string Type, string Score, string Options, string PdfPath,
    string CorrectOptions = "", int PageCount = 0, string RegionPages = "");

/// <summary>The checked source and compiled PDFs belong to this one preview; a new selection disposes them.</summary>
public sealed class PreparedExamImport : IDisposable
{
    internal PreparedExamImport(ExamImportWorkspace workspace, ExamManifest manifest, string manifestJson,
        string answerJson, ExamVersionSnapshot expected, IReadOnlyDictionary<string, TeXCompileResult> compiled,
        IReadOnlyList<ImportPreviewQuestion> questions, string source)
    {
        Workspace = workspace; Manifest = manifest; ManifestJson = manifestJson; AnswerJson = answerJson;
        Expected = expected; Compiled = compiled; Questions = questions; Source = source;
        ResourceId = "c05-" + Guid.NewGuid().ToString("N");
        RequestId = Guid.NewGuid().ToString("N");
    }

    internal ExamImportWorkspace Workspace { get; }
    internal ExamManifest Manifest { get; }
    internal string ManifestJson { get; }
    internal string AnswerJson { get; }
    internal ExamVersionSnapshot Expected { get; }
    internal IReadOnlyDictionary<string, TeXCompileResult> Compiled { get; }
    internal string ResourceId { get; }
    internal string RequestId { get; }
    internal bool IsDisposed { get; private set; }
    internal UploadExamBundleRequest? Upload { get; set; }
    public string Source { get; }
    public IReadOnlyList<ImportPreviewQuestion> Questions { get; }
    public long TargetPaperVersion => Expected.Versions.PaperVersion + 1;
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; Workspace.Dispose(); }
}

public interface IExamImportService
{
    Task<PreparedExamImport> PrepareAsync(string source, string examId, CancellationToken ct = default);
    Task<ExamVersionSnapshot> PublishAsync(PreparedExamImport prepared, CancellationToken ct = default);
}

public sealed class ExamImportService(AdminApiClient api) : IExamImportService
{
    public async Task<PreparedExamImport> PrepareAsync(string source, string examId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        var expected = await api.GetExamAsync(examId, ct);
        var selectedExam = await api.GetExamDetailAsync(examId, ct);
        if (selectedExam.Summary.ExamId != expected.ExamId)
            throw new InvalidDataException("所选考试与服务器版本信息不一致，请重新打开考试。");
        // The current C05 increment is first publication only. In-exam revisions are a separate task.
        RequireFirstPublication(expected);
        var workspace = await ExamImportWorkspace.CreateAsync(source, ct);
        try
        {
            var manifestPath = Path.Combine(workspace.PackageRoot, "manifest.json");
            var answerPath = Path.Combine(workspace.PackageRoot, "server-only", "answer-key.json");
            var manifestJson = await ExamImportWorkspace.ReadUtf8Async(manifestPath, ct);
            var answerJson = await ExamImportWorkspace.ReadUtf8Async(answerPath, ct);
            (manifestJson, answerJson) = await Task.Run(() => BindPackageToSelectedExam(
                manifestJson, answerJson, examId, selectedExam.Summary.Name), ct);
            await File.WriteAllTextAsync(manifestPath, manifestJson, ct);
            await File.WriteAllTextAsync(answerPath, answerJson, ct);
            var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())
                ?? throw new InvalidDataException("试题清单无效。");
            var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive)
                ?? throw new InvalidOperationException("未找到 XeLaTeX；需要在考务主机安装 TeX 发行版才能检查与预览。");
            var runner = new ExternalProcessRunner();
            var version = await runner.RunAsync(toolchain.ExecutablePath, ["--version"],
                new Dictionary<string, string>(), TimeSpan.FromSeconds(10), ct);
            var platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
                ? XeLaTeXPlatform.MiKTeX : XeLaTeXPlatform.TeXLive;
            var compiler = new XeLaTeXCompiler(runner, toolchain with { Platform = platform }, TimeSpan.FromMinutes(3));
            Directory.CreateDirectory(workspace.CompilationRoot);
            var compiled = new Dictionary<string, TeXCompileResult>(StringComparer.Ordinal);
            foreach (var question in manifest.Questions)
            {
                ct.ThrowIfCancellationRequested();
                var result = await compiler.CompileAsync(new TeXCompileRequest(workspace.PackageRoot,
                    question.Source, workspace.CompilationRoot), ct);
                if (!result.Succeeded)
                    throw new InvalidDataException($"题目 {question.QuestionId} 编译失败：" +
                        string.Join("；", result.Issues.Select(issue => issue.Code + " " + issue.Message)));
                compiled.Add(question.QuestionId, result);
            }
            var regions = await Task.Run(() => new OptionRegionExtractor().Extract(new OptionRegionRequest(examId,
                expected.Versions.PaperVersion + 1, manifest.Questions.Select(q =>
                    new OptionRegionSourceQuestion(q.QuestionId, compiled[q.QuestionId].OutputPdfPath!, q.Options!)).ToArray())), ct);
            if (!regions.Succeeded)
                throw new InvalidDataException("选项定位检查失败：" + string.Join("；",
                    regions.Issues.Select(issue => issue.QuestionId + " " + issue.Code + " " + issue.Message)));
            var subjects = manifest.Subjects.ToDictionary(x => x.SubjectId, x => x.Name, StringComparer.Ordinal);
            var groups = manifest.Groups.ToDictionary(x => x.GroupId, x => x, StringComparer.Ordinal);
            var answers = JsonSerializer.Deserialize<AnswerKeyFile>(answerJson, ImportJson.Create())!
                .Answers.ToDictionary(x => x.QuestionId, StringComparer.Ordinal);
            var regionPages = regions.Artifact!.Questions.ToDictionary(x => x.QuestionId,
                x => string.Join("、", x.Options.Select(o => o.OptionId + ":" + (o.PageIndex + 1) + "页")),
                StringComparer.Ordinal);
            var preview = manifest.Questions.Select(q => new ImportPreviewQuestion(q.QuestionId, q.QuestionNo,
                subjects[q.SubjectId], groups[q.GroupId].Name, groups[q.GroupId].Type ?? "",
                QuestionFullScore(groups[q.GroupId], answers[q.QuestionId].CorrectOptionIds!.Count),
                string.Join("、", q.Options!), compiled[q.QuestionId].OutputPdfPath!,
                string.Join("、", answers[q.QuestionId].CorrectOptionIds!),
                compiled[q.QuestionId].Report.PageCount, regionPages[q.QuestionId])).ToArray();
            return new PreparedExamImport(workspace, manifest, manifestJson, answerJson, expected, compiled, preview, source);
        }
        catch { workspace.Dispose(); throw; }
    }

    internal static void RequireFirstPublication(ExamVersionSnapshot expected)
    {
        if (expected.LifecycleState != ExamLifecycle.Scheduled)
            throw new InvalidOperationException("该考试已经开始或结束；本轮不支持考试中导入或修改试卷。");
        if (expected.Versions.PaperVersion != 0 || expected.Paper is not null)
            throw new InvalidOperationException("该考试已经发布过试卷；本轮暂不支持覆盖或考试中修改。");
    }

    internal static (string Manifest, string Answers) BindPackageToSelectedExam(string manifestJson,
        string answerJson, string selectedExamId, string selectedExamName)
    {
        // Validate the untouched source first: mismatched source answers cannot be made valid by rebinding.
        ImportedGradingRules.Create(manifestJson, answerJson);
        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())!;
        if (!StringComparer.Ordinal.Equals(manifest.ExamName.Trim(), selectedExamName.Trim()))
            throw new InvalidDataException("试题包考试名称与所选考试不一致，拒绝导入。");
        var sourceManifest = JsonNode.Parse(manifestJson)?.AsObject()
            ?? throw new InvalidDataException("试题清单无效。");
        var sourceAnswers = JsonNode.Parse(answerJson)?.AsObject()
            ?? throw new InvalidDataException("独立参考答案无效。");
        sourceManifest["examId"] = selectedExamId;
        sourceAnswers["examId"] = selectedExamId;
        var reboundManifest = sourceManifest.ToJsonString();
        var reboundAnswers = sourceAnswers.ToJsonString();
        if (ImportedGradingRules.Create(reboundManifest, reboundAnswers).ExamId != selectedExamId)
            throw new InvalidDataException("试题包无法绑定到所选考试。");
        return (reboundManifest, reboundAnswers);
    }

    internal static string QuestionFullScore(ManifestGroup group, int correctCount)
    {
        var expression = correctCount == 1
            ? group.Scoring?.OneCorrect?.Correct : group.Scoring?.MultipleCorrect?.AllCorrect;
        var checkedExpression = ScoreExpressionParser.Check(expression);
        if (checkedExpression.Root is null)
            throw new InvalidDataException("逐题满分表达式无效。");
        var score = ScoreExpressionParser.Evaluate(checkedExpression.Root, correctCount, correctCount);
        if (!score.TryFormatFiniteDecimal(out var text))
            throw new InvalidDataException("逐题满分不是有限小数。");
        return text;
    }

    public async Task<ExamVersionSnapshot> PublishAsync(PreparedExamImport prepared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.IsDisposed) throw new InvalidOperationException("预览已过期，请重新检查试题包。");
        var current = await api.GetExamAsync(prepared.Expected.ExamId, ct);
        if (prepared.Upload is not null && current.Paper?.ArtifactId == prepared.ResourceId &&
            current.Versions.PaperVersion == prepared.TargetPaperVersion &&
            current.Versions.GradingVersion > prepared.Expected.Versions.GradingVersion)
            return current; // The original request committed but its reply was lost.
        RequireFirstPublication(current);
        if (current.Versions != prepared.Expected.Versions)
            throw new InvalidOperationException("检查后考试版本已变化；请重新检查试题包，不能用旧预览发布。");
        if (prepared.Upload is null)
        {
            var recipients = await api.GetPublicationRecipientsAsync(ct);
            if (recipients.Recipients.Count == 0) throw new InvalidOperationException("服务器未提供发布证书。");
            var certificates = recipients.Recipients.Select(r => new NodeKeyRecipient(r.NodeId,
                X509CertificateLoader.LoadCertificate(Convert.FromBase64String(r.CertificateDerBase64)))).ToArray();
            try
            {
                var requestForPackage = new StudentPackageRequest(prepared.Workspace.PackageRoot,
                        prepared.Workspace.CompilationRoot, prepared.Workspace.StudentRoot, prepared.ResourceId,
                        prepared.TargetPaperVersion, prepared.Workspace.EncryptedPath, certificates)
                    { CompiledQuestions = prepared.Compiled };
                var result = await Task.Run(() => new StudentPackagePublisher(new PackageEncryptionService())
                    .PublishAsync(prepared.Manifest, requestForPackage, ct), ct);
                if (!result.Succeeded || result.Envelope is null || result.EncryptedPackagePath is null)
                    throw new InvalidDataException("学生包生成失败：" + string.Join("；", result.Issues.Select(i => i.Message)));
                var request = new PublishExamBundleApiRequest(prepared.RequestId,
                    prepared.Expected.Versions.PaperVersion, prepared.Expected.Versions.GradingVersion,
                    prepared.ResourceId, prepared.ManifestJson, prepared.AnswerJson,
                    prepared.Manifest.Questions.Select(q => q.QuestionId).ToArray(), "首次导入并发布整套试卷", true);
                var e = result.Envelope;
                prepared.Upload = new UploadExamBundleRequest(request, new PublicationPackageEnvelope(e.PackageId,
                    e.PaperVersion, e.ChunkSize, e.NoncePrefix, e.WrappedKeys, e.CertificateThumbprints));
            }
            finally { foreach (var item in certificates) item.Certificate.Dispose(); }
        }
        return await api.UploadPublicationAsync(prepared.Expected.ExamId, prepared.Upload,
            prepared.Workspace.EncryptedPath, ct);
    }
}
