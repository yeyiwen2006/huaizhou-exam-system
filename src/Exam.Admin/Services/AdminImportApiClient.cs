using System.Net.Http;
using Exam.Contracts.Importing;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    /// <summary>
    /// 服务端预检：校验清单与答案、逐题编译、逐题抽取选项坐标，返回**逐题摘要**供导入预览确认。
    /// 不组包、不入库、不发布——确认后才调用发布。
    /// </summary>
    public Task<ImportPipelineResult> PreviewPaperImportAsync(
        string packageRoot,
        string examId,
        long expectedPaperVersion,
        string compilationDirectory,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendAsync<ImportPipelineResult>(HttpMethod.Post, "api/v1/imports/preview", new
        {
            packageRoot,
            examId,
            expectedPaperVersion,
            compilationOutputDirectory = compilationDirectory,
            reason
        }, cancellationToken);

    /// <summary>
    /// 服务端发布：在预检同一准备结果上继续完成组包加密、版本资源入库与试卷版本发布。
    /// 收件人为学生包的加密接收节点（节点标识与其公钥证书 DER）。
    /// </summary>
    public Task<ImportPipelineResult> PublishPaperImportAsync(
        string packageRoot,
        string examId,
        long expectedPaperVersion,
        string compilationDirectory,
        string packageOutputDirectory,
        IReadOnlyList<PackageRecipient> recipients,
        string reason,
        CancellationToken cancellationToken = default) =>
        SendAsync<ImportPipelineResult>(HttpMethod.Post, "api/v1/imports/publish", new
        {
            packageRoot,
            examId,
            expectedPaperVersion,
            compilationOutputDirectory = compilationDirectory,
            packageOutputDirectory,
            recipients,
            reason
        }, cancellationToken);
}
