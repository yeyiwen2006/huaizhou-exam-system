using System.Diagnostics.Metrics;
using Exam.Server.Capacity;

namespace Exam.Server.Diagnostics;

/// <summary>Only finite workload/outcome tags. Never include routes, query strings, identity, headers, SQL or payloads.</summary>
public sealed class ExamRuntimeMetrics : IDisposable
{
    public const string MeterName = "Huaizhou.Exam.Server";
    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _queueDuration;
    private readonly UpDownCounter<long> _active;
    private readonly UpDownCounter<long> _queued;
    private readonly Counter<long> _rejected;
    private readonly Bucket[] _buckets = [new(), new(), new(), new()];

    private sealed class Bucket
    {
        public long Active, PeakActive, Queued, Completed, Rejected, DurationMicroseconds, MaximumMicroseconds;
    }

    public ExamRuntimeMetrics()
    {
        _duration = _meter.CreateHistogram<double>("exam.http.duration", "s", "End-to-end server request time, including admission queue.");
        _queueDuration = _meter.CreateHistogram<double>("exam.http.queue.duration", "s");
        _active = _meter.CreateUpDownCounter<long>("exam.http.active", "{request}");
        _queued = _meter.CreateUpDownCounter<long>("exam.http.queued", "{request}");
        _rejected = _meter.CreateCounter<long>("exam.http.rejected", "{request}");
        _meter.CreateObservableGauge("exam.process.working_set", () => Environment.WorkingSet, "By");
        _meter.CreateObservableGauge("exam.process.gc_heap", () => GC.GetGCMemoryInfo().HeapSizeBytes, "By");
        _meter.CreateObservableGauge("exam.process.thread_pool.pending", () => ThreadPool.PendingWorkItemCount, "{work_item}");
    }
    private static KeyValuePair<string, object?> Tag(RequestWorkload workload) => new("workload", workload.ToString().ToLowerInvariant());
    public void Active(RequestWorkload workload, long delta)
    {
        var bucket = _buckets[(int)workload];
        Max(ref bucket.PeakActive, Interlocked.Add(ref bucket.Active, delta));
        _active.Add(delta, Tag(workload));
    }
    public void Queued(RequestWorkload workload, long delta)
    {
        Interlocked.Add(ref _buckets[(int)workload].Queued, delta);
        _queued.Add(delta, Tag(workload));
    }
    public void QueueDuration(RequestWorkload workload, double seconds) => _queueDuration.Record(seconds, Tag(workload));
    public void Rejected(RequestWorkload workload, string reason)
    {
        Interlocked.Increment(ref _buckets[(int)workload].Rejected);
        _rejected.Add(1, Tag(workload), new("reason", reason));
    }
    public void Duration(RequestWorkload workload, double seconds, string outcome)
    {
        var bucket = _buckets[(int)workload];
        var elapsed = (long)(seconds * 1_000_000);
        Interlocked.Increment(ref bucket.Completed);
        Interlocked.Add(ref bucket.DurationMicroseconds, elapsed);
        Max(ref bucket.MaximumMicroseconds, elapsed);
        _duration.Record(seconds, Tag(workload), new("outcome", outcome));
    }
    public IReadOnlyList<RequestMetricsSnapshot> Snapshot() =>
        new[] { RequestWorkload.Critical, RequestWorkload.Api, RequestWorkload.Resource }.Select(workload =>
        {
            var bucket = _buckets[(int)workload];
            return new RequestMetricsSnapshot(workload.ToString().ToLowerInvariant(),
                Interlocked.Read(ref bucket.Active), Interlocked.Read(ref bucket.PeakActive),
                Interlocked.Read(ref bucket.Queued), Interlocked.Read(ref bucket.Completed),
                Interlocked.Read(ref bucket.Rejected), Interlocked.Read(ref bucket.DurationMicroseconds) / 1_000_000.0,
                Interlocked.Read(ref bucket.MaximumMicroseconds) / 1_000_000.0);
        }).ToArray();
    private static void Max(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
    public void Dispose() => _meter.Dispose();
}

public sealed record RequestMetricsSnapshot(string Workload, long Active, long PeakActive, long Queued,
    long Completed, long Rejected, double TotalDurationSeconds, double MaximumDurationSeconds);
