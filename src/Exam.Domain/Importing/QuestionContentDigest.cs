using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;

namespace Exam.Domain.Importing;

/// <summary>
/// 逐题**内容摘要**：用于判断「这场考试的某道题的内容相对上一已发布版本变了没有」，
/// 是服务器核对「实际变更题号 vs 声明变更题号」的唯一判据。客户端与服务器**共用这份实现**，
/// 避免两边算法漂移；服务器那道门才是权威的。
///
/// 摘要面 = 需求允许在开考后修改的面（`requirements.md:170`：题干、图片、已有选项内容）：
/// 题目的结构字段（题号/科目/题组/题型/弃权/选项顺序）、源文件相对路径、显示元数据、
/// 题目 `.tex` 内容，以及**依赖闭包**（`\input`/`\include` 递归、包内 `.sty`/`.cls`/`.def`/`.cfg`、
/// 按 `\graphicspath` 解析到的图片）的逐文件内容哈希。
///
/// 刻意**不计入**：
/// - 独立答案 JSON——只改答案属于规则修订，学生手里那份卷子没变，不该要求复核；
/// - TeX 发行版的宏包/字体——不在材料集合内，工具链版本不在内容差异范围；
/// - 没有任何题目依赖的文件——改它不算题目内容变化。
///
/// 刻意**保守**（写进文档，避免被当成误判）：共享文件（共享样式、共享图片）变化会让
/// **所有依赖它的题**都算变更——它们的渲染确实变了，方向是「宁可多提示复核，不可漏提示」。
///
/// 已知残余限制：宏在运行时拼出的图片文件名无法静态解析（只做同文件内的宏展开，见
/// <see cref="TeXSourceInspector"/>），这类依赖只能靠共享样式文件本身的内容变化间接兜住。
/// </summary>
public static class QuestionContentDigest
{
    /// <summary>摘要格式版本。算法语义一变就要递增，服务器按库内记录拒绝跨版本比较。</summary>
    public const int CurrentFormatVersion = 1;

    private const string Scheme = "c05-content-digest-v1";
    private const int MaxDependencyDepth = 32;
    private const int MaxDependencyFiles = 2048;

    /// <summary>跨文件图片宏的回扫轮次上限：新样式文件可能再定义新宏，有限轮次保证有界。</summary>
    private const int MaxImageMacroRounds = 4;

    /// <summary>可当作文本解析依赖的文件后缀：只扫这些，避免把几 MB 的图片当文本解析。</summary>
    private static readonly string[] TextExtensions = [".tex", ".sty", ".cls", ".def", ".cfg"];

    /// <summary>包内宏包/文档类文件的后缀（发行版宏包不在材料集合内，自然不计入）。</summary>
    private static readonly string[] PackageExtensions = [".sty", ".cls", ".def", ".cfg"];

    /// <summary>按清单逐题计算内容摘要，返回 questionId → 摘要（十六进制）。</summary>
    public static IReadOnlyDictionary<string, string> Compute(ExamManifest manifest, IMaterialFileSource materials)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(materials);

