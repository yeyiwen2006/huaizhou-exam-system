using System.Text;

using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 逐题内容摘要的用例矩阵：哪些改动**必须**算成「这题变了」，哪些**不该**算。
///
/// 判据是 requirements.md「考试中修改时间与试题」允许改的面（题干、图片、已有选项内容），
/// 以及 A 的要求「摘要必须明确覆盖题目依赖的图片与共享资源」。全部用内存合成材料，不接触真实试卷。
/// </summary>
public sealed class QuestionContentDigestTests
{
    private const string Source = "questions/合成题_1.tex";
    private const string SharedStyle = "styles/optmarks.sty";

    [Fact]
    public void Compute_IsDeterministic_ForTheSameMaterials()
    {
        var materials = new InMemoryMaterialFileSource(BaseMaterials());

        Assert.Equal(
            QuestionContentDigest.Compute(Manifest(), materials),
            QuestionContentDigest.Compute(Manifest(), materials));
    }

    [Fact]
    public void Changed_StemEdit_MarksThatQuestionOnly()
    {
        // 每题引用自己的图片，改一题不该牵连另一题。
        Assert.Equal(["q-1"], Changed(Mutate(Source, QuestionBody(1, "题干改了一个字"))));
    }

    [Fact]
    public void Changed_StemEditOfTheSecondQuestion_MarksOnlyIt()
    {
        Assert.Equal(["q-2"], Changed(Mutate("questions/合成题_2.tex", QuestionBody(2, "只改第二题"))));
    }

    [Fact]
    public void Changed_ReferencedImageEdit_MarksThatQuestion()
    {
        Assert.Equal(["q-1"], Changed(Replace("figures/q1.png", Bytes("改过的图"))));
    }

    [Fact]
    public void Changed_SharedStyleEdit_MarksEveryQuestionThatIncludesIt()
    {
        // 共享样式一变，依赖它的题渲染都变了：两题都算变更（刻意的保守方向）。
        Assert.Equal(["q-1", "q-2"], Changed(Mutate(SharedStyle, "% 共享样式改了一处")));
    }

    [Fact]
    public void Changed_ImagePassedToASharedStyleMacro_MarksThatQuestion()
    {
        // `\qimg{...}` 的宏定义在共享样式里：只看题目文件解析不出这张图，必须回扫宏调用才看得见。
        Assert.Equal(["q-1"], Changed(Replace("figures/via-macro-q1.png", Bytes("改过的图"))));
    }

    [Fact]
    public void Changed_NestedInputEdit_MarksThatQuestion()
    {
        // 题干片段放在 \input 进来的文件里。
        Assert.Equal(["q-1", "q-2"], Changed(Mutate("parts/body.tex", "片段里改了一个字，两题共用")));
    }

    [Fact]
    public void Changed_UnreferencedFileEdit_MarksNothing()
    {
        Assert.Empty(Changed(Replace("figures/未被引用.png", Bytes("随便改"))));
    }

    [Fact]
    public void Changed_AnswerKeyEdit_MarksNothing()
    {
        // 只改答案属于规则修订：学生手里那份卷子没变，不该要求复核。
        Assert.Empty(Changed(Mutate("server-only/answer-key.json",
            """{"schemaVersion":1,"examId":"exam","answers":[{"questionId":"q-1"}]}""")));
    }

    [Fact]
    public void Changed_DisplayMetadataEdit_MarksThatQuestion()
    {
        var previous = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(BaseMaterials()));
        var current = QuestionContentDigest.Compute(
            Manifest(captions: ["甲", "乙"]), new InMemoryMaterialFileSource(BaseMaterials()));

