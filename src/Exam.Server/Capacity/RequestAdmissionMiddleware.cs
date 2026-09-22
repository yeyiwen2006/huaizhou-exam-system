using System.Diagnostics;
using System.Threading.RateLimiting;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Server.Diagnostics;

namespace Exam.Server.Capacity;

public sealed class RequestAdmissionControl : IDisposable
{
    private readonly Dictionary<RequestWorkload, ConcurrencyLimiter> _limiters;
    public RequestAdmissionControl(RequestCapacityOptions options)
    {
        ConcurrencyLimiter Create(int permits) => new(new ConcurrencyLimiterOptions
        {
            PermitLimit = permits, QueueLimit = options.QueueLimit, QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        _limiters = new()
        {
            [RequestWorkload.Critical] = Create(options.CriticalPermits),
            [RequestWorkload.Api] = Create(options.ApiPermits),
            [RequestWorkload.Resource] = Create(options.ResourcePermits)
        };
    }
    public ValueTask<RateLimitLease> AcquireAsync(RequestWorkload workload, CancellationToken token) =>
        _limiters[workload].AcquireAsync(1, token);
    public void Dispose() { foreach (var limiter in _limiters.Values) limiter.Dispose(); }
}

public sealed class RequestAdmissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestAdmissionControl admission, RequestCapacityOptions options,
        ExamRuntimeMetrics metrics, ServerNodeEnvelope server)
    {
        var workload = RequestWorkloadClassifier.Classify(context.Request);
        // Upgraded SignalR connections have their own lifetime and bounded registry. Do not hold an API permit for hours.
        if (workload == RequestWorkload.Unrestricted) { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        var outcome = "failure";
        try
        {
            RateLimitLease? lease = null;
            string? rejectionReason = null;
            var queued = Stopwatch.GetTimestamp();
            metrics.Queued(workload, 1);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(options.QueueTimeoutMilliseconds);
                lease = await admission.AcquireAsync(workload, timeout.Token);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                outcome = "cancelled";
            }
            catch (OperationCanceledException)
            {
                rejectionReason = "queue_timeout";
            }
            finally
            {
                metrics.Queued(workload, -1);
                metrics.QueueDuration(workload, Stopwatch.GetElapsedTime(queued).TotalSeconds);
            }
            if (lease is null)
            {
                if (outcome == "cancelled") return;
                outcome = "rejected";
                await RejectAsync(context, metrics, server, workload, rejectionReason!);
                return;
            }
            using (lease)
            {
                if (!lease.IsAcquired)
                {
                    outcome = "rejected";
                    await RejectAsync(context, metrics, server, workload, "queue_full");
                    return;
                }
                metrics.Active(workload, 1);
                try
                {
                    await next(context);
                    outcome = context.RequestAborted.IsCancellationRequested ? "cancelled" :
                        context.Response.StatusCode < 400 ? "success" : "failure";
                }
                finally { metrics.Active(workload, -1); }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { outcome = "cancelled"; }
        finally { metrics.Duration(workload, Stopwatch.GetElapsedTime(started).TotalSeconds, outcome); }
    }

    private static Task RejectAsync(HttpContext context, ExamRuntimeMetrics metrics, ServerNodeEnvelope server,
        RequestWorkload workload, string reason)
    {
        metrics.Rejected(workload, reason);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        return context.Response.WriteAsJsonAsync(new ApiEnvelope<object>(server.ServerId, server.NodeId, server.ServerEpoch,
            context.TraceIdentifier, null, new ApiError(ErrorCodes.ServiceRecovering,
                "服务器当前请求已达处理上限，本次尚未进入业务处理。请保留原请求标识与内容，稍后重试。", Retryable: true)),
            JsonDefaults.Create(), context.RequestAborted);
    }
}
