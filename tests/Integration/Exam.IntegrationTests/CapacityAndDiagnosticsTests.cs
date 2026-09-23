using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Server.Capacity;
using Exam.Server.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Exam.IntegrationTests;

public sealed class CapacityAndDiagnosticsTests
{
    private static readonly ServerNodeEnvelope Server = new("synthetic-server", "node-a", 1);

    [Fact]
    public void ProductionRequiresExplicitBudget_AndMultipliesEveryProcess()
    {
        var missing = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => DatabaseCapacityOptions.Read(missing, 16, true));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:MaximumPoolSize"] = "32",
            ["Database:Capacity:PostgresMaxConnections"] = "100",
            ["Database:Capacity:PostgresReservedConnections"] = "3",
            ["Database:Capacity:MaximumServerProcesses"] = "2",
            ["Database:Capacity:OtherConnections"] = "5",
            ["Database:Capacity:OperationsReserveConnections"] = "10"
        }).Build();
        var budget = DatabaseCapacityOptions.Read(configuration, 32, true);
        Assert.Equal(82, budget.RequiredConnections);
        Assert.Null(budget.ValidateActual(100, 3));
        Assert.NotNull(budget.ValidateActual(100, 6));
        Assert.NotNull(budget.ValidateActual(80, 3));
        Assert.Throws<InvalidOperationException>(() => (budget with { MaximumServerProcesses = 3 }).Validate());
        Assert.Throws<InvalidOperationException>(() => (budget with { OperationsReserveConnections = 0 }).Validate());
    }

    [Fact]
    public void DevelopmentBudgetDoesNotPretendToBeProductionValidation()
    {
        var budget = DatabaseCapacityOptions.Read(new ConfigurationBuilder().Build(), 16, false);
        Assert.False(budget.RequireActualDatabaseMatch);
        Assert.NotNull(budget.ValidateActual(20, 3));
        Assert.Null(budget.ValidateActual(100, 3));
    }

    [Fact]
    public void DownloadsAndApiCannotConsumeAllConfiguredPoolCapacity()
    {
        Assert.Throws<InvalidOperationException>(() => new RequestCapacityOptions(8, 12, 4).Validate(16));
        RequestCapacityOptions.Read(new ConfigurationBuilder().Build(), 16).Validate(16);
    }

    [Theory]
    [InlineData("GET", "/api/v1/resources/package-one", RequestWorkload.Resource)]
    [InlineData("POST", "/api/v1/resources/unlock", RequestWorkload.Critical)]
    [InlineData("POST", "/api/v1/submissions/final", RequestWorkload.Critical)]
    [InlineData("PUT", "/api/v1/submissions/progress", RequestWorkload.Api)]
    [InlineData("POST", "/api/v1/commands/acknowledgements", RequestWorkload.Critical)]
    [InlineData("POST", "/hubs/exam-events/negotiate", RequestWorkload.Api)]
    [InlineData("GET", "/hubs/exam-events", RequestWorkload.Unrestricted)]
    public void WorkloadPartitionsKeepLongConnectionsOutOfHttpPermits(string method, string path, RequestWorkload expected)
    {
        var context = Context(path);
        context.Request.Method = method;
        Assert.Equal(expected, RequestWorkloadClassifier.Classify(context.Request));
    }

    [Fact]
    public async Task QueueIsBounded_RejectedRequestDoesNotReachBusiness_AndOtherWorkloadStillRuns()
    {
        var options = new RequestCapacityOptions(1, 1, 1, QueueLimit: 1);
        using var admission = new RequestAdmissionControl(options);
        using var metrics = new ExamRuntimeMetrics();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceCalls = 0;
        var criticalCalls = 0;
        var middleware = new RequestAdmissionMiddleware(async context =>
        {
            if (RequestWorkloadClassifier.Classify(context.Request) == RequestWorkload.Critical)
            { Interlocked.Increment(ref criticalCalls); return; }
            Interlocked.Increment(ref resourceCalls);
            entered.TrySetResult();
            await release.Task;
        });
        var first = middleware.InvokeAsync(Context("/api/v1/resources/secret-question-one"), admission, options, metrics, Server);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = middleware.InvokeAsync(Context("/api/v1/resources/secret-question-two"), admission, options, metrics, Server);
        try
        {
            var rejected = Context("/api/v1/resources/secret-question-three");
            await middleware.InvokeAsync(rejected, admission, options, metrics, Server);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, rejected.Response.StatusCode);
            Assert.Equal("1", rejected.Response.Headers.RetryAfter);
            rejected.Response.Body.Position = 0;
            using var body = await JsonDocument.ParseAsync(rejected.Response.Body);
            Assert.Equal("synthetic-server", body.RootElement.GetProperty("serverId").GetString());
            Assert.True(body.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean());
            Assert.Null(body.RootElement.GetProperty("result").Deserialize<object>());
            Assert.Equal(1, resourceCalls);
            Assert.Equal(1, metrics.Snapshot().Single(item => item.Workload == "resource").Queued);

            await middleware.InvokeAsync(Context("/api/v1/submissions/final"), admission, options, metrics, Server);
            Assert.Equal(1, criticalCalls);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, resourceCalls);
        var snapshot = metrics.Snapshot().Single(item => item.Workload == "resource");
        Assert.Equal(0, snapshot.Active);
        Assert.Equal(0, snapshot.Queued);
        Assert.Equal(1, snapshot.PeakActive);
        Assert.Equal(1, snapshot.Rejected);
        Assert.DoesNotContain("secret-question", JsonSerializer.Serialize(metrics.Snapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueTimeoutOrClientCancellationReleasesQueueWithoutExecutingBusiness(bool abortClient)
    {
        var options = new RequestCapacityOptions(1, 1, 1, QueueLimit: 1, QueueTimeoutMilliseconds: 25);
        using var admission = new RequestAdmissionControl(options);
        using var metrics = new ExamRuntimeMetrics();
        using var occupied = await admission.AcquireAsync(RequestWorkload.Api, CancellationToken.None);
        var executions = 0;
        var middleware = new RequestAdmissionMiddleware(_ => { executions++; return Task.CompletedTask; });
        var context = Context("/api/v1/student/exams");
        using var cancelled = new CancellationTokenSource();
        context.RequestAborted = cancelled.Token;
        var pending = middleware.InvokeAsync(context, admission, options, metrics, Server);
        if (abortClient) cancelled.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, executions);
        Assert.Equal(0, metrics.Snapshot().Single(item => item.Workload == "api").Queued);
        if (!abortClient) Assert.Equal(503, context.Response.StatusCode);
        else Assert.Equal(0, context.Response.Body.Length);
        occupied.Dispose();
        await middleware.InvokeAsync(Context("/api/v1/student/exams"), admission, options, metrics, Server);
        Assert.Equal(1, executions);
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