        Assert.Equal(["q-1"], QuestionContentDigest.Changed(previous, current));
    }

    [Fact]
    public void Changed_SourcePathEditWithIdenticalBytes_MarksThatQuestion()
    {
        // 源文件路径变了，学生端拿到的 PDF 文件名就变了：按保守方向算变更。
        var renamed = new List<KeyValuePair<string, byte[]>>();
        foreach (var pair in BaseMaterials())
        {
            renamed.Add(pair.Key == Source
                ? new KeyValuePair<string, byte[]>("questions/合成题_1改.tex", pair.Value)
                : pair);
        }

        var previous = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(BaseMaterials()));
        var current = QuestionContentDigest.Compute(
            Manifest(source: "questions/合成题_1改.tex"), new InMemoryMaterialFileSource(renamed));

        Assert.Equal(["q-1"], QuestionContentDigest.Changed(previous, current));
    }

    [Fact]
    public void Changed_PathCaseRenameWithIdenticalBytes_DoesNotMark()
    {
        // 只改大小写、字节没变：材料集合里不可能同时存在两个只差大小写的文件，不该算内容变化。
        var renamed = BaseMaterials()
            .Select(pair => pair.Key == "figures/q1.png"
                ? new KeyValuePair<string, byte[]>("Figures/Q1.PNG", pair.Value)
                : pair)
            .ToList();

        var previous = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(BaseMaterials()));
        var current = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(renamed));

        Assert.Empty(QuestionContentDigest.Changed(previous, current));
    }

    [Fact]
    public void Changed_WhichFileAnImageResolvesTo_MarksThatQuestion()
    {
        // 同名 q1.png 在两个搜索路径下都存在：把优先命中那个删掉，实际取用的文件就换了 ⇒ 算变更。
        var body = QuestionBody(1, "题干", graphicsPath: "\\graphicspath{{figures/}{second/}}")
            .Replace("figures/q1.png", "q1.png", StringComparison.Ordinal);
        var withFigures = BaseMaterials()
            .Where(pair => pair.Key != "figures/q1.png")
            .Append(new KeyValuePair<string, byte[]>("figures/q1.png", Bytes("优先命中")))
            .Select(pair => pair.Key == Source ? new KeyValuePair<string, byte[]>(Source, Bytes(body)) : pair)
            .ToList();
        var withSecond = withFigures
            .Where(pair => pair.Key != "figures/q1.png")
            .Append(new KeyValuePair<string, byte[]>("second/q1.png", Bytes("次选命中")))
            .ToList();

        var before = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(withFigures));
        var after = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(withSecond));

        Assert.Equal(["q-1"], QuestionContentDigest.Changed(before, after));
    }

    private static IReadOnlyList<string> Changed(IReadOnlyList<KeyValuePair<string, byte[]>> after)
    {
        var previous = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(BaseMaterials()));
        var current = QuestionContentDigest.Compute(Manifest(), new InMemoryMaterialFileSource(after));
        return QuestionContentDigest.Changed(previous, current);
    }

    private static IReadOnlyList<KeyValuePair<string, byte[]>> Mutate(string path, string content) =>
        Replace(path, Bytes(content));

    private static IReadOnlyList<KeyValuePair<string, byte[]>> Replace(string path, byte[] content) =>
        BaseMaterials()
            .Select(pair => pair.Key == path ? new KeyValuePair<string, byte[]>(path, content) : pair)
            .ToList();

    private static IReadOnlyList<KeyValuePair<string, byte[]>> BaseMaterials() =>
    [
        new(Source, Bytes(QuestionBody(1, "合成题：下列选项正确的是。"))),
        new("questions/合成题_2.tex", Bytes(QuestionBody(2, "合成题二：下列选项正确的是。"))),
        new(SharedStyle, Bytes(SharedStyleBody())),
        new("parts/body.tex", Bytes("题干片段（两题共用）。")),
        new("figures/q1.png", Bytes("图片一")),
        new("figures/q2.png", Bytes("图片二")),
        new("figures/via-macro-q1.png", Bytes("宏引用图片一")),
        new("figures/via-macro-q2.png", Bytes("宏引用图片二")),
        new("figures/未被引用.png", Bytes("没人引用我")),
        new("server-only/answer-key.json", Bytes("""{"schemaVersion":1,"examId":"exam","answers":[]}""")),
        new("manifest.json", Bytes("{}"))
    ];

    private static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);

    private static string QuestionBody(int number, string stem,
        string graphicsPath = "\\graphicspath{{figures/}}") => string.Join('\n',
        "\\documentclass{ctexart}",
        "\\usepackage{" + SharedStyle + "}",
        graphicsPath,
        "\\input{parts/body.tex}",
        "\\begin{document}",
        stem,
        "\\includegraphics[width=4cm]{figures/q" + number + ".png}",
        "\\qimg{figures/via-macro-q" + number + ".png}",
        "\\end{document}");

    private static string SharedStyleBody() => string.Join('\n',
        "\\newcommand{\\optmark}[3]{\\hypertarget{opt:#1:#2}{}\\hyperlink{opt:#1:#2}{#3}}",
        "\\newcommand{\\qimg}[1]{\\includegraphics{#1}}");

    private static ExamManifest Manifest(string source = Source, IReadOnlyList<string>? captions = null) => new(
        1,
        "exam",
        "合成题",
        [new ManifestSubject("subject", "科目", "1")],
        [new ManifestGroup("group", "subject", "单选", "single", 1, 2, false, "1",
            new ManifestGroupScoring(new ManifestOneCorrect("1", "0", "0"), null))],
        [
            new ManifestQuestion("q-1", 1, "subject", "group", source, ["A", "B"],
                captions is null ? null : new ManifestDisplayMetadata("options", captions, 1)),
            new ManifestQuestion("q-2", 2, "subject", "group", "questions/合成题_2.tex", ["A", "B"], null)
        ],
        []);
}
