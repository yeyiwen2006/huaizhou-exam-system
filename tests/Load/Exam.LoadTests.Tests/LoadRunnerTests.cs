using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.LoadTests.Tests;

public sealed partial class LoadRunnerTests
{
    private static string FreshPath() => Path.Combine(Path.GetTempPath(), "exam-load-tests-" + Guid.NewGuid().ToString("N"));
    private static LoadOptions Options(string scenario = "burst") => new(new("http://127.0.0.1:5089"), "unused", scenario, FreshPath(), 1, 2, true);
    private static LoadSession Session() => new()
    {
        ExamId = "synthetic-exam", CandidateId = "candidate", ParticipationId = "participation", DeviceId = "device",
        EntryVersion = 1, AuthorizationVersion = 1, PaperVersion = 1, Credential = "private-test-credential"
    };
    private static LoadManifest Manifest() => new() { Synthetic = true, ServerId = "server", ServerEpoch = 1, Sessions = [Session()] };

    [Theory]
    [InlineData("http://example.test", true)]
    [InlineData("http://127.0.0.1:5089", false)]
    [InlineData("https://user:secret@example.test", false)]
    [InlineData("https://example.test/path", false)]
    [InlineData("https://example.test?token=secret", false)]
    public void UnsafeTargetsAreRejected(string target, bool allowHttp)
    {
        var args = new List<string> { "--target", target, "--sessions", "synthetic.json", "--scenario", "burst", "--output", FreshPath(), "--confirm-synthetic" };
        if (allowHttp) args.Add("--allow-loopback-http");
        Assert.Throws<ArgumentException>(() => LoadOptions.Parse(args.ToArray()));
    }

    [Fact]
    public void ExplicitSyntheticConfirmationIsRequired() => Assert.Throws<ArgumentException>(() => LoadOptions.Parse(
        ["--target", "https://example.test", "--sessions", "input", "--scenario", "burst", "--output", FreshPath()]));

