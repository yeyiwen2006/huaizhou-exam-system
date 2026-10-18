using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;
using Exam.Domain.Scoring;

namespace Exam.Infrastructure.Packaging;

public interface IGradingRevisionClient
{
    long Generation { get; }
    string? ServerId { get; }
    Task<ExamVersionSnapshot> GetExamAsync(string examId, CancellationToken ct = default);
    Task<string> GetGradingOperatorAsync(CancellationToken ct = default);
    Task<GradingMaterialSnapshot> GetGradingMaterialsAsync(string examId, CancellationToken ct = default);
    Task<ExamVersionSnapshot> SendGradingRevisionAsync(PublishGradingRulesRequest request, CancellationToken ct = default);
    Task<GradingStatusReport> GetGradingStatusAsync(string examId, CancellationToken ct = default);
}

/// <summary>One checked, immutable rule change. Retries never refresh its expected version or request ID.</summary>
public sealed class PreparedGradingRevision : IDisposable
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private readonly Metadata _metadata;
    private FileStream? _lease;
    private string? _savedDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private PreparedGradingRevision(Metadata metadata, string manifest, string answers)
    {
        _metadata = metadata;
        if (string.IsNullOrWhiteSpace(metadata.ServerId) || string.IsNullOrWhiteSpace(metadata.Operator) ||
            string.IsNullOrWhiteSpace(metadata.ExamId) || metadata.PaperVersion < 1 || metadata.GradingVersion is < 1 or long.MaxValue ||
            metadata.Reason is not { Length: > 0 and <= 1000 } || string.IsNullOrWhiteSpace(metadata.Reason) ||
            metadata.RequestId is not { Length: > 0 and <= 128 } || metadata.Structure is null ||
            Hash(manifest) != metadata.ManifestHash || Hash(answers) != metadata.AnswerHash ||
            metadata.RuleSetId != RuleSetId(manifest, answers))
            throw new InvalidDataException("规则预览的身份、版本或内容校验值无效。");
        var rules = ImportedGradingRules.Create(manifest, answers);
        if (rules.ExamId != metadata.ExamId || !rules.Matches(metadata.Structure))
            throw new InvalidDataException("答案与计分规则必须属于本场已发布试卷，且不能改变题目、选项或题型结构；请先取回本场源材料。");
        Request = new(metadata.ExamId, metadata.GradingVersion, metadata.RuleSetId, new(true, true, true),
            metadata.Operator, metadata.Reason, DateTimeOffset.UnixEpoch, metadata.RequestId, manifest, answers);
        var parsed = JsonSerializer.Deserialize<ExamManifest>(manifest, ImportJson.Create())!;
        var groups = parsed.Groups.ToDictionary(g => g.GroupId, StringComparer.Ordinal);
        var keys = JsonSerializer.Deserialize<AnswerKeyFile>(answers, ImportJson.Create())!.Answers
            .ToDictionary(a => a.QuestionId, StringComparer.Ordinal);
        Preview = Array.AsReadOnly(parsed.Questions.OrderBy(q => q.QuestionNo).Select(q =>
            new GradingRevisionQuestion(q.QuestionNo, q.QuestionId, string.Join("、", q.Options!
                .Select((id, index) => (id, index))
                .Where(option => keys[q.QuestionId].CorrectOptionIds!.Contains(option.id, StringComparer.Ordinal))
                .Select(option => q.DisplayMetadata?.OptionCaptions is { } captions ? captions[option.index] : option.id)),
                ImportPipeline.QuestionFullScore(groups[q.GroupId], keys[q.QuestionId].CorrectOptionIds!.Count))).ToArray());
    }

    public PublishGradingRulesRequest Request { get; }
    public string ServerId => _metadata.ServerId;
    public long PaperVersion => _metadata.PaperVersion;
    public IReadOnlyList<GradingRevisionQuestion> Preview { get; }
    public bool PublicationAttempted { get; private set; }

    public static async Task<PreparedGradingRevision> PrepareAsync(IGradingRevisionClient api, string examId,
        string source, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new InvalidDataException("请填写不超过 1000 字的本次修改备注。");
        var generation = api.Generation;
        var snapshot = await api.GetExamAsync(examId, ct);
        var serverId = api.ServerId;
        var actor = await api.GetGradingOperatorAsync(ct);
        if (snapshot.ExamId != examId || snapshot.Paper is null || snapshot.Grading is null ||
            snapshot.Versions.PaperVersion < 1 || snapshot.Versions.GradingVersion < 1)
            throw new InvalidDataException("该考试尚未发布完整的试卷和规则版本。");
        var manifest = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(source, "manifest.json"), ct);
        var answers = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(source, "server-only", "answer-key.json"), ct);
        var rules = ImportedGradingRules.Create(manifest, answers);
        if (rules.ExamId != examId || !rules.Matches(snapshot.Paper.Structure))
        {
            var current = await api.GetGradingMaterialsAsync(examId, ct);
            var currentRules = ImportedGradingRules.Create(current.ManifestJson, current.AnswerKeyJson);
            if (current.ExamId != examId || current.PaperVersion != snapshot.Versions.PaperVersion ||
                current.GradingVersion != snapshot.Versions.GradingVersion || current.RuleSetId != snapshot.Grading.RuleSetId ||
                currentRules.ExamId != examId || !currentRules.Matches(snapshot.Paper.Structure))
                throw new InvalidDataException("读取材料期间试卷或规则版本已变化，请重新检查。");
            (manifest, answers) = ImportPipeline.BindPackageToSelectedExam(manifest, answers, examId,
                JsonSerializer.Deserialize<ExamManifest>(current.ManifestJson, ImportJson.Create())!.ExamName);
            manifest = BindPublishedCatalog(manifest, current.ManifestJson);
        }
        if (api.Generation != generation || string.IsNullOrWhiteSpace(serverId) || api.ServerId != serverId)
            throw new InvalidOperationException("核对期间服务器连接改变，请在原服务器重新核对。");
        return new(new(serverId, examId, snapshot.Versions.PaperVersion, snapshot.Versions.GradingVersion,
            snapshot.Paper.Structure, actor, reason, Guid.NewGuid().ToString("N"), RuleSetId(manifest, answers),
            Hash(manifest), Hash(answers)), manifest, answers);
    }

    private static string BindPublishedCatalog(string manifestJson, string publishedJson)
    {
        var json = ImportJson.Create();
        var source = JsonSerializer.Deserialize<ExamManifest>(manifestJson, json)!;
        var published = JsonSerializer.Deserialize<ExamManifest>(publishedJson, json)!;
        if (source.Subjects.Count != published.Subjects.Count || source.Groups.Count != published.Groups.Count)
            throw new InvalidDataException("答案修订不能改变已发布试卷的科目或题组数量。");
        var subjects = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subject in source.Subjects)
        {
            var matches = published.Subjects.Where(s => ExamSettingsRules.NameKey(s.Name) ==
                ExamSettingsRules.NameKey(subject.Name)).ToArray();
            if (matches.Length != 1 || subjects.Values.Contains(matches[0].SubjectId, StringComparer.Ordinal))
                throw new InvalidDataException("答案修订的科目与已发布试卷不一致。");
            subjects.Add(subject.SubjectId, matches[0].SubjectId);
        }
        foreach (var group in source.Groups)
        {
            var matches = published.Groups.Where(g => g.SubjectId == subjects[group.SubjectId] &&
                g.Order == group.Order && ExamSettingsRules.NameKey(g.Name) == ExamSettingsRules.NameKey(group.Name) &&
                g.Type == group.Type && g.QuestionCount == group.QuestionCount && g.AllowAbstain == group.AllowAbstain).ToArray();
            if (matches.Length != 1 || groups.Values.Contains(matches[0].GroupId, StringComparer.Ordinal))
                throw new InvalidDataException("答案修订的题组与已发布试卷不一致。");
            groups.Add(group.GroupId, matches[0].GroupId);
        }
        // First publication binds source identifiers to server identifiers. Repeat only
        // that mapping here; the operator's new answers, totals and scoring stay intact.
        return JsonSerializer.Serialize(source with
        {
            Subjects = source.Subjects.Select(s => s with { SubjectId = subjects[s.SubjectId] }).ToArray(),
            Groups = source.Groups.Select(g => g with { SubjectId = subjects[g.SubjectId], GroupId = groups[g.GroupId] }).ToArray(),
            Questions = source.Questions.Select(q => q with { SubjectId = subjects[q.SubjectId], GroupId = groups[q.GroupId] }).ToArray()
        }, json);
    }

    public async Task<ExamVersionSnapshot> PublishAsync(IGradingRevisionClient api, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = api.Generation;
            // Only establish/verify identity here. A newer local projection is never a durable acknowledgement.
            var identity = await api.GetExamAsync(Request.ExamId, ct);
            var actor = await api.GetGradingOperatorAsync(ct);
            if (api.Generation != generation || api.ServerId != ServerId || identity.ExamId != Request.ExamId || actor != Request.Operator)
                throw new InvalidDataException("规则预览属于另一服务器、考试或考务账号，未发送发布请求。");
            if (_savedDirectory is not null)
            {
                var manifest = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(_savedDirectory, "manifest.json"), ct);
                var answers = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(_savedDirectory, "answer-key.json"), ct);
                var metadata = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(_savedDirectory, "revision.json"), ct);
                if (Hash(manifest) != _metadata.ManifestHash || Hash(answers) != _metadata.AnswerHash ||
                    JsonSerializer.Serialize(JsonSerializer.Deserialize<Metadata>(metadata, Json), Json) != JsonSerializer.Serialize(_metadata, Json))
                    throw new InvalidDataException("已保存的规则预览发生变化，禁止以原请求编号提交不同内容。");
            }
            PublicationAttempted = true;
            var result = await api.SendGradingRevisionAsync(Request, ct);
            if (api.Generation != generation || api.ServerId != ServerId || result.ExamId != Request.ExamId ||
                result.Versions.PaperVersion != PaperVersion || result.Paper?.Version != PaperVersion ||
                result.Versions.GradingVersion != Request.ExpectedVersion + 1 ||
                result.Grading?.Version != Request.ExpectedVersion + 1 || result.Grading.RuleSetId != Request.RuleSetId)
                throw new InvalidDataException("发布回执与本次规则预览不一致，尚不能确认成功；请保留同一预览重试。");
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ExportAsync(string directory, CancellationToken ct = default)
    {
        directory = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(directory) ?? throw new InvalidDataException("请选择新的规则预览目录。");
        ExamImportWorkspace.RejectLinks(parent);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("规则预览输出目录已经存在，请选择新目录。");
        var staging = Path.Combine(parent, ".c05-grading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            ExamImportWorkspace.RestrictAccess(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), Request.ManifestJson, ct);
            await File.WriteAllTextAsync(Path.Combine(staging, "answer-key.json"), Request.AnswerKeyJson, ct);
            await File.WriteAllTextAsync(Path.Combine(staging, "revision.json"), JsonSerializer.Serialize(_metadata, Json), ct);
            var preview = new StringBuilder("# 规则修订预览（含参考答案，仅保存在本机）\n\n")
                .AppendLine($"试卷版本：{PaperVersion}；规则版本：{Request.ExpectedVersion} → {Request.ExpectedVersion + 1}。")
                .AppendLine("修改原因：" + Request.Reason).AppendLine();
            foreach (var q in Preview) preview.AppendLine($"第 {q.QuestionNo} 题：参考答案 {q.CorrectOptions}；满分 {q.FullScore}。");
            await File.WriteAllTextAsync(Path.Combine(staging, "preview.md"), preview.ToString(), ct);
            Directory.Move(staging, directory);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static async Task<PreparedGradingRevision> LoadAsync(string directory, CancellationToken ct = default)
    {
        directory = Path.GetFullPath(directory);
        ExamImportWorkspace.RejectLinks(directory);
        var lockPath = Path.Combine(directory, ".revision.lock");
        ExamImportWorkspace.RejectLinks(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var metadata = JsonSerializer.Deserialize<Metadata>(await ExamImportWorkspace.ReadUtf8Async(
                Path.Combine(directory, "revision.json"), ct), Json) ?? throw new InvalidDataException("规则预览缺少元数据。");
            var prepared = new PreparedGradingRevision(metadata,
                await ExamImportWorkspace.ReadUtf8Async(Path.Combine(directory, "manifest.json"), ct),
                await ExamImportWorkspace.ReadUtf8Async(Path.Combine(directory, "answer-key.json"), ct))
            { _lease = lease, _savedDirectory = directory };
            return prepared;
        }
        catch { lease.Dispose(); throw; }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string RuleSetId(string manifest, string answers) => "rules-" + Hash(JsonSerializer.Serialize(new[] { manifest, answers }));
    public void Dispose() { _disposed = true; _lease?.Dispose(); }
    private sealed record Metadata(string ServerId, string ExamId, long PaperVersion, long GradingVersion,
        PaperStructure Structure, string Operator, string Reason, string RequestId, string RuleSetId,
        string ManifestHash, string AnswerHash);
}

public sealed record GradingRevisionQuestion(int QuestionNo, string QuestionId, string CorrectOptions, string FullScore);
