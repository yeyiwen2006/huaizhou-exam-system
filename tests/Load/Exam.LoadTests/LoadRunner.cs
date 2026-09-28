using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.LoadTests;

public sealed partial class LoadRunner : IAsyncDisposable
{
    private readonly LoadOptions _options;
    private readonly LoadManifest _manifest;
    private readonly HttpClient _http;
    private readonly LoadReport _report;
    private readonly TimeProvider _timeProvider;
    private readonly ElapsedClock _clock;
    private sealed class ElapsedClock(TimeProvider time)
    {
        private readonly long _started = time.GetTimestamp();
        public TimeSpan Elapsed => time.GetElapsedTime(_started);
    }
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private readonly long[] _versions;

    public LoadRunner(LoadOptions options, LoadManifest manifest, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clock = new ElapsedClock(_timeProvider);
        _options = options;
        _manifest = manifest;
        _versions = manifest.Sessions.Select(s => s.InitialAnswerVersion).ToArray();
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, MaxConnectionsPerServer = Math.Max(300, manifest.Sessions.Length),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }) { BaseAddress = options.Target, Timeout = Timeout.InfiniteTimeSpan };
        _report = new(options.OutputDirectory);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var interrupted = false;
        try
        {
            if (_options.Scenario == "steady") await SteadyAsync(ct);
            else if (_options.Scenario == "commands") await CommandsAsync(ct);
            else if (_options.Scenario == "mixed") await MixedAsync(ct);
            else
            {
                var due = _clock.Elapsed.TotalMilliseconds + 100;
                await Task.WhenAll(_manifest.Sessions.Select((session, index) => AtAsync(due, async () =>
                {
                    switch (_options.Scenario)
                    {
                        case "burst":
                            await MeasureAsync("burst", index, due, async token =>
                            {
                                await SendAsync<ServerTimeSnapshot>(session, HttpMethod.Get, "/api/v1/commands/time", null, token);
                                await ProgressAsync(index, token);
                            }, ct);
                            break;
                        case "final": await FinalAsync(index, due, ct); break;
                        case "download": await MeasureAsync("download", index, due, token => DownloadAsync(session, token), ct); break;
                    }
                }, ct)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { interrupted = true; }
        var summaries = _report.Summarize();
        var passed = !interrupted && summaries.Count > 0 && summaries.Values.All(s => s.Failed == 0 && s.LatencyTargetMet is not false);
        var report = new
        {
            runId = _runId, scenario = _options.Scenario, startedAt = started, finishedAt = DateTimeOffset.UtcNow,
            target = _options.Target.GetLeftPart(UriPartial.Authority), sessions = _manifest.Sessions.Length,
            questionCount = _manifest.QuestionCount, durationSeconds = _options.DurationSeconds,
            requestTimeoutSeconds = _options.TimeoutSeconds, interrupted, localScenarioChecksPassed = passed,
            localScenarioTargetsMet = summaries.Values.Any(s => s.LatencyTargetMet is not null) ? (bool?)passed : null,
            productionCapacityCertified = false, automaticRetryCount = 0,
            loadGenerator = new { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, logicalProcessors = Environment.ProcessorCount,
                processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 },
            operations = summaries,
            nextAnswerVersions = _versions.Select((version, index) => new { sessionIndex = index, answerVersion = version }),
            limitations = new[]
            {
                "Prepared synthetic identities: activation, login, entry and real student GUI not measured.",
                "No dual-host synchronous replication, physical-device or venue-network certification.",
                "Progress verifies persistence response; independent database audit is still required.",
                "Commands measure transport/receipt only, not student business application or START/END timing acceptance.",
                "Download checks byte length and server/version headers, not package decryption, image rendering or instructions completeness.",
                "No automatic retries: normal-load failures remain visible; final replay and receipt lookup are separate checks.",
                "No full-duration/monitoring/export/failover certification unless separately executed and documented."
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_options.OutputDirectory, "summary.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(LoadJson.Options) { WriteIndented = true }));
        Console.WriteLine($"{_options.Scenario}: sessions={_manifest.Sessions.Length}; local checks passed={passed}; production certified=false");
        return passed ? 0 : 1;
    }

    private async Task SteadyAsync(CancellationToken ct)
    {
        var start = _clock.Elapsed.TotalMilliseconds + 100;
        var end = start + _options.DurationSeconds * 1000d;
        await Task.WhenAll(_manifest.Sessions.Select((session, index) => Task.WhenAll(RunSessionAsync(index), RunClockAsync(index))));
        await DelayUntilAsync(end, ct);
        async Task RunClockAsync(int index)
        {
            // First sync is immediate; following periodic syncs are uniformly staggered across 60 seconds.
            await DelayUntilAsync(start, ct);
            await MeasureAsync("clock", index, start, token => SendAsync<ServerTimeSnapshot>(
                _manifest.Sessions[index], HttpMethod.Get, "/api/v1/commands/time", null, token), ct);
            for (var due = start + (index + 1) * 60000d / _manifest.Sessions.Length; due < end; due += 60000)
            {
                await DelayUntilAsync(due, ct);
                await MeasureAsync("clock", index, due, token => SendAsync<ServerTimeSnapshot>(
                    _manifest.Sessions[index], HttpMethod.Get, "/api/v1/commands/time", null, token), ct);
            }
        }
        async Task RunSessionAsync(int index)
        {
            Task? pending = null;
            var due = start + index * 2000d / _manifest.Sessions.Length;
            for (; due < end; due += 2000)
            {
                await DelayUntilAsync(due, ct);
                if (pending is { IsCompleted: false })
                {
                    _report.Add(new("missed-progress-arrival", index, false, _clock.Elapsed.TotalMilliseconds - due, 0,
                        "previous-request-still-running", DateTimeOffset.UtcNow));
                    continue;
                }
                pending = MeasureAsync("progress", index, due, token => ProgressAsync(index, token), ct);
            }
            if (pending is not null) await pending;
        }
        // Clock arrivals use their own fixed cadence, independent of slower answer writes.
    }

    private Task MixedAsync(CancellationToken ct) => MixedScenario.RunAsync(
        (hold, connected, cancellation) => CommandsAsync(cancellation, hold, connected),
        SteadyAsync,
        async cancellation =>
        {
            // Start large transfers after answer/clock arrivals have begun, while hubs remain connected.
            var due = _clock.Elapsed.TotalMilliseconds + 2000;
            await Task.WhenAll(_manifest.Sessions.Select((session, index) => AtAsync(due,
                () => MeasureAsync("download", index, due, token => DownloadAsync(session, token), cancellation), cancellation)));
        },
        async cancellation =>
        {
            var due = _clock.Elapsed.TotalMilliseconds + 100;
            await Task.WhenAll(_manifest.Sessions.Select((_, index) => AtAsync(due,
                () => FinalAsync(index, due, cancellation), cancellation)));
        }, ct);

    private async Task ProgressAsync(int index, CancellationToken ct)
    {
        var s = _manifest.Sessions[index];
        var version = checked(++_versions[index]);
        var answers = Answers(index, version);
        var current = _manifest.QuestionPrefix + (((version - 1) % _manifest.QuestionCount) + 1);
        var request = new SyncProgressRequest($"{_runId}-{index}-{version}", s.ExamId, s.CandidateId, s.ParticipationId,
            s.EntryVersion, s.DeviceId, s.AuthorizationVersion, version, s.PaperVersion, answers, current);
        var result = await SendAsync<PersistedProgress>(s, HttpMethod.Put, "/api/v1/submissions/progress", request, ct);
        if (result.AnswerVersion != version || result.ExamId != s.ExamId || result.CandidateId != s.CandidateId ||
            result.ParticipationId != s.ParticipationId || result.EntryVersion != s.EntryVersion || result.DeviceId != s.DeviceId ||
            result.AuthorizationVersion != s.AuthorizationVersion || result.PaperVersion != s.PaperVersion || result.CurrentQuestionId != current ||
            result.Answers is null || result.Answers.Count != answers.Count || answers.Any(p => !result.Answers.TryGetValue(p.Key, out var actual) || actual is null || actual.OptionIds is null ||
                actual.Kind != p.Value.Kind || !actual.OptionIds.SequenceEqual(p.Value.OptionIds)))
            throw new InvalidDataException("persisted-progress-mismatch");
    }

    public IReadOnlyDictionary<string, AnswerValue> Answers(int index, long version) =>
        Enumerable.Range(1, _manifest.QuestionCount).ToDictionary(q => _manifest.QuestionPrefix + q,
            q => AnswerValue.Selected(_manifest.OptionIds[(int)((index + (long)q + version) % _manifest.OptionIds.Length)]));

    private async Task FinalAsync(int index, double due, CancellationToken ct)
    {
        var s = _manifest.Sessions[index];
        // Stable across process retries; the frozen synthetic payload intentionally does not depend on runId.
        var id = $"load-final-{s.ParticipationId}-{s.EntryVersion}";
        var request = new SubmitFinalAnswersRequest(id, id, s.ExamId, s.CandidateId, s.ParticipationId, s.EntryVersion,
            s.DeviceId, s.AuthorizationVersion, s.PaperVersion, Answers(0, 0));
        SubmissionReceipt? receipt = null;
        await MeasureAsync("final", index, due, async token =>
        {
            receipt = await SendAsync<SubmissionReceipt>(s, HttpMethod.Post, "/api/v1/submissions/final", request, token);
            ValidateReceipt(s, receipt, id);
        }, ct);
        if (receipt is null) return;
        await MeasureAsync("receipt-check", index, _clock.Elapsed.TotalMilliseconds, async token =>
        {
            var found = await SendAsync<SubmissionReceipt>(s, HttpMethod.Get, "/api/v1/submissions/receipts?" + s.Query, null, token);
            if (found != receipt) throw new InvalidDataException("receipt-mismatch");
        }, ct);
        await MeasureAsync("final-replay", index, _clock.Elapsed.TotalMilliseconds, async token =>
        {
            var replay = await SendAsync<SubmissionReceipt>(s, HttpMethod.Post, "/api/v1/submissions/final", request, token);
            if (replay != receipt) throw new InvalidDataException("replay-receipt-mismatch");
        }, ct);
    }

    private static void ValidateReceipt(LoadSession s, SubmissionReceipt receipt, string id)
    {
        if (receipt.SubmissionId != id || receipt.ExamId != s.ExamId || receipt.CandidateId != s.CandidateId ||
            receipt.ParticipationId != s.ParticipationId || receipt.PaperVersion != s.PaperVersion || string.IsNullOrEmpty(receipt.ReceiptId))
            throw new InvalidDataException("invalid-receipt");
    }

    private async Task<T> SendAsync<T>(LoadSession s, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = Request(s, method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: LoadJson.Options);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("http-status", null, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(LoadJson.Options, ct)
            ?? throw new InvalidDataException("empty-envelope");
        if (envelope.Error is not null || envelope.ServerId != _manifest.ServerId || envelope.ServerEpoch != _manifest.ServerEpoch || envelope.Result is null)
            throw new InvalidDataException("invalid-envelope");
        return envelope.Result;
    }

    private HttpRequestMessage Request(LoadSession s, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Exam-Device-Id", s.DeviceId);
        request.Headers.Add("X-Exam-Device-Credential", s.Credential);
        return request;
    }

    private async Task DownloadAsync(LoadSession s, CancellationToken ct)
    {
        using var request = Request(s, HttpMethod.Get, s.ResourcePath!);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("http-status", null, response.StatusCode);
        bool Header(string key, string expected) => response.Headers.TryGetValues(key, out var values) && values.SingleOrDefault() == expected;
        if (!Header("X-Exam-Server-Id", _manifest.ServerId) || !Header("X-Exam-Server-Epoch", _manifest.ServerEpoch.ToString()) ||
            !Header("X-Exam-Paper-Version", s.PaperVersion.ToString())) throw new InvalidDataException("download-identity-mismatch");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            bytes += read;
            if (bytes > s.ExpectedResourceBytes) throw new InvalidDataException("download-length-mismatch");
        }
        if (bytes != s.ExpectedResourceBytes) throw new InvalidDataException("download-length-mismatch");
    }

    private async Task MeasureAsync(string operation, int index, double due, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var started = _clock.Elapsed.TotalMilliseconds;
        string? error = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try { await action(deadline.Token); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or IOException or Microsoft.AspNetCore.SignalR.HubException)
        {
            error = e is HttpRequestException http ? $"HTTP-{(int?)http.StatusCode}" : e.GetType().Name;
        }
        _report.Add(new(operation, index, error is null, Math.Max(0, _clock.Elapsed.TotalMilliseconds - due), Math.Max(0, started - due), error, DateTimeOffset.UtcNow));
    }
    private async Task AtAsync(double due, Func<Task> action, CancellationToken ct) { await DelayUntilAsync(due, ct); await action(); }
    private async Task DelayUntilAsync(double due, CancellationToken ct)
    {
        var delay = due - _clock.Elapsed.TotalMilliseconds;
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _timeProvider, ct);
        ct.ThrowIfCancellationRequested();
    }
    public ValueTask DisposeAsync() { _http.Dispose(); _report.Dispose(); return ValueTask.CompletedTask; }
}