        var structure = ImportedGradingRules.BuildStructure(manifest);
        var questions = manifest.Questions.ToDictionary(question => question.QuestionId, StringComparer.Ordinal);
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var question in structure.Questions)
        {
            if (!questions.TryGetValue(question.QuestionId, out var manifestQuestion))
            {
                throw new InvalidDataException($"清单缺少题目的源文件声明：{question.QuestionId}");
            }

            digests[question.QuestionId] = ComputeOne(question, manifestQuestion, materials);
        }

        return digests;
    }

    /// <summary>
    /// 比对两次摘要，返回**实际变更**的题号（新增、消失、或摘要不同），ordinal 排序。
    /// 结构变化本身由 A06 在事务内拒绝，这里只回答「内容变了没有」。
    /// </summary>
    public static IReadOnlyList<string> Changed(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        return previous.Keys.Concat(current.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(questionId => !previous.TryGetValue(questionId, out var before) ||
                !current.TryGetValue(questionId, out var after) ||
                !StringComparer.Ordinal.Equals(before, after))
            .OrderBy(questionId => questionId, StringComparer.Ordinal)
            .ToList();
    }

    private static string ComputeOne(PaperQuestionStructure structure, ManifestQuestion question,
        IMaterialFileSource materials)
    {
        var writer = new DigestWriter();
        writer.Text(Scheme);
        writer.Text(structure.QuestionId);
        writer.Number(structure.QuestionNo);
        writer.Text(structure.SubjectId);
        writer.Text(structure.GroupId);
        writer.Text(structure.QuestionType);
        writer.Bool(structure.AllowAbstain);
        foreach (var option in structure.OptionIds)
        {
            writer.Text(option);
        }

        // 源文件路径也计入：路径变了学生端拿到的 PDF 文件名就变了，属可见变化。
        writer.Text(InMemoryMaterialFileSource.Normalize(question.Source));
        writer.Text(question.DisplayMetadata?.AnchorScheme ?? string.Empty);
        foreach (var caption in question.DisplayMetadata?.OptionCaptions ?? [])
        {
            writer.Text(caption);
        }

        writer.Number(question.DisplayMetadata?.PageCount ?? 0);

        var source = InMemoryMaterialFileSource.Normalize(question.Source);
        var canonical = materials.Resolve(source)
            ?? throw new InvalidDataException($"材料集合里找不到题目源文件：{question.Source}");
        writer.Bytes(Sha256(materials.Read(canonical)));

        foreach (var (path, hash) in ResolveDependencies(canonical, materials))
        {
            writer.Text(path);
            writer.Bytes(hash);
        }

        return writer.Finish();
    }

    /// <summary>
    /// 依赖闭包：从题目源文件出发按 BFS 收集，返回 (规范化路径, 内容哈希) 并按路径 ordinal 排序。
    ///
    /// 图片宏是**跨文件**的：题目里 `\qimg{图.png}` 的宏定义在共享样式里，
    /// 因此先按 `\input`/包内样式/`\includegraphics` 走一轮，再用闭包内已发现的宏名回头扫一遍，
    /// 直到没有新文件为止（有限轮次 + 文件数上限，保证确定且有界）。
    /// </summary>
    private static IReadOnlyList<(string Path, byte[] Hash)> ResolveDependencies(
        string entry, IMaterialFileSource materials)
    {
        // 键是**小写**规范路径：材料集合里不存在只差大小写的两个文件，
        // 因此大小写重命名不该被当成内容变化。
        var collected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var graphicsRoots = new List<string>();
        var imageMacros = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        void Visit(string? candidate, int depth)
        {
            if (candidate is null || visited.Contains(candidate))
            {
                return;
            }

            if (depth > MaxDependencyDepth)
                throw new InvalidDataException("题目依赖层级超过上限，不能忽略深层材料后继续发布。");
            visited.Add(candidate);
            if (visited.Count > MaxDependencyFiles)
            {
                throw new InvalidDataException("题目的依赖闭包引用了过多文件，无法计算内容摘要。");
            }

            depths[candidate] = depth;
            collected[candidate.ToLowerInvariant()] = Sha256(materials.Read(candidate));
            queue.Enqueue(candidate);
        }

        void Drain()
        {
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var depth = depths[path];
                if (!IsTextLike(path))
                {
                    continue;
                }

                var text = TryDecode(materials.Read(path));
                if (text is null)
                    throw new InvalidDataException("题目依赖的文本材料不是有效 UTF-8，无法完整计算内容摘要。");

                texts[path] = text;
                var declaration = TeXSourceInspector.Inspect(text);
                foreach (var root in declaration.GraphicsRoots)
                {
                    if (!graphicsRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                    {
                        graphicsRoots.Add(root);
                    }
                }

                foreach (var macro in TeXSourceInspector.ReadImageMacros(text))
                {
                    imageMacros.Add(macro);
                }

                foreach (var nested in TeXSourceInspector.ReadNestedInputs(text))
                {
                    Visit(ResolveRelative(nested, path, materials, [".tex"]), depth + 1);
                }

                foreach (var package in declaration.Packages
                             .Append(declaration.DocumentClass)
                             .Where(name => name.Length > 0))
                {
                    Visit(ResolveRelative(package, path, materials, PackageExtensions), depth + 1);
                }

                foreach (var reference in declaration.ImageReferences)
                {
                    Visit(ResolveImage(reference, materials, graphicsRoots), depth + 1);
                }
            }
        }

        // 题目源文件本身是闭包的起点：先入队再处理，否则闭包恒为空。
        depths[entry] = 0;
        queue.Enqueue(entry);
        Drain();
        // 闭包里新出现的图片宏可能把图片引用写在别的文件里：再扫一遍，直到没有新文件。
        for (var round = 0; round < MaxImageMacroRounds; round++)
        {
            var before = visited.Count;
            foreach (var (path, text) in texts)
            {
                // A shared style may declare graphicspath after the question was visited.
                // Revisit direct image references as well as cross-file image macros.
                foreach (var reference in TeXSourceInspector.Inspect(text).ImageReferences
                    .Concat(TeXSourceInspector.ReadImageMacroInvocations(text, imageMacros.ToList())))
                {
                    Visit(ResolveImage(reference, materials, graphicsRoots), depths[path] + 1);
                }
            }

            Drain();
            if (visited.Count == before)
            {
                break;
            }
            if (round == MaxImageMacroRounds - 1)
                throw new InvalidDataException("图片依赖解析未收敛，无法完整计算内容摘要。");
        }

        return collected.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    /// <summary>
    /// 图片解析：与导入期同一顺序（包根 → 各 `\graphicspath`），取材料集合里第一个存在的候选。
    /// 编译期实际取用哪个文件由导入校验与编译负责；这里只保证「它变了就一定看得见」。
    /// </summary>
    private static string? ResolveImage(string reference, IMaterialFileSource materials,
        IReadOnlyList<string> graphicsRoots)
    {
        foreach (var candidate in TeXSourceInspector.ResolveImageCandidates(reference, graphicsRoots))
        {
            if (materials.Resolve(candidate) is { } image)
            {
                return image;
            }
        }

        return null;
    }

    /// <summary>
    /// 把引用解析为材料集合内的规范路径：先按引用所在目录、再按材料根目录，逐个候选后缀探测。
    /// 带后缀或带目录的引用只按原样探测（不在后面拼后缀）。
    /// </summary>
    private static string? ResolveRelative(string reference, string includingFile,
        IMaterialFileSource materials, IReadOnlyList<string> extensions)
    {
        var normalized = InMemoryMaterialFileSource.Normalize(reference);
        if (normalized.Length == 0 || !PackagePathRules.IsRelativeAndInside(normalized))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(includingFile.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
        var names = extensions.Count == 0 || Path.HasExtension(normalized)
            ? [normalized]
            : extensions.Select(extension => normalized + extension);

        foreach (var name in names)
        {
            if (directory.Length > 0 && materials.Resolve($"{directory}/{name}") is { } relative)
            {
                return relative;
            }

            if (materials.Resolve(name) is { } rooted)
            {
                return rooted;
            }
        }

        return null;
    }

    private static bool IsTextLike(string path) =>
        TextExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string? TryDecode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            // 非 UTF-8 文本：不作为依赖来源解析，仍按内容参与哈希。
            return null;
        }
    }

    private static byte[] Sha256(byte[] content) => SHA256.HashData(content);

    /// <summary>长度前缀编码：不依赖 JSON 属性顺序，且跨节点字节序一致（显式小端）。</summary>
    private sealed class DigestWriter
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Text(string value) => Frame(Encoding.UTF8.GetBytes(value));

        public void Bytes(byte[] value) => Frame(value);

        public void Bool(bool value) => Frame(value ? [1] : [0]);

        public void Number(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            Frame(buffer);
        }

        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

        private void Frame(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            _hash.AppendData(length);
            _hash.AppendData(value);
        }
    }
}
