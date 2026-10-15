using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Administration;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Domain.Exams;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Packaging;

public sealed record ImportPreviewQuestion(string QuestionId, int QuestionNo, string Subject,
    string Group, string Type, string Score, string Options, string PdfPath,
    string CorrectOptions = "", int PageCount = 0, string RegionPages = "");

/// <summary>The checked source and compiled PDFs belong to this one preview; a new selection disposes them.</summary>
public sealed partial class PreparedExamImport : IDisposable
{
    internal PreparedExamImport(ExamImportWorkspace workspace, ExamManifest manifest, string manifestJson,
        string answerJson, ExamVersionSnapshot expected, IReadOnlyDictionary<string, TeXCompileResult> compiled,
        IReadOnlyList<ImportPreviewQuestion> questions, string source,
        string reason, IReadOnlyList<string> declaredChangedQuestions, IReadOnlyList<string> changedQuestionsHint)
    {
        Workspace = workspace; Manifest = manifest; ManifestJson = manifestJson; AnswerJson = answerJson;
        Expected = expected; Compiled = compiled; Questions = questions; Source = source;
        Reason = reason;
        DeclaredChangedQuestions = declaredChangedQuestions;
        ChangedQuestionsHint = changedQuestionsHint;
        ResourceId = "c05-" + Guid.NewGuid().ToString("N");
        RequestId = Guid.NewGuid().ToString("N");
    }

    internal ExamImportWorkspace Workspace { get; }
    internal ExamManifest Manifest { get; }
    internal string ManifestJson { get; }
    internal string AnswerJson { get; }
    internal ExamVersionSnapshot Expected { get; }
    internal IReadOnlyDictionary<string, TeXCompileResult> Compiled { get; }
    internal string ResourceId { get; private set; }
    internal string RequestId { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal UploadExamBundleRequest? Upload { get; set; }
    public string Source { get; }
    public IReadOnlyList<ImportPreviewQuestion> Questions { get; }

    /// <summary>本次发布记录的业务原因（首次导入用固定文案，修订用操作者填写的原因）。</summary>
    public string Reason { get; }

    /// <summary>
    /// 本次发布**声明**的变更题号：修订时由服务器按内容核对，必须与实际变更集完全一致；
    /// 首次发布是整卷（服务端按整卷都是新题处理）。
    /// </summary>
    public IReadOnlyList<string> DeclaredChangedQuestions { get; }

    /// <summary>
    /// 修订预览时按内容算出的差异**提示**（用上一版本归档里的清单与材料逐题比对）：
    /// 只帮操作者确认，不代替服务器的核对。
    /// </summary>
    public IReadOnlyList<string> ChangedQuestionsHint { get; }

    /// <summary>是否属于开考后修订（相对首次导入）。</summary>
    public bool IsRevision => Expected.Versions.PaperVersion > 0;

    public long TargetPaperVersion => Expected.Versions.PaperVersion + 1;
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (PublicationGate.CurrentCount != 0) Workspace.Dispose();
    }

}

public interface IExamImportService
{
    /// <summary>
    /// 检查材料并生成预览。开考后修订要给出原因；声明的变更题号省略时采用按内容算出的差异提示
    /// （服务器仍会按内容核对，不一致即拒绝）。
    /// </summary>
    Task<PreparedExamImport> PrepareAsync(string source, string examId, CancellationToken ct = default,
        string? reason = null, IReadOnlyList<string>? changedQuestions = null);

    Task<PreparedExamImport> PrepareAsync(string source, string examId, CancellationToken ct,
        string? reason, IReadOnlyList<string>? changedQuestions, IProgress<string>? progress) =>
        PrepareAsync(source, examId, ct, reason, changedQuestions);

    Task<ExamVersionSnapshot> PublishAsync(PreparedExamImport prepared, CancellationToken ct = default);
}

