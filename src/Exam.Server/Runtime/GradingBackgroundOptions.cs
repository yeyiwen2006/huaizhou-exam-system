using Exam.Application.Scoring;

namespace Exam.Server.Runtime;

/// <summary>
/// D05 生产后台计分的运行参数。取值范围在此校验，配置错误直接拒绝启动，
/// 不静默退回默认值——否则「没在计分」会被误当成「没有待计分任务」。
/// </summary>
public sealed record GradingBackgroundOptions(
    int BatchSize,
    TimeSpan Lease,
    TimeSpan IdleDelay,
    TimeSpan BusyDelay,
    TimeSpan ErrorDelay)
{
    public const int MaxBatchSize = GradingCoordinator.MaxBatchSize;

    public static GradingBackgroundOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var batchSize = configuration.GetValue("Grading:BatchSize", 32);
        if (batchSize is < 1 or > MaxBatchSize)
            throw new InvalidOperationException($"Grading:BatchSize 必须在 1 至 {MaxBatchSize} 之间。");

        var leaseSeconds = configuration.GetValue("Grading:LeaseSeconds", 300);
        if (leaseSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Grading:LeaseSeconds 必须在 1 至 3600 之间。");

        return new GradingBackgroundOptions(
            batchSize,
            TimeSpan.FromSeconds(leaseSeconds),
            Delay(configuration, "Grading:IdleMilliseconds", 500),
            Delay(configuration, "Grading:BusyMilliseconds", 50),
            Delay(configuration, "Grading:ErrorMilliseconds", 2000));
    }

    private static TimeSpan Delay(IConfiguration configuration, string key, int fallback)
    {
        var milliseconds = configuration.GetValue(key, fallback);
        if (milliseconds is < 10 or > 60000)
            throw new InvalidOperationException($"{key} 必须在 10 至 60000 之间。");
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
