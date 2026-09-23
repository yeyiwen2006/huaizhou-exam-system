using Exam.Infrastructure.Postgres;

namespace Exam.UnitTests.ServiceManagement;

public sealed class PostgresConnectionOptionsTests
{
    [Fact]
    public void MissingPoolValueUsesExplicitBoundedDevelopmentFallback()
    {
        var settings = new PostgresConnectionOptions("Host=localhost;Database=synthetic;Username=test;Password=synthetic-secret")
            .BuildConnectionSettings();
        Assert.Equal(PostgresConnectionOptions.DevelopmentMaximumPoolSize, settings.MaxPoolSize);
        Assert.Equal(0, settings.MinPoolSize);
        Assert.Equal(15, settings.Timeout);
        Assert.True(settings.Pooling);
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.LogParameters);
        Assert.Equal("Exam.BusinessDatabase", PostgresConnectionOptions.MetricsPoolName);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(100)]
    public void ExistingExplicitConnectionStringPoolIsPreservedForTestAndDirectConsumers(int maximum)
    {
        var settings = new PostgresConnectionOptions($"Host=localhost;Database=synthetic;Maximum Pool Size={maximum}")
            .BuildConnectionSettings();
        Assert.Equal(maximum, settings.MaxPoolSize);
    }

    [Theory]
    [InlineData("Host=localhost;Pooling=false", 16, 0)]
    [InlineData("Host=localhost;Maximum Pool Size=32", 16, 0)]
    [InlineData("Host=localhost", 0, 0)]
    [InlineData("Host=localhost", 16, 17)]
    public void UnboundedOrContradictorySettingsAreRejected(string connectionString, int maximum, int minimum)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PostgresConnectionOptions(connectionString,
            MaximumPoolSize: maximum, MinimumPoolSize: minimum).BuildConnectionSettings());
    }
}
