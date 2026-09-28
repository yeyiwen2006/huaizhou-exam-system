using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.LoadTests.Tests;

public sealed partial class LoadRunnerTests
{
    [Theory]
    [InlineData(500, 0)]
    [InlineData(501, 1)]
    [InlineData(2001, 1)]
    public async Task BurstKeepsOriginalLatencyThresholdsWithDeterministicTime(int elapsedMs, int expectedExit)
    {
        var options = Options();
        var time = new SyntheticTimeProvider();
        try
        {
            using var handler = new Handler(async request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/time"))
                    return Envelope(new ServerTimeSnapshot(time.GetUtcNow(), new("server", "node", 1)));
                var body = (await request.Content!.ReadFromJsonAsync<SyncProgressRequest>(LoadJson.Options))!;
                time.Advance(TimeSpan.FromMilliseconds(elapsedMs));
                return Envelope(new PersistedProgress(body.ExamId, body.CandidateId, body.ParticipationId,
                    body.EntryVersion, body.DeviceId, body.AuthorizationVersion, body.AnswerVersion,
                    body.PaperVersion, body.Answers, body.CurrentQuestionId, time.GetUtcNow()));
            });
            await using (var runner = new LoadRunner(options, Manifest(), handler, time))
                Assert.Equal(expectedExit, await runner.RunAsync(default));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.OutputDirectory, "summary.json")));
            var burst = report.RootElement.GetProperty("operations").GetProperty("burst");
            Assert.Equal(elapsedMs, burst.GetProperty("p95Ms").GetDouble());
            Assert.Equal(expectedExit == 0, burst.GetProperty("latencyTargetMet").GetBoolean());
            Assert.False(report.RootElement.GetProperty("productionCapacityCertified").GetBoolean());
        }
        finally { if (Directory.Exists(options.OutputDirectory)) Directory.Delete(options.OutputDirectory, true); }
    }

    // 仅供单次合成 HTTP 契约/报告测试；正式 CLI 默认使用真实单调时钟。
    private sealed class SyntheticTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("This test clock supports one-shot waits only.");
            if (dueTime == Timeout.InfiniteTimeSpan) return new Timer(callback, state, dueTime, period);
            Advance(dueTime);
            return new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
