using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Grading;
using Exam.Contracts.Operators;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;
using Exam.IntegrationTests.Importing;

namespace Exam.IntegrationTests.Cli;

/// <summary>Real CLI processes against a controlled loopback HTTP host, synthetic input only.</summary>
public sealed class CliBoundaryTests
{
    private static string Repository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    private static async Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        start.ArgumentList.Add(Path.Combine(Repository(), "src/Exam.Cli/bin/Release/net10.0/Exam.Cli.dll"));
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("EXAM_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["EXAM_ALLOW_INSECURE_LOOPBACK"] = "true";
        start.Environment["EXAM_OPERATOR_TOKEN"] = "synthetic-sensitive-sentinel";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("version")]
    [InlineData("error-codes")]
    public async Task PublicCommandsStaySuccessful(string command)
    {
        var result = await RunAsync(command);
        Assert.Equal(0,result.Exit); Assert.NotEmpty(result.Out); Assert.Empty(result.Error);
        if (command.Contains("help"))
        {
            Assert.Contains("grading status",result.Out);
            Assert.Contains("规则预览目录",result.Out);
            Assert.False(result.Out.StartsWith("\uFEFF", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("health", "--server", "https://localhost", "--server", "https://localhost")]
    [InlineData("health", "--server")]
    [InlineData("health", "--server", "https://synthetic-sensitive-sentinel\u0040localhost")]
    [InlineData("health", "--server", "https://localhost?synthetic-sensitive-sentinel")]
    [InlineData("health", "--server", "http://example.invalid")]
    [InlineData("api", "POST", "/test", "--server", "https://localhost", "--input", "missing-synthetic-sensitive-sentinel.json")]
    [InlineData("synthetic-sensitive-sentinel")]
    [InlineData("health", "--confirm")]
    [InlineData("grading", "status", "synthetic", "--confirm")]
    [InlineData("grading", "revise", "synthetic", "--confirm")]
    public async Task InvalidInputIsControlledAndNeverEchoed(params string[] arguments)
    {
        var result = await RunAsync(["--error-format","json",..arguments]);
        Assert.Equal(2,result.Exit); Assert.Empty(result.Out);
        Assert.DoesNotContain("synthetic-sensitive-sentinel",result.Error);
        using var error = JsonDocument.Parse(result.Error);
        Assert.Equal(2,error.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("INVALID_INPUT",error.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(200,0)] [InlineData(400,2)] [InlineData(401,3)] [InlineData(403,3)]
    [InlineData(404,4)] [InlineData(409,4)] [InlineData(500,6)] [InlineData(503,6)] [InlineData(302,4)]
    public async Task RealHttpResponsesMapToExitAndControlledStreams(int status,int exit)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/test",async context =>
        {
            context.Response.StatusCode=status;
            if(status==302) context.Response.Headers.Location="http://127.0.0.1:1/credential-target";
            await context.Response.WriteAsync("{\"result\":\"synthetic-sensitive-sentinel\"}");
        });
        await app.StartAsync();
        var result = await RunAsync("api","GET","/test","--server",app.Urls.Single(),"--error-format","json");
        Assert.Equal(exit,result.Exit);
        if(exit==0) { Assert.Contains("result",result.Out); Assert.Empty(result.Error); }
        else { Assert.Empty(result.Out); Assert.DoesNotContain("synthetic-sensitive-sentinel",result.Error); JsonDocument.Parse(result.Error).Dispose(); }
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("pending")]
    [InlineData("status-unavailable")]
    [InlineData("rejected")]
    public async Task GradingConfirmationAndControlledStatusWorkThroughRealCliWithoutNativeTools(string outcome)
    {
        using var source = new SyntheticImportFixture();
        var json = JsonDefaults.Create();
        var now = DateTimeOffset.UtcNow;
        var current = new ExamVersionSnapshot("source-exam", new(1, 1, 1), new(now, now.AddHours(2)),
            ExamLifecycle.Running, null, null,
            new(1, "synthetic-paper", ImportedGradingRules.Create(await File.ReadAllTextAsync(source.ManifestPath),
                await File.ReadAllTextAsync(source.AnswerPath)).Structure, [], now, "synthetic-operator", "initial"),
            new(1, "initial-rules", now, "synthetic-operator", "initial"), new Dictionary<string, ParticipantVersionStatus>());
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        IResult Envelope<T>(T value) => Results.Json(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1,
            "synthetic-request", value, null), json);
        IResult Failure() => Results.Json(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1,
            "synthetic-request", null, new ApiError("VERSION_CONFLICT", "synthetic-private-response")), json, statusCode: 409);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/v1/versioning/exams/source-exam", () => Envelope(current));
        app.MapGet("/api/v1/operators/me", () => Envelope(new OperatorAccount("synthetic-operator", "合成考务", OperatorStatus.Active, true, now, null)));
        app.MapGet("/api/v1/exams", () => Envelope(new[] { new ExamSummary(current.ExamId, source.Name,
            current.Schedule.StartsAt, current.Schedule.EndsAt, ExamLifecycle.Running, false, false, 0, 0, 1) }));
        app.MapPost("/api/v1/versioning/exams/source-exam/grading-publications", async (HttpContext context) =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            requests.Enqueue(body);
            if (outcome == "rejected") return Failure();
            var revision = JsonSerializer.Deserialize<PublishGradingRulesRequest>(body, json)!;
            current = current with { Versions = new(1, 1, 2), Grading = new(2, revision.RuleSetId, now, "synthetic-operator", revision.Reason) };
            return Envelope(current);
        });
        app.MapGet("/api/v1/grading/source-exam/status", () => outcome == "status-unavailable" ? Failure()
            : Envelope(new GradingStatusReport("source-exam", 2, 1, outcome == "ready" ? 1 : 0,
                new GradingReadiness(2, outcome == "ready", outcome == "ready" ? [] : [new RegradeItem("synthetic-submission", RegradeReason.StaleVersion)], []), [], now)));
        await app.StartAsync();
        var address = app.Urls.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var api = new HttpExamPublicationClient(http);
        var output = Path.Combine(source.Root, "rules-preview");
        using (var prepared = await PreparedGradingRevision.PrepareAsync(api, current.ExamId, source.Source, "合成命令接线"))
            await prepared.ExportAsync(output);
        var unconfirmed = await RunAsync("grading", "publish", output, "--server", address);
        Assert.Equal(2, unconfirmed.Exit); Assert.Empty(requests);
        var duplicate = await RunAsync("grading", "publish", output, "--server", address, "--confirm", "--confirm");
        Assert.Equal(2, duplicate.Exit); Assert.Empty(requests);
        var published = await RunAsync("--error-format", "json", "grading", "publish", output, "--server", address, "--confirm");
        Assert.Single(requests);
        Assert.DoesNotContain("synthetic-private-response", published.Out + published.Error);
        Assert.DoesNotContain("synthetic-sensitive-sentinel", published.Out + published.Error);
        Assert.DoesNotContain("correctOptionIds", published.Out + published.Error);
        if (outcome == "rejected")
        {
            Assert.Equal(4, published.Exit); Assert.Empty(published.Out);
            Assert.DoesNotContain("规则发布成功", published.Error);
            using var failure = JsonDocument.Parse(published.Error);
            Assert.Contains("保留原预览目录", failure.RootElement.GetProperty("message").GetString());
            return;
        }
        Assert.Equal(0, published.Exit);
        using var receipt = JsonDocument.Parse(published.Out);
        Assert.Equal(2, receipt.RootElement.GetProperty("result").GetProperty("versions").GetProperty("gradingVersion").GetInt64());
        Assert.Contains("规则发布成功：规则版本 2", published.Error);
        Assert.Contains(outcome == "ready" ? "结果就绪" : outcome == "pending" ? "重算中" : "发布结果不受影响", published.Error);
        var replay = await RunAsync("grading", "publish", output, "--confirm", "--server", address);
        Assert.Equal(0, replay.Exit); Assert.Equal(2, requests.Count);
        Assert.Equal(requests.First(), requests.Last());
        if (outcome is "ready" or "pending")
        {
            var status = await RunAsync("grading", "status", source.Name, "--server", address);
            Assert.Equal(0, status.Exit); JsonDocument.Parse(status.Out).Dispose();
            Assert.Contains(outcome == "ready" ? "结果就绪" : "重算中", status.Error);
        }
    }

    [Fact]
    public async Task DeadlineIsNetworkFailureAndMalformedJsonNeverSent()
    {
        var calls=0;
        var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();
        app.MapMethods("/slow",["GET","POST"],async context => { Interlocked.Increment(ref calls); await Task.Delay(3000,context.RequestAborted); });
        await app.StartAsync();
        var result=await RunAsync("api","GET","/slow","--server",app.Urls.Single(),"--timeout-seconds","1","--error-format","json");
        Assert.Equal(5,result.Exit); Assert.Contains("NETWORK_UNAVAILABLE",result.Error);
        var path=Path.Combine(Path.GetTempPath(),"synthetic-input-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            await File.WriteAllTextAsync(path,"{synthetic-sensitive-sentinel");
            result=await RunAsync("api","POST","/slow","--server",app.Urls.Single(),"--input",path,"--error-format","json");
            Assert.Equal(2,result.Exit); Assert.Equal(1,calls); Assert.DoesNotContain("synthetic-sensitive-sentinel",result.Error);
        }
        finally { File.Delete(path); }
    }
}
