using System.Collections.Frozen;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;
using Exam.Domain.Importing;

namespace Exam.Student.Core;

/// <summary>
/// 从 B02 已认证解锁的 D03 包读取不可变题目目录。只在内存中检查，不解压到磁盘，
/// 不提供参考答案，也不凭本地目录授权作答。版本和当前授权仍由 B02/运行时核对。
/// </summary>
public sealed class StudentPaperDefinition
{
    private sealed record Question(QuestionTypeKind Type, bool AllowAbstain, FrozenSet<string> Options);
    private readonly FrozenDictionary<string, Question> _questions;

    private StudentPaperDefinition(ExamManifest manifest)
    {
        ExamId = manifest.ExamId;
        var groups = manifest.Groups.ToDictionary(group => group.GroupId, StringComparer.Ordinal);
        _questions = manifest.Questions.ToFrozenDictionary(question => question.QuestionId, question =>
        {
            var group = groups[question.GroupId];
            var type = group.Type switch
            {
                "single" => QuestionTypeKind.Single,
                "multiple" => QuestionTypeKind.Multiple,
                "indeterminate" => QuestionTypeKind.Indeterminate,
                _ => throw new InvalidDataException("题组类型无效。")
            };
            return new Question(type, group.AllowAbstain, question.Options!.ToFrozenSet(StringComparer.Ordinal));
        }, StringComparer.Ordinal);
    }

    public string ExamId { get; }
    public int QuestionCount => _questions.Count;
    public bool ContainsQuestion(string questionId) => _questions.ContainsKey(questionId);

    public bool AllowsAnswer(string questionId, AnswerValue? answer)
    {
        if (!_questions.TryGetValue(questionId, out var question) || answer is null ||
            !Enum.IsDefined(answer.Kind) || answer.OptionIds is null)
            return false;
        var options = answer.OptionIds;
        if (answer.Kind != AnswerKind.Selected)
            return options.Count == 0 && (answer.Kind == AnswerKind.Empty || question.AllowAbstain);
        return options.Count > 0 && (question.Type != QuestionTypeKind.Single || options.Count == 1) &&
            options.All(option => option is not null && question.Options.Contains(option)) &&
            options.Distinct(StringComparer.Ordinal).Count() == options.Count;
    }

    public IReadOnlyDictionary<string, AnswerValue> CreateEmptyAnswers() =>
        _questions.Keys.ToFrozenDictionary(id => id, _ => AnswerValue.Empty(), StringComparer.Ordinal);

    public bool HasCompleteAnswers(IReadOnlyDictionary<string, AnswerValue> answers) =>
        answers.Count == _questions.Count && answers.All(pair => AllowsAnswer(pair.Key, pair.Value));

    public static StudentPaperDefinition Read(byte[] authenticatedPayload, string expectedExamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExamId);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new MemoryStream(authenticatedPayload, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > StudentPackageLayout.MaxEntryCount)
            throw new InvalidDataException("学生题包文件数量无效。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedLength = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.FullName;
            if (path.Length > PackagePathRules.MaxLength || !PackagePathRules.IsRelativeAndInside(path) ||
                path != PackagePathRules.Normalize(path) || path.Split('/').Any(segment => !WindowsFileNameRules.IsValidFileName(segment)) ||
                !caseInsensitivePaths.Add(path) ||
                entry.Length < 0 || entry.Length > StudentPackageLayout.MaxContentBytes - expandedLength)
                throw new InvalidDataException("学生题包存在重复、越界路径或超限文件。");
            expandedLength += entry.Length;
            entries.Add(path, entry);
        }
        if (!entries.TryGetValue(StudentPackagePlan.MetadataFileName, out var metadata))
            throw new InvalidDataException("学生题包缺少清单。");

        var metadataBytes = ReadBounded(metadata, StudentPackageLayout.MaxMetadataBytes, cancellationToken);
        ExamManifest manifest;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(metadataBytes);
            var validation = new ExamManifestValidator().ValidateJson(text);
            if (!validation.IsValid)
                throw new InvalidDataException("学生题包清单未通过结构及题目约束校验。");
            manifest = JsonSerializer.Deserialize<ExamManifest>(text, ImportJson.Create())!;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("学生题包清单必须使用有效的 UTF-8 编码。", exception);
        }
        finally { CryptographicOperations.ZeroMemory(metadataBytes); }
        if (!string.Equals(manifest.ExamId, expectedExamId, StringComparison.Ordinal))
            throw new InvalidDataException("题包所属考试与当前参试授权不一致。");

        var questionPdfs = manifest.Questions.Select(question => StudentPackageLayout.QuestionPdfPath(question.Source))
            .ToHashSet(StringComparer.Ordinal);
        if (questionPdfs.Count != manifest.Questions.Count || questionPdfs.Any(path => !entries.ContainsKey(path)))
            throw new InvalidDataException("题目清单与包内 PDF 未一一对应。");
        var resourceRoots = manifest.ResourceRoots.Select(PackagePathRules.Normalize).ToArray();
        foreach (var (path, entry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path == StudentPackagePlan.MetadataFileName) continue;
            if (!questionPdfs.Contains(path) &&
                (path.StartsWith("questions/", StringComparison.OrdinalIgnoreCase) ||
                 !resourceRoots.Any(root => root.Length == 0 || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("学生题包包含未声明的显示文件。");
            if (!StudentPackageAllowlist.IsAllowed(path, null, out _))
                throw new InvalidDataException("学生题包包含不允许分发的文件。");
            var bytes = ReadBounded(entry, StudentPackageLayout.MaxContentBytes, cancellationToken);
            try
            {
                if (!HasDisplaySignature(path, bytes))
                    throw new InvalidDataException("学生题包显示文件类型不符合约定。");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return new StudentPaperDefinition(manifest);
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, int maximum, CancellationToken token)
    {
        if (entry.Length > maximum) throw new InvalidDataException("题包内容超过容量限制。");
        var bytes = new byte[checked((int)entry.Length)];
        try
        {
            using var stream = entry.Open();
            var offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var count = stream.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                if (count == 0) throw new InvalidDataException("题包内容未读取完整。");
                offset += count;
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("题包内容超过声明长度。");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static bool HasDisplaySignature(string path, byte[] bytes) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => bytes.AsSpan().StartsWith("%PDF-"u8),
        ".png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
        _ => false
    };
}