    [Fact]
    public void DuplicateIdentitiesAndSmallPayloadAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LoadManifest { Synthetic = true, ServerId = "s", ServerEpoch = 1, Sessions = [Session(), Session()] }.Validate(Options()));
        Assert.Throws<ArgumentException>(() => new LoadManifest { Synthetic = true, ServerId = "s", ServerEpoch = 1, Sessions = [Session()], QuestionCount = 119 }.Validate(Options()));
    }

    [Fact]
    public void PercentilesUseNearestRankAndIncludeFailures()
    {
        Assert.Equal(95, LoadReport.Percentile(Enumerable.Range(1, 100).Select(n => (double)n), .95));
        var path = FreshPath();
        using (var report = new LoadReport(path))
        {
            report.Add(new("burst", 0, true, 1, 0, null, DateTimeOffset.UtcNow));
            report.Add(new("burst", 1, false, 3000, 0, "timeout", DateTimeOffset.UtcNow));
            var summary = report.Summarize()["burst"];
            Assert.Equal(1, summary.Failed);
            Assert.Equal(3000, summary.P95Ms);
            Assert.False(summary.LatencyTargetMet);
        }
        Directory.Delete(path, true);
    }

    [Fact]
    public async Task BurstUsesRealContractAndVerifiesAllAnswers()
    {
        var options = Options();
        var calls = 0;
        var time = new SyntheticTimeProvider();
        using var handler = new Handler(async request =>
        {
            Assert.Equal("private-test-credential", request.Headers.GetValues("X-Exam-Device-Credential").Single());
            calls++;
            time.Advance(TimeSpan.FromMilliseconds(25));
            if (request.RequestUri!.AbsolutePath.EndsWith("/time"))
                return Envelope(new ServerTimeSnapshot(DateTimeOffset.UtcNow, new("server", "node", 1)));
            var body = await request.Content!.ReadFromJsonAsync<SyncProgressRequest>(LoadJson.Options);
            Assert.Equal(120, body!.Answers.Count);
            return Envelope(new PersistedProgress(body.ExamId, body.CandidateId, body.ParticipationId, body.EntryVersion,
                body.DeviceId, body.AuthorizationVersion, body.AnswerVersion, body.PaperVersion, body.Answers, body.CurrentQuestionId, DateTimeOffset.UtcNow));
        });
        await using (var runner = new LoadRunner(options, Manifest(), handler, time)) Assert.Equal(0, await runner.RunAsync(default));
        Assert.Equal(2, calls);
        var report = await File.ReadAllTextAsync(Path.Combine(options.OutputDirectory, "summary.json"));
        Assert.DoesNotContain("private-test-credential", report);
        Assert.Contains("\"productionCapacityCertified\": false", report);
        Directory.Delete(options.OutputDirectory, true);
    }

    [Fact]
    public async Task Http200WithWrongAnswerVersionFails()
    {
        var options = Options();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/time")) return Envelope(new ServerTimeSnapshot(DateTimeOffset.UtcNow, new("server", "node", 1)));
            var body = (await request.Content!.ReadFromJsonAsync<SyncProgressRequest>(LoadJson.Options))!;
            return Envelope(new PersistedProgress(body.ExamId, body.CandidateId, body.ParticipationId, body.EntryVersion,
                body.DeviceId, body.AuthorizationVersion, 0, body.PaperVersion, body.Answers, body.CurrentQuestionId, DateTimeOffset.UtcNow));
        });
        await using (var runner = new LoadRunner(options, Manifest(), handler)) Assert.Equal(1, await runner.RunAsync(default));
        Directory.Delete(options.OutputDirectory, true);
    }

    [Fact]
    public async Task FinalChecksOriginalReceiptAndReplaysIdenticalFrozenRequest()
    {
        var options = Options("final");
        string? firstBody = null;
        var posts = 0;
        var gets = 0;
        var receipt = new SubmissionReceipt("receipt", "load-final-participation-1", "synthetic-exam", "candidate", "participation", DateTimeOffset.UtcNow, 1, 1);
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                var body = await request.Content!.ReadAsStringAsync();
                if (firstBody is null) firstBody = body; else Assert.Equal(firstBody, body);
            }
            else gets++;
            return Envelope(receipt);
        });
        await using (var runner = new LoadRunner(options, Manifest(), handler)) Assert.Equal(0, await runner.RunAsync(default));
        Assert.Equal(2, posts);
        Assert.Equal(1, gets);
        Directory.Delete(options.OutputDirectory, true);
    }

    [Fact]
    public async Task NetworkFailuresAreReportedNotSilentlyRetried()
    {
        var options = Options();
        var calls = 0;
        using var handler = new Handler(_ => { calls++; throw new HttpRequestException("secret-in-exception", null, HttpStatusCode.ServiceUnavailable); });
        await using (var runner = new LoadRunner(options, Manifest(), handler)) Assert.Equal(1, await runner.RunAsync(default));
        var samples = await File.ReadAllTextAsync(Path.Combine(options.OutputDirectory, "samples.jsonl"));
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret-in-exception", samples);
        Assert.Contains("HTTP-503", samples);
        Directory.Delete(options.OutputDirectory, true);
    }

    [Fact]
    public async Task FinalPayloadIsStableWhenSessionManifestOrderChanges()
    {
        var first = Session();
        var second = new LoadSession { ExamId = first.ExamId, CandidateId = "candidate-2", ParticipationId = "participation-2",
            DeviceId = "device-2", Credential = "private-2", EntryVersion = 1, AuthorizationVersion = 1, PaperVersion = 1 };
        var bodies = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var receipts = new System.Collections.Concurrent.ConcurrentDictionary<string, SubmissionReceipt>();
        foreach (var sessions in new[] { new[] { first, second }, new[] { second, first } })
        {
            var options = Options("final");
            using var handler = new Handler(async request =>
            {
                var device = request.Headers.GetValues("X-Exam-Device-Id").Single();
                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadAsStringAsync();
                    Assert.Equal(bodies.GetOrAdd(device, body), body);
                    var final = JsonSerializer.Deserialize<SubmitFinalAnswersRequest>(body, LoadJson.Options)!;
                    receipts.TryAdd(device, new("receipt-" + device, final.SubmissionId, final.ExamId, final.CandidateId,
                        final.ParticipationId, DateTimeOffset.UtcNow, final.PaperVersion, 1));
                }
                return Envelope(receipts[device]);
            });
            var manifest = new LoadManifest { Synthetic = true, ServerId = "server", ServerEpoch = 1, Sessions = sessions };
            await using (var runner = new LoadRunner(options, manifest, handler)) Assert.Equal(0, await runner.RunAsync(default));
            Directory.Delete(options.OutputDirectory, true);
        }
    }

    [Fact]
    public void TransportProbeDoesNotClaimUnmeasuredBusinessLatencyTarget()
    {
        var path = FreshPath();
        using (var report = new LoadReport(path))
        {
            report.Add(new("command-received", 0, true, 4000, 0, null, DateTimeOffset.UtcNow));
            Assert.Null(report.Summarize()["command-received"].LatencyTargetMet);
        }
        Directory.Delete(path, true);
    }

    [Fact]
    public async Task FixedCadenceReportsMissedArrivalsInsteadOfReducingOfferedLoad()
    {
        var options = Options("steady") with { DurationSeconds = 3, TimeoutSeconds = 5 };
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/time")) return Envelope(new ServerTimeSnapshot(DateTimeOffset.UtcNow, new("server", "node", 1)));
            await Task.Delay(2500);
            var body = (await request.Content!.ReadFromJsonAsync<SyncProgressRequest>(LoadJson.Options))!;
            return Envelope(new PersistedProgress(body.ExamId, body.CandidateId, body.ParticipationId, body.EntryVersion,
                body.DeviceId, body.AuthorizationVersion, body.AnswerVersion, body.PaperVersion, body.Answers, body.CurrentQuestionId, DateTimeOffset.UtcNow));
        });
        await using (var runner = new LoadRunner(options, Manifest(), handler)) Assert.Equal(1, await runner.RunAsync(default));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.OutputDirectory, "summary.json")));
        Assert.Equal(1, report.RootElement.GetProperty("operations").GetProperty("missed-progress-arrival").GetProperty("failed").GetInt32());
        Directory.Delete(options.OutputDirectory, true);
    }

    [Fact]
    public async Task DownloadRejectsTruncatedBodyEvenWithHttp200()
    {
        var source = Session();
        var session = new LoadSession { ExamId = source.ExamId, CandidateId = source.CandidateId, ParticipationId = source.ParticipationId,
            DeviceId = source.DeviceId, Credential = source.Credential, EntryVersion = 1, AuthorizationVersion = 1, PaperVersion = 1,
            ResourcePath = "/api/v1/resources/test", ExpectedResourceBytes = 100 };
        var manifest = new LoadManifest { Synthetic = true, ServerId = "server", ServerEpoch = 1, Sessions = [session] };
        var options = Options("download");
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[99]) };
            response.Headers.Add("X-Exam-Server-Id", "server");
            response.Headers.Add("X-Exam-Server-Epoch", "1");
            response.Headers.Add("X-Exam-Paper-Version", "1");
            return Task.FromResult(response);
        });
        await using (var runner = new LoadRunner(options, manifest, handler)) Assert.Equal(1, await runner.RunAsync(default));
        Directory.Delete(options.OutputDirectory, true);
    }

    private static HttpResponseMessage Envelope<T>(T result) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>("server", "node", 1, "correlation", result, null), options: LoadJson.Options)
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
