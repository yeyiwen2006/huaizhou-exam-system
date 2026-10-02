namespace Exam.Contracts.Importing;

/// <summary>
/// 导入流水线的阶段。失败时据此告诉使用者**停在哪一步**：
/// 是材料本身不过校验、编译不过、组包失败、入库失败，还是版本发布没通过。
/// </summary>
public enum ImportStage
{
    Validate,
    Compile,
    Package,
    Store,
    Publish,
    Completed
}

/// <summary>一条导入问题。Code 为稳定标识，客户端不解析中文文案。</summary>
public sealed record ImportPipelineIssue(string Code, string Message, string? QuestionId = null, string? Path = null);

/// <summary>
/// 一次真实导入的输入。材料以**服务器本机可读的目录**给出（单机部署下即考务端所选目录）；
/// 编译产物与包产物一律写到调用方指定的目录，不写回材料目录。
///
/// 加密收件人**不在这里**：学生包的接收节点取自服务器受管节点及其公钥，
/// 主备任一台都必须能解密，不能由操作者给一组自选证书。
///
/// <paramref name="ExpectedPaperVersion"/> 是**本次发布出的版本**；
/// 该考试已发布过试卷版本时（开考后修订）必须逐题声明 <paramref name="DeclaredChangedQuestionIds"/>。
/// </summary>
public sealed record ImportPipelineRequest(
    string PackageRoot,
    string ExamId,
    long ExpectedPaperVersion,
    string CompilationOutputDirectory,
    string PackageOutputDirectory,
    string Operator,
    string Reason,
    string RequestId = "",
    IReadOnlyList<string>? DeclaredChangedQuestionIds = null);

/// <summary>
/// 预检的逐题摘要，供导入预览逐题显示：是否编译成功、清单声明的选项数、实际抽到的区域数。
/// 区域数与选项数不一致说明该题的标记不完整，预览阶段就应看到。
/// </summary>
public sealed record ImportQuestionPreview(string QuestionId, bool Compiled, int OptionCount, int RegionCount);

/// <summary>
/// 一次导入的结果。失败时不返回部分产物：学生包只有在**加密完成并成功入库**后才可能出现在结果里，
/// 版本发布失败也不会把未发布的包当成已发布。
/// </summary>
public sealed record ImportPipelineResult(
    bool Succeeded,
    ImportStage Stage,
    IReadOnlyList<ImportPipelineIssue> Issues,
    StudentPackagePlan? Plan = null,
    string? EncryptedPackagePath = null,
    string? ResourceId = null,
    long? PublishedPaperVersion = null,
    IReadOnlyList<ImportQuestionPreview>? Preview = null);