public class ImportPipeline(IExamPublicationClient api) : IExamImportService
{
    // Deterministic contract tests may substitute compilation; production constructors always use XeLaTeX.
    internal Func<TeXCompileRequest, CancellationToken, Task<TeXCompileResult>>? CompileOverride { get; init; }
    /// <param name="reason">本次发布记录的原因：首次导入可省略（用固定文案），开考后修订必须填写。</param>
    /// <param name="changedQuestions">
    /// 开考后修订**声明**的变更题号。省略时采用按内容算出的差异提示；服务器会按内容核对，不一致即拒绝。
    /// </param>
    public Task<PreparedExamImport> PrepareAsync(string source, string examId, CancellationToken ct = default,
        string? reason = null, IReadOnlyList<string>? changedQuestions = null) =>
        PrepareAsync(source, examId, ct, reason, changedQuestions, null);

    public async Task<PreparedExamImport> PrepareAsync(string source, string examId, CancellationToken ct,
        string? reason, IReadOnlyList<string>? changedQuestions, IProgress<string>? progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        progress?.Report("正在读取考试设置并检查试题材料…");
        var generation = api.Generation;
        var expected = await api.GetExamAsync(examId, ct);
        var selectedExam = await api.GetExamDetailAsync(examId, ct);
        if (selectedExam.Summary.ExamId != expected.ExamId)
            throw new InvalidDataException("所选考试与服务器版本信息不一致，请重新打开考试。");
        var revision = expected.Versions.PaperVersion > 0;
        if (revision)
        {
            // 开考后修订：试卷必须已发布（结构不变由服务器在发布事务内判定），修订从上一版本材料出发。
            if (expected.Paper is null || expected.Grading is null)
                throw new InvalidOperationException("服务器上的试卷或规则状态不完整，请重新打开考试后再检查材料。");
        }
        else
        {
            RequireFirstPublication(expected);
        }
        EnsureConnection(generation, api.ServerId);
        var workspace = await ExamImportWorkspace.CreateAsync(source, ct);
        try
        {
            var manifestPath = Path.Combine(workspace.PackageRoot, "manifest.json");
            var answerPath = Path.Combine(workspace.PackageRoot, "server-only", "answer-key.json");
            var manifestJson = await ExamImportWorkspace.ReadUtf8Async(manifestPath, ct);
            var answerJson = await ExamImportWorkspace.ReadUtf8Async(answerPath, ct);
            (manifestJson, answerJson) = await Task.Run(() => BindPackageToSelectedExam(
                manifestJson, answerJson, examId, selectedExam.Summary.Name), ct);
            if (!revision) manifestJson = ImportCatalogBinding.Bind(manifestJson, answerJson, selectedExam.Settings);
            else if (!ImportedGradingRules.Create(manifestJson, answerJson).Matches(expected.Paper!.Structure))
                throw new InvalidDataException("修订材料必须保持已发布试卷的题目与选项结构。");
            await File.WriteAllTextAsync(manifestPath, manifestJson, ct);
            await File.WriteAllTextAsync(answerPath, answerJson, ct);
            var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())
                ?? throw new InvalidDataException("试题清单无效。");
            var compile = CompileOverride;
            if (compile is null)
            {
                var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive)
                    ?? throw new InvalidOperationException("未找到 XeLaTeX；需要在考务主机安装 TeX 发行版才能检查与预览。");
                var runner = new ExternalProcessRunner();
                var version = await runner.RunAsync(toolchain.ExecutablePath, ["--version"],
                    new Dictionary<string, string>(), TimeSpan.FromSeconds(10), ct);
                var platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
                    ? XeLaTeXPlatform.MiKTeX : XeLaTeXPlatform.TeXLive;
                var compiler = new XeLaTeXCompiler(runner, toolchain with { Platform = platform }, TimeSpan.FromMinutes(3));
                compile = compiler.CompileAsync;
            }
            Directory.CreateDirectory(workspace.CompilationRoot);
            var compiled = new Dictionary<string, TeXCompileResult>(StringComparer.Ordinal);
            foreach (var question in manifest.Questions.OrderBy(q => q.QuestionNo))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"正在编译第 {compiled.Count + 1}/{manifest.Questions.Count} 题（已完成 {compiled.Count} 题）…");
                var result = await compile(new TeXCompileRequest(workspace.PackageRoot,
                    question.Source, workspace.CompilationRoot), ct);
                if (!result.Succeeded)
                    throw new InvalidDataException($"第 {question.QuestionNo} 题编译失败：" +
                        string.Join("；", result.Issues.Select(issue => issue.Message +
                            (issue.File is null ? "" : "（" + issue.File + (issue.Line is null ? "" : $"，第 {issue.Line} 行") + "）"))));
                compiled.Add(question.QuestionId, result);
                progress?.Report($"已完成编译 {compiled.Count}/{manifest.Questions.Count} 题…");
            }
            progress?.Report($"已完成编译 {compiled.Count}/{manifest.Questions.Count} 题，正在核对最新考试设置…");
            // Compilation is the expensive part. Saving the same exam must not cancel it.
            // Rebind the compiled material to the latest compatible settings before previewing.
            var latest = await api.GetExamDetailAsync(examId, ct);
            var latestVersion = await api.GetExamAsync(examId, ct);
            EnsureConnection(generation, api.ServerId);
            if (latest.Summary.ExamId != examId || latestVersion.ExamId != examId ||
                latestVersion.Versions.PaperVersion != expected.Versions.PaperVersion ||
                latestVersion.Versions.GradingVersion != expected.Versions.GradingVersion)
                throw new InvalidOperationException("检查期间试卷或计分规则已被发布，请重新读取当前版本。");
            if (!revision)
            {
                RequireFirstPublication(latestVersion);
                var refreshed = manifest with { ExamName = latest.Summary.Name };
                if (!StringComparer.Ordinal.Equals(manifest.ExamName, refreshed.ExamName))
                {
                    refreshed = refreshed with { Questions = manifest.Questions.Select(question => question with
                    { Source = (Path.GetDirectoryName(question.Source)?.Replace('\\', '/') + "/" + refreshed.ExamName + "_" + question.QuestionNo + ".tex").TrimStart('/') }).ToArray() };
                    // Validate filenames before moving only this import's private source copies.
                    ImportedGradingRules.Create(JsonSerializer.Serialize(refreshed, ImportJson.Create()), answerJson);
                    foreach (var (oldQuestion, newQuestion) in manifest.Questions.Zip(refreshed.Questions))
                    {
                        File.Move(Path.Combine(workspace.PackageRoot, oldQuestion.Source), Path.Combine(workspace.PackageRoot, newQuestion.Source));
                        var result = compiled[oldQuestion.QuestionId];
                        var pdfName = Path.GetFileNameWithoutExtension(newQuestion.Source) + ".pdf";
                        var pdfPath = Path.Combine(Path.GetDirectoryName(result.OutputPdfPath!)!, pdfName);
                        File.Move(result.OutputPdfPath!, pdfPath);
                        compiled[oldQuestion.QuestionId] = result with { OutputPdfPath = pdfPath,
                            Report = result.Report with { OutputFileName = pdfName } };
                    }
                }
                manifestJson = JsonSerializer.Serialize(refreshed, ImportJson.Create());
                manifestJson = ImportCatalogBinding.Bind(manifestJson, answerJson, latest.Settings);
                await File.WriteAllTextAsync(manifestPath, manifestJson, ct);
                await File.WriteAllTextAsync(answerPath, answerJson, ct);
                manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())!;
            }
            expected = latestVersion;
            selectedExam = latest;
            progress?.Report($"已完成编译 {compiled.Count}/{manifest.Questions.Count} 题，正在检查选项定位…");
            var regions = await Task.Run(() => new OptionRegionExtractor().Extract(new OptionRegionRequest(examId,
                expected.Versions.PaperVersion + 1, manifest.Questions.Select(q =>
                    new OptionRegionSourceQuestion(q.QuestionId, compiled[q.QuestionId].OutputPdfPath!, q.Options!)).ToArray())), ct);
            if (!regions.Succeeded)
                throw new InvalidDataException("选项定位检查失败：" + string.Join("；",
                    regions.Issues.Select(issue => "第 " + manifest.Questions.FirstOrDefault(q => q.QuestionId == issue.QuestionId)?.QuestionNo + " 题：" + issue.Message)));
            var subjects = manifest.Subjects.ToDictionary(x => x.SubjectId, x => x.Name, StringComparer.Ordinal);
            var groups = manifest.Groups.ToDictionary(x => x.GroupId, x => x, StringComparer.Ordinal);
            var answers = JsonSerializer.Deserialize<AnswerKeyFile>(answerJson, ImportJson.Create())!
                .Answers.ToDictionary(x => x.QuestionId, StringComparer.Ordinal);
            var regionPages = regions.Artifact!.Questions.ToDictionary(x => x.QuestionId,
                x => string.Join("、", x.Options.Select(o => o.OptionId + ":" + (o.PageIndex + 1) + "页")),
                StringComparer.Ordinal);
            var preview = manifest.Questions.OrderBy(q => q.QuestionNo).Select(q => new ImportPreviewQuestion(q.QuestionId, q.QuestionNo,
                subjects[q.SubjectId], groups[q.GroupId].Name, QuestionTypeText(groups[q.GroupId].Type),
                QuestionFullScore(groups[q.GroupId], answers[q.QuestionId].CorrectOptionIds!.Count),
                string.Join("、", q.Options!), compiled[q.QuestionId].OutputPdfPath!,
                string.Join("、", answers[q.QuestionId].CorrectOptionIds!),
                compiled[q.QuestionId].Report.PageCount, regionPages[q.QuestionId])).ToArray();
            EnsureConnection(generation, api.ServerId);
            var hint = revision
                ? await ChangedQuestionsHintAsync(examId, expected.Versions.PaperVersion, manifest, workspace, generation, ct)
                : [];
            if (revision && hint.Count == 0)
                throw new InvalidDataException("本次材料与上一已发布版本没有内容差异：只改答案或计分规则请使用规则修订入口。");
            var declared = revision
                ? ResolveDeclaredChangedQuestions(changedQuestions, hint, manifest)
                : manifest.Questions.OrderBy(question => question.QuestionNo)
                    .Select(question => question.QuestionId).ToArray();
            string publishReason;
            if (revision && string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("开考后修订必须填写本次修改备注。");
            publishReason = string.IsNullOrWhiteSpace(reason) ? FirstImportReason : reason!;
            var prepared = new PreparedExamImport(workspace, manifest, manifestJson, answerJson, expected, compiled,
                preview, source, publishReason, declared, hint)
            { ConnectionGeneration = generation, LogicalServerId = api.ServerId,
              ExpectedSettingsVersion = selectedExam.Summary.SettingsVersion };
            progress?.Report($"已完成编译 {compiled.Count}/{manifest.Questions.Count} 题，正在整理预览和源材料…");
            await prepared.SealAsync(ct);
            return prepared;
        }
        catch { workspace.Dispose(); throw; }
    }

    /// <summary>首次导入记录的原因（与服务端既有文案一致）。</summary>
    public const string FirstImportReason = "首次导入并发布整套试卷";

    /// <summary>
    /// 修订预览提示：取回上一已发布版本的源材料归档（内含那一版的清单），
    /// 用**同一算法**算出两版的逐题摘要并比对。只作提示——服务器会用同一算法核对声明，
    /// 且还会核对结构字段与显示元数据。
    /// </summary>
    private async Task<IReadOnlyList<string>> ChangedQuestionsHintAsync(string examId, long previousPaperVersion,
        ExamManifest manifest, ExamImportWorkspace workspace, long generation, CancellationToken ct)
    {
        var previous = await api.DownloadPaperMaterialsAsync(examId, previousPaperVersion, ct);
        EnsureConnection(generation, api.ServerId);
        var archived = PaperMaterialsArchive.Read(previous);
        if (!archived.Succeeded || archived.Materials is null)
            throw new InvalidDataException("服务器保存的上一版本源材料不可读，无法比较：" +
                (archived.Failure?.Value ?? "归档格式不符"));
        var manifestPath = archived.Materials.Resolve(StudentPackagePlan.MetadataFileName)
            ?? throw new InvalidDataException("上一版本归档里没有清单，无法比较。");
        var previousManifest = System.Text.Json.JsonSerializer.Deserialize<ExamManifest>(
            System.Text.Encoding.UTF8.GetString(archived.Materials.Read(manifestPath)), ImportJson.Create())
            ?? throw new InvalidDataException("上一版本归档里的清单无效。");
        var previousDigests = QuestionContentDigest.Compute(previousManifest, archived.Materials);
        var currentDigests = QuestionContentDigest.Compute(manifest,
            DirectoryMaterialFileSource.Create(workspace.PackageRoot));
        return QuestionContentDigest.Changed(previousDigests, currentDigests);
    }

    /// <summary>规范化操作者声明的变更题号；声明了清单之外的题号就早失败，不做猜测。</summary>
    private static IReadOnlyList<string> ResolveDeclaredChangedQuestions(IReadOnlyList<string>? declared,
        IReadOnlyList<string> hint, ExamManifest manifest)
    {
        if (declared is not { Count: > 0 })
        {
            return hint;
        }

        var normalized = declared.Select(questionId => questionId.Trim())
            .Where(questionId => questionId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var known = manifest.Questions.Select(question => question.QuestionId).ToHashSet(StringComparer.Ordinal);
        var unknown = normalized.Where(questionId => !known.Contains(questionId)).ToArray();
        if (unknown.Length > 0)
            throw new InvalidDataException("声明的变更题号不在本次材料清单中：" + string.Join("、", unknown));
        return normalized;
    }

    /// <summary>
    /// 开考后修订的前置：服务器仍停留在预览时那一版试卷，且本次**声明**了变更题号
    /// （声明由服务器按内容核对，这里只保证不为空——空声明会让所有实际变更都变成漏报）。
    /// </summary>
    public static void RequireRevision(ExamVersionSnapshot current, PreparedExamImport prepared)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(prepared);
        if (current.Paper is null || current.Versions.PaperVersion != prepared.Expected.Versions.PaperVersion)
            throw new InvalidOperationException("服务器试卷版本已变化，请重新检查材料，不能用旧预览发布修订。");
        if (prepared.DeclaredChangedQuestions.Count == 0)
            throw new InvalidOperationException("开考后修订必须声明变更题号；请重新检查材料并确认变更题号。");
    }

    public static void RequireFirstPublication(ExamVersionSnapshot expected)
    {
        if (expected.LifecycleState != ExamLifecycle.Scheduled)
            throw new InvalidOperationException("考试已开始或结束，首次导入必须在开考前完成。");
        if (expected.Versions.PaperVersion != 0 || expected.Paper is not null ||
            expected.Versions.GradingVersion != 0 || expected.Grading is not null)
            throw new InvalidOperationException("这场考试已有正式试卷，请使用版本修改流程，不能再次首次导入。");
    }

    public static (string Manifest, string Answers) BindPackageToSelectedExam(string manifestJson,
        string answerJson, string selectedExamId, string selectedExamName)
    {
        // Validate the untouched source first: mismatched source answers cannot be made valid by rebinding.
        ImportedGradingRules.Create(manifestJson, answerJson);
        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())!;
        if (ExamSettingsRules.NameKey(manifest.ExamName) != ExamSettingsRules.NameKey(selectedExamName))
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

    public static string QuestionFullScore(ManifestGroup group, int correctCount)
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

    public static string QuestionTypeText(string? type) => type switch
    {
        "single" => "单项选择题", "multiple" => "多项选择题", "indeterminate" => "不定项选择题",
        _ => throw new InvalidDataException("题型无效。")
    };

    /// <summary>
    /// 用**同一组受管公钥**加密源材料归档。归档里含独立答案，明文不得进入表单临时缓冲；
    /// 服务器解出后按既有惯例以明文入库（与版本事件里已明文保存的答案 JSON 一致）。
    /// </summary>
    private static async Task<PublicationPackageEnvelope> EncryptMaterialsAsync(PackageEncryptionService encryption,
        PreparedExamImport prepared, IReadOnlyList<NodeKeyRecipient> recipients, CancellationToken ct)
    {
        await using var plaintext = new FileStream(prepared.MaterialsArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var ciphertext = new FileStream(prepared.MaterialsCiphertextPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var envelope = await encryption.EncryptAsync(plaintext, ciphertext, prepared.ResourceId + "-materials",
            prepared.TargetPaperVersion, recipients, ct);
        return new PublicationPackageEnvelope(envelope.PackageId, envelope.PaperVersion, envelope.ChunkSize,
            envelope.NoncePrefix, envelope.WrappedKeys, envelope.CertificateThumbprints);
    }

    private void EnsureConnection(long generation, string? serverId)
    {
        if (api.Generation != generation || string.IsNullOrWhiteSpace(serverId) ||
            !StringComparer.Ordinal.Equals(serverId, api.ServerId))
            throw new InvalidOperationException("服务器连接已改变，请在原服务器核对结果并重新检查材料。");
    }

    public async Task<ExamVersionSnapshot> PublishAsync(PreparedExamImport prepared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.IsDisposed) throw new InvalidOperationException("预览已过期，请重新检查试题包。");
        await prepared.PublicationGate.WaitAsync(ct);
        try
        {
            if (prepared.IsDisposed) throw new ObjectDisposedException(nameof(PreparedExamImport));
            EnsureConnection(prepared.ConnectionGeneration, prepared.LogicalServerId);
            await prepared.VerifyUnchangedAsync(ct);
            if (prepared.Upload is null)
            {
                var current = await api.GetExamAsync(prepared.Expected.ExamId, ct);
                var detail = await api.GetExamDetailAsync(prepared.Expected.ExamId, ct);
                EnsureConnection(prepared.ConnectionGeneration, prepared.LogicalServerId);
                if (prepared.IsRevision)
                {
                    // 修订会推进版本，因此只要求服务器仍停留在预览时那一版试卷，且声明非空。
                    RequireRevision(current, prepared);
                }
                else
                {
                    RequireFirstPublication(current);
                    // A schedule change does not change the compiled paper. The server checks
                    // its latest saved schedule under the exam stream lock when publishing.
                }

                if (current.ExamId != prepared.Expected.ExamId ||
                    current.Versions.PaperVersion != prepared.Expected.Versions.PaperVersion ||
                    (prepared.IsRevision && detail.Summary.SettingsVersion != prepared.ExpectedSettingsVersion))
                    throw new InvalidOperationException("考试版本或设置已变化，请重新检查试题包，不能用旧预览发布。");
                if (!prepared.IsRevision)
                {
                    try { ImportCatalogBinding.RequireMatch(prepared.ManifestJson, prepared.AnswerJson, detail.Summary.Name, detail.Settings); }
                    catch (ArgumentException error)
                    { throw new InvalidOperationException("考试名称、科目题组或计分设置已改变，请重新检查试题包。仅修改考试时间无需重新编译。", error); }
                }
                var recipients = await api.GetPublicationRecipientsAsync(ct);
                EnsureConnection(prepared.ConnectionGeneration, prepared.LogicalServerId);
                if (recipients.Recipients is not { Count: > 0 and <= 32 })
                    throw new InvalidOperationException("服务器没有提供有效的发布证书。");
                var certificates = new List<NodeKeyRecipient>();
                try
                {
                    var nodes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var recipient in recipients.Recipients)
                    {
                        if (string.IsNullOrWhiteSpace(recipient.NodeId) || !nodes.Add(recipient.NodeId) ||
                            recipient.CertificateDerBase64 is not { Length: > 0 and <= 131072 })
                            throw new InvalidDataException("服务器返回的发布证书列表无效。");
                        certificates.Add(new NodeKeyRecipient(recipient.NodeId,
                            X509CertificateLoader.LoadCertificate(Convert.FromBase64String(recipient.CertificateDerBase64))));
                    }
                    var requestForPackage = new StudentPackageRequest(prepared.Workspace.PackageRoot,
                        prepared.Workspace.CompilationRoot, prepared.Workspace.StudentRoot, prepared.ResourceId,
                        prepared.TargetPaperVersion, prepared.Workspace.EncryptedPath, certificates)
                    { CompiledQuestions = prepared.Compiled };
                    var encryption = new PackageEncryptionService();
                    var result = await Task.Run(() => new StudentPackagePublisher(encryption)
                        .PublishAsync(prepared.Manifest, requestForPackage, ct), ct);
                    if (!result.Succeeded || result.Envelope is null || result.EncryptedPackagePath is null)
                        throw new InvalidDataException("学生包生成失败：" + string.Join("；", result.Issues.Select(i => i.Message)));
                    await prepared.VerifyUnchangedAsync(ct);
                    // 源材料归档同样用受管公钥加密后再上传：临时缓冲里不能出现题目明文或独立答案。
                    await prepared.VerifyMaterialsAsync(ct);
                    var materialsEnvelope = await EncryptMaterialsAsync(encryption, prepared, certificates, ct);
                    // 声明集与原因在预览阶段定稿（发布只重放同一份已保存的请求）：
                    // 首次发布是整卷 + 固定文案；修订是操作者确认过的变更题号 + 操作者填写的原因。
                    var request = new PublishExamBundleApiRequest(prepared.RequestId,
                        prepared.Expected.Versions.PaperVersion, prepared.Expected.Versions.GradingVersion,
                        prepared.ResourceId, prepared.ManifestJson, prepared.AnswerJson,
                        prepared.DeclaredChangedQuestions.ToArray(), prepared.Reason,
                        true, prepared.MaterialsSha256 ?? string.Empty);
                    var e = result.Envelope;
                    prepared.Upload = new UploadExamBundleRequest(request, new PublicationPackageEnvelope(e.PackageId,
                        e.PaperVersion, e.ChunkSize, e.NoncePrefix, e.WrappedKeys, e.CertificateThumbprints),
                        materialsEnvelope);
                    await prepared.SaveUploadAsync(ct);
                }
                finally { foreach (var item in certificates) item.Certificate.Dispose(); }
            }
            // A visible local version is NOT a durable HA acknowledgement. An uncertain retry must replay
            // the exact upload through the existing ConfirmationBeforeResult gate, even after the exam starts.
            EnsureConnection(prepared.ConnectionGeneration, prepared.LogicalServerId);
            await prepared.SaveUploadAsync(ct);
            var published = await api.UploadPublicationAsync(prepared.Expected.ExamId, prepared.Upload,
                prepared.Workspace.EncryptedPath, prepared.MaterialsCiphertextPath, ct);
            EnsureConnection(prepared.ConnectionGeneration, prepared.LogicalServerId);
            if (!prepared.MatchesPublication(published))
                throw new InvalidDataException("服务器回执与本次考试、试卷和规则版本不一致，不能确认发布成功。");
            return published;
        }
        finally
        {
            prepared.PublicationGate.Release();
            if (prepared.IsDisposed) prepared.Workspace.Dispose();
        }
    }
}
