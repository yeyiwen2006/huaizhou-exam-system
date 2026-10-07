namespace Exam.Domain.Scoring;

/// <summary>某场考试当前已发布的计分规则版本。</summary>
public sealed record PublishedGradingVersion(string ExamId, long GradingVersion);

/// <summary>
/// 已发布计分版本的枚举入口，供生产后台按**实际已发布版本**逐场对账：
/// 重算范围必须由实际成绩版本与当前已发布版本共同决定，不能只看旧 outbox 状态。
///
/// 只读，不返回规则内容、答卷或答案；规则源数据仍由 <see cref="IGradingInputs"/> 按考试读取，
/// 因此对账本身不会改动任何答卷，也不会代替 版本服务 的当前版本校验。
/// </summary>
public interface IPublishedGradingDirectory
{
    /// <summary>列出已发布计分规则的考试及其当前版本，按考试标识稳定排序。</summary>
    ValueTask<IReadOnlyList<PublishedGradingVersion>> ListPublishedAsync(
        CancellationToken cancellationToken = default);
}
