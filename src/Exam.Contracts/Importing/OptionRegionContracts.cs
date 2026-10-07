namespace Exam.Contracts.Importing;

/// <summary>
/// 选项定位抽取的问题类别。稳定取值，客户端与 CLI 不解析中文文案分支。
/// 抽取失败时整次不产出坐标文件，不做部分成功。
/// </summary>
public enum OptionRegionIssueCode
{
    None,

    /// <summary>请求本身不合法：考试标识为空、试卷版本非正、题目或选项为空。</summary>
    RequestInvalid,

    /// <summary>题目 PDF 不存在、无法打开或页数无效。</summary>
    PdfUnreadable,

    /// <summary>两个不同的 (题目, 选项) 组合生成了相同的目标名，无法唯一绑定，必须拒绝。</summary>
    ExpectedNameCollision,

    /// <summary>选项标记结构不合法，包括同时指定动作与直接目标，或设置 Hidden/NoView 标志。</summary>
    MarkerMalformed,

    /// <summary>清单声明的选项没有对应的标签区域。</summary>
    MarkerMissing,

    /// <summary>同一个目标名出现分离的多处标记（不重叠、不相接），或跨页出现。</summary>
    MarkerDuplicate,

    /// <summary>PDF 里存在清单未声明的 <c>opt:</c> 标记。</summary>
    MarkerUnexpected,

    /// <summary>标记无法解析为同一 PDF 内的 GoTo 目标（远程目标、其它动作或目标不存在）。</summary>
    MarkerUnresolved,

    /// <summary>标记解析到的目标页与注解所在页不一致。</summary>
    DestinationPageMismatch,

    /// <summary>归一化结果不是有限数、宽高非正，或越出 <c>[0,1]</c>。</summary>
    RegionOutOfBounds,

    /// <summary>页索引超出该题 PDF 实际页数。</summary>
    PageIndexInvalid
}

/// <summary>
/// 一条选项定位问题。QuestionId / OptionId / PageIndex 在能确定时给出；
/// 不携带原始 PDF 内容或答案信息。
/// </summary>
public sealed record OptionRegionIssue(
    OptionRegionIssueCode Code,
    string Message,
    string? QuestionId = null,
    string? OptionId = null,
    int? PageIndex = null);

/// <summary>
/// 单个选项的定位区域。<see cref="PageIndex"/> 为**题内从 0 开始**的页索引，
/// 相对于该题 <c>PdfPath</c> 指向的逐题 PDF；第一页为 0。
/// 矩形为页面左上角原点的归一化坐标，取值须落在 <c>[0,1]</c>。
/// </summary>
public sealed record OptionRegion(
    string OptionId,
    int PageIndex,
    double X,
    double Y,
    double Width,
    double Height,
    OptionLabelOverlay? Overlay = null);

/// <summary>
/// 实心选项按钮的显示字母及遮盖范围。矩形包含原标签的可见字母和标点，不含链接留白，
/// 使用同页左上角归一化坐标；圆心仍取 OptionRegion 的字形框中心。
/// Text 只用于显示，答题绑定仍用 OptionId。旧产物或不可读字形可以没有此信息。
/// </summary>
public sealed record OptionLabelOverlay(string Text, double X, double Y, double Width, double Height)
{
    /// <summary>统一校验生产端、包读取和学生端的遮盖元数据。</summary>
    public bool IsValid() => !string.IsNullOrWhiteSpace(Text) &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}

/// <summary>一道题的选项区域，按清单选项顺序排列。<c>PdfPath</c> 沿用学生清单的题目映射，不在此重复。</summary>
public sealed record OptionRegionQuestion(
    string QuestionId,
    IReadOnlyList<OptionRegion> Options);

/// <summary>
/// 选项定位产物，固定放在学生包根目录，与 <c>manifest.json</c> 和逐题 PDF 位于同一加密 ZIP。
/// <see cref="ArtifactVersion"/> 与考生清单的 <c>schemaVersion</c> 是两个独立版本字段。
/// </summary>
public sealed record OptionRegionArtifact(
    int ArtifactVersion,
    string AnchorScheme,
    string ExamId,
    long PaperVersion,
    IReadOnlyList<OptionRegionQuestion> Questions)
{
    /// <summary>当前坐标产物版本。</summary>
    public const int CurrentArtifactVersion = 2;

    /// <summary>v1 只有定位矩形，仍可读取；新产物使用 v2 携带实心按钮的显示及遮盖信息。</summary>
    public const int LegacyArtifactVersion = 1;

    public static bool IsSupportedVersion(int version) => version is LegacyArtifactVersion or CurrentArtifactVersion;

    /// <summary>坐标规范名称。选项区域与题包 产出的区域与 的控件绑定共用此名称，任何一方不得自造。</summary>
    public const string AnchorSchemeName = "pageTopLeftNormalizedV1";

    /// <summary>学生包根目录下的固定文件名。</summary>
    public const string FileName = "option-regions.json";

    /// <summary>标记目标名的命名空间前缀。</summary>
    public const string MarkerPrefix = "opt:";

    /// <summary>按契约生成期望的目标名。标识内允许出现冒号，因此只用于精确比较，不做反解。</summary>
    public static string MarkerName(string questionId, string optionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        return $"{MarkerPrefix}{questionId}:{optionId}";
    }
}

/// <summary>
/// 一道题的抽取输入。<see cref="PdfPath"/> 为该题编译产物的绝对路径（不写入产物）；
/// <see cref="OptionIds"/> 必须与清单选项顺序一致。
/// </summary>
public sealed record OptionRegionSourceQuestion(
    string QuestionId,
    string PdfPath,
    IReadOnlyList<string> OptionIds);

/// <summary>一次抽取的输入。</summary>
public sealed record OptionRegionRequest(
    string ExamId,
    long PaperVersion,
    IReadOnlyList<OptionRegionSourceQuestion> Questions);

/// <summary>
/// 抽取结果。<see cref="Artifact"/> 非空仅当没有任何问题；失败时不产出部分坐标文件。
/// </summary>
public sealed record OptionRegionExtractionResult(
    OptionRegionArtifact? Artifact,
    IReadOnlyList<OptionRegionIssue> Issues)
{
    public bool Succeeded => Artifact is not null && Issues.Count == 0;

    public static OptionRegionExtractionResult Failed(params OptionRegionIssue[] issues) => new(null, issues);

    public static OptionRegionExtractionResult Failure(IReadOnlyList<OptionRegionIssue> issues) => new(null, issues);
}
