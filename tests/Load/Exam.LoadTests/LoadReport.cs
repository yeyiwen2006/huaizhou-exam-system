using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Exam.LoadTests;

public sealed record LoadSample(string Operation, int Session, bool Success, double ElapsedMs,
    double ScheduleDelayMs, string? Error, DateTimeOffset CompletedAt);
public sealed record OperationSummary(int Count, int Failed, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    double MaxScheduleDelayMs, bool? LatencyTargetMet);

public sealed class LoadReport : IDisposable
{
    private readonly object _gate = new();
    private sealed class Bucket
    {
        public List<double> Times { get; } = [];
        public int Failed { get; set; }
        public double MaxScheduleDelay { get; set; }
    }
    private readonly Dictionary<string, Bucket> _samples = new(StringComparer.Ordinal);
    private readonly StreamWriter _writer;
    public LoadReport(string directory)
    {
        Directory.CreateDirectory(directory);
        _writer = new StreamWriter(new FileStream(Path.Combine(directory, "samples.jsonl"), FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false));
    }
    public void Add(LoadSample sample)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(sample.Operation, out var bucket)) _samples.Add(sample.Operation, bucket = new());
            bucket.Times.Add(sample.ElapsedMs);
            if (!sample.Success) bucket.Failed++;
            bucket.MaxScheduleDelay = Math.Max(bucket.MaxScheduleDelay, sample.ScheduleDelayMs);
            _writer.WriteLine(JsonSerializer.Serialize(sample, LoadJson.Options));
        }
    }
    public static double Percentile(IEnumerable<double> values, double quantile)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(quantile * ordered.Length) - 1, 0, ordered.Length - 1)];
    }
    public IReadOnlyDictionary<string, OperationSummary> Summarize()
    {
        lock (_gate)
        {
            _writer.Flush();
            return _samples.ToDictionary(p => p.Key, p =>
            {
                var bucket = p.Value;
                var times = bucket.Times.ToArray();
                var p95 = Percentile(times, .95);
                var p99 = Percentile(times, .99);
                var max = times.Max();
                bool? target = p.Key switch
                {
                    "progress" or "burst" => p95 <= 500 && p99 <= 1000 && (p.Key != "burst" || max <= 2000),
                    "final" => p95 <= 3000 && max <= 10000,
                    "download" => max <= 120000,
                    _ => null
                };
                return new OperationSummary(times.Length, bucket.Failed, Percentile(times, .5), p95, p99,
                    max, bucket.MaxScheduleDelay, target);
            });
        }
    }
    public void Dispose() => _writer.Dispose();
}
