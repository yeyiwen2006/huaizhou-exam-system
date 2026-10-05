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
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;
using Exam.IntegrationTests.Importing;

namespace Exam.IntegrationTests.Cli;

/// <summary>Real CLI processes against a controlled loopback HTTP host, synthetic input only.</summary>
public sealed class CliBoundaryTests
{
    private static Task<(int Exit, string Out, string Error)> RunAsync(params string[] args) =>
        CliProcess.RunAsync(args);

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

    /// <summary>
    /// 需求“全部实际参试答卷持久保存后 GUI 弹窗、CLI 发出对应事件；每个名单版本一次，零参试者不触发”：
    /// 真 CLI 进程对环回宿主验证 watch（单次，可指定起始名单版本）与 follow（按间隔、可限轮数）。
    /// </summary>
    [Fact]
    public async Task MonitorWatchAndFollowEmitEachCollectionEventOnce()
    {
        var json = JsonDefaults.Create();
        var completedAt = new DateTimeOffset(2026, 10, 5, 11, 2, 30, TimeSpan.FromHours(8));
        var events = new[]
        {
            new CollectionCompletedNotification("event-a", "collection-exam", 1, 12, completedAt),
            new CollectionCompletedNotification("event-b", "collection-exam", 2, 13, completedAt.AddMinutes(18)),
        };
        var cursors = new List<long>();
        IResult Envelope<T>(T value) => Results.Json(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1,
            "synthetic-request", value, null), json);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/v1/exams", () => Envelope(new[]
        {
            new ExamSummary("collection-exam", "合成回收考试", DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(2), ExamLifecycle.Running, false, false, 13, 13, 1),
        }));
        app.MapGet("/api/v1/submissions/collection/events", (string examId, long afterRosterVersion) =>
        {
            Assert.Equal("collection-exam", examId);
            lock (cursors) cursors.Add(afterRosterVersion);
            return Envelope(events.Where(item => item.RosterVersion > afterRosterVersion).ToArray());
        });
        await app.StartAsync();
        var address = app.Urls.Single();

        var watch = await RunAsync("monitor", "watch", "合成回收考试", "--server", address);
        Assert.Equal(0, watch.Exit);
        Assert.Equal(new[] { "event-a", "event-b" }, Emitted(watch.Out, json).Select(item => item.EventId));
        Assert.Contains("全部答卷已成功回收", watch.Error, StringComparison.Ordinal);
        Assert.Contains("12 名实际参试考生", watch.Error, StringComparison.Ordinal);

        // 位置参数指定起始名单版本：只发出之后的新事件，不重复通知。
        var tail = await RunAsync("monitor", "watch", "合成回收考试", "1", "--server", address);
        Assert.Equal(0, tail.Exit);
        Assert.Equal("event-b", Assert.Single(Emitted(tail.Out, json)).EventId);

        // follow：位置参数依次是起始名单版本、间隔秒数、轮数；达到轮数后正常退出，同一名单版本只发出一次。
        var follow = await RunAsync("monitor", "follow", "合成回收考试", "0", "1", "1", "--server", address);
        Assert.Equal(0, follow.Exit);
        Assert.Equal(new[] { "event-a", "event-b" }, Emitted(follow.Out, json).Select(item => item.EventId));
        // 可读提醒要实时写 stderr（不能等进程退出、也不能因退出码非零而被丢弃）。
        Assert.Contains("全部答卷已成功回收", follow.Error, StringComparison.Ordinal);

        // 指定起始名单版本：持续监控也不重播历史。
        var followFrom = await RunAsync("monitor", "follow", "合成回收考试", "1", "1", "1", "--server", address);
        Assert.Equal(0, followFrom.Exit);
        Assert.Equal("event-b", Assert.Single(Emitted(followFrom.Out, json)).EventId);

        // 位置参数非法时是用例错误（退出码 2），不发请求。
        var badInterval = await RunAsync("monitor", "follow", "合成回收考试", "0", "0", "--server", address);
        Assert.Equal(2, badInterval.Exit);
        var tooLongInterval = await RunAsync("monitor", "follow", "合成回收考试", "0", "5000000", "--server", address);
        Assert.Equal(2, tooLongInterval.Exit);
        var badRounds = await RunAsync("monitor", "follow", "合成回收考试", "0", "1", "0", "--server", address);
        Assert.Equal(2, badRounds.Exit);
        var badStart = await RunAsync("monitor", "watch", "合成回收考试", "-1", "--server", address);
        Assert.Equal(2, badStart.Exit);
        var missingName = await RunAsync("monitor", "watch", "--server", address);
        Assert.Equal(2, missingName.Exit);
    }

    /// <summary>
    /// 服务器错误正文绝不能进终端（错误层就是为此存在的），且非 JSON 的 5xx 正文要按 5xx 处理而不是输入错误。
    /// </summary>
    [Theory]
    [InlineData(503, "<html>SENSITIVE-LEAK-12345</html>")]
    [InlineData(200, "{SENSITIVE-LEAK-12345")]
    [InlineData(200, "null")]
    public async Task MonitorNeverPrintsServerErrorTextAndRetriesInvalidResponses(int status, string invalid)
    {
        var json = JsonDefaults.Create();
        var completedAt = new DateTimeOffset(2026, 10, 5, 11, 2, 30, TimeSpan.FromHours(8));
        var valid = new CollectionCompletedNotification("event-a", "collection-exam", 1, 12, completedAt);
        var calls = 0;
        IResult Ok<T>(T value) => Results.Json(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1,
            "synthetic-request", value, null), json);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/v1/exams", () => Ok(new[]
        {
            new ExamSummary("collection-exam", "合成回收考试", DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(2), ExamLifecycle.Running, false, false, 12, 12, 1),
        }));
        app.MapGet("/api/v1/submissions/collection/events", (string examId, long afterRosterVersion) =>
        {
            // 第一次返回网关样式的 503（HTML 正文），之后正常：follow 必须重试而不是当成输入错误退出。
            if (Interlocked.Increment(ref calls) == 1)
                return Results.Content(invalid, "application/json", statusCode: status);
            return Ok(new[] { valid }.Where(item => item.RosterVersion > afterRosterVersion).ToArray());
        });
        await app.StartAsync();
        var address = app.Urls.Single();

        var follow = await RunAsync("monitor", "follow", "合成回收考试", "0", "1", "2", "--server", address);
        Assert.Equal(0, follow.Exit);
        Assert.Equal("event-a", Assert.Single(Emitted(follow.Out, json)).EventId);
        Assert.Contains("收齐事件读取失败", follow.Error, StringComparison.Ordinal);
        // 服务器正文（含网关页里的敏感串）不得出现在终端。
        Assert.DoesNotContain("SENSITIVE-LEAK-12345", follow.Out + follow.Error);
    }

    /// <summary>
    /// --error-format json 时 stderr 必须保持“只有一个 JSON 错误信封”，实时提醒不能混进去。
    /// </summary>
    [Fact]
    public async Task MonitorFollowKeepsStderrSingleJsonWhenJsonErrorsAreRequested()
    {
        var json = JsonDefaults.Create();
        var completedAt = new DateTimeOffset(2026, 10, 5, 11, 2, 30, TimeSpan.FromHours(8));
        var valid = new CollectionCompletedNotification("event-a", "collection-exam", 1, 12, completedAt);
        var calls = 0;
        IResult Envelope<T>(T value) => Results.Json(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1,
            "synthetic-request", value, null), json);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/v1/exams", () => Envelope(new[]
        {
            new ExamSummary("collection-exam", "合成回收考试", DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(2), ExamLifecycle.Running, false, false, 12, 12, 1),
        }));
        app.MapGet("/api/v1/submissions/collection/events", (string examId, long afterRosterVersion) =>
            Interlocked.Increment(ref calls) == 1
                ? Envelope(new[] { valid })
                : Results.Json(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "synthetic-request",
                    null, new ApiError("ACCESS_DENIED", "synthetic-private-response")), json, statusCode: 401));
        await app.StartAsync();
        var address = app.Urls.Single();

        var follow = await RunAsync("monitor", "follow", "合成回收考试", "0", "1", "2",
            "--error-format", "json", "--server", address);

        // 401 不可重试，按退出码约定失败；stderr 必须仍是单个 JSON 文档（提醒不得混入）。
        Assert.Equal(3, follow.Exit);
        using var document = JsonDocument.Parse(follow.Error);
        Assert.Equal("ACCESS_DENIED", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("全部答卷已成功回收", follow.Error, StringComparison.Ordinal);
        Assert.Equal("event-a", Assert.Single(Emitted(follow.Out, json)).EventId);
    }

    /// <summary>
    /// 单页整页无效不能让长时运行的持续监控退出：与考务端弹窗同口径，游标不前进、提示后继续等待。
    /// </summary>
    [Fact]
    public async Task MonitorFollowKeepsRunningWhenAPageIsRejected()
    {
        var json = JsonDefaults.Create();
        var completedAt = new DateTimeOffset(2026, 10, 5, 11, 2, 30, TimeSpan.FromHours(8));
        var valid = new CollectionCompletedNotification("event-a", "collection-exam", 1, 12, completedAt);
        var calls = 0;
        IResult Envelope<T>(T value) => Results.Json(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1,
            "synthetic-request", value, null), json);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/v1/exams", () => Envelope(new[]
        {
            new ExamSummary("collection-exam", "合成回收考试", DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddHours(2), ExamLifecycle.Running, false, false, 12, 12, 1),
        }));
        app.MapGet("/api/v1/submissions/collection/events", (string examId, long afterRosterVersion) =>
        {
            // 第一页混入别的考试的事件（整页拒绝），之后恢复正常。
            var page = Interlocked.Increment(ref calls) == 1
                ? new[] { valid with { ExamId = "other-exam" } }
                : new[] { valid }.Where(item => item.RosterVersion > afterRosterVersion).ToArray();
            return Envelope(page);
        });
        await app.StartAsync();
        var address = app.Urls.Single();

        var follow = await RunAsync("monitor", "follow", "合成回收考试", "0", "1", "3", "--server", address);

        Assert.Equal(0, follow.Exit);
        Assert.Contains("收齐事件读取失败", follow.Error, StringComparison.Ordinal);
        Assert.Equal("event-a", Assert.Single(Emitted(follow.Out, json)).EventId);
    }

    [Fact]
    public async Task MonitorFollow_ContinuousFailureCannotSucceedAtTheRoundLimitOrDeadline()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/v1/exams", () => Results.Json(new ApiEnvelope<ExamSummary[]>("synthetic-server", "node", 1, "request",
            [new("exam", "合成考试", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(2), ExamLifecycle.Running,
                false, false, 1, 0, 1)], null), JsonDefaults.Create()));
        var calls = 0;
        app.MapGet("/api/v1/submissions/collection/events", () =>
        {
            Interlocked.Increment(ref calls);
            return Results.StatusCode(503);
        });
        await app.StartAsync();
        var result = await RunAsync("monitor", "follow", "合成考试", "0", "1", "1", "--server", app.Urls.Single(),
            "--timeout-seconds", "3", "--error-format", "json");
        Assert.Equal(5, result.Exit);
        Assert.Empty(result.Out);
        Assert.True(calls >= 2);
        using var error = JsonDocument.Parse(result.Error);
        Assert.Equal("NETWORK_UNAVAILABLE", error.RootElement.GetProperty("code").GetString());
    }

    private static CollectionCompletedNotification[] Emitted(string output, JsonSerializerOptions json) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<CollectionCompletedNotification>(line, json)!)
            .ToArray();
}
