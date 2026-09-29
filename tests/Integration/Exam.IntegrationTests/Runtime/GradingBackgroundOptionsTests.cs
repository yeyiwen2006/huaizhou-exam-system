using Exam.Server.Runtime;
using Microsoft.Extensions.Configuration;

namespace Exam.IntegrationTests.Runtime;

/// <summary>
/// 生产后台计分参数的校验：越界配置必须拒绝启动，不能静默退回默认值——
/// 否则「没在计分」会被误当成「没有待计分任务」。
/// </summary>
public sealed class GradingBackgroundOptionsTests
{
    [Fact]
    public void FromConfiguration_WithoutSettings_UsesBoundedDefaults()
    {
        var options = GradingBackgroundOptions.FromConfiguration(Configuration());

        Assert.Equal(32, options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(300), options.Lease);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.IdleDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.BusyDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), options.ErrorDelay);
    }

    [Fact]
    public void FromConfiguration_WithValidOverrides_AppliesThem()
    {
        var options = GradingBackgroundOptions.FromConfiguration(Configuration(
            ("Grading:BatchSize", "64"),
            ("Grading:LeaseSeconds", "60"),
            ("Grading:IdleMilliseconds", "1000")));

        Assert.Equal(64, options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Lease);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), options.IdleDelay);
    }

    [Theory]
    [InlineData("Grading:BatchSize", "0")]
    [InlineData("Grading:BatchSize", "513")]
    [InlineData("Grading:BatchSize", "-1")]
    [InlineData("Grading:LeaseSeconds", "0")]
    [InlineData("Grading:LeaseSeconds", "3601")]
    [InlineData("Grading:IdleMilliseconds", "5")]
    [InlineData("Grading:BusyMilliseconds", "60001")]
    [InlineData("Grading:ErrorMilliseconds", "0")]
    public void FromConfiguration_WithOutOfRangeValue_IsRejected(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() =>
            GradingBackgroundOptions.FromConfiguration(Configuration((key, value))));

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
