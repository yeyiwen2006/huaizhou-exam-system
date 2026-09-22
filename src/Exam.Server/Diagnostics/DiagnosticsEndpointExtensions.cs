using System.Diagnostics;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Server.Capacity;
using Exam.Server.Runtime;
using Npgsql;

namespace Exam.Server.Diagnostics;

public sealed record DatabaseActivitySnapshot(bool Available, int? ClientConnections, int? ActiveConnections,
    int? LockWaitingConnections, int? StateNotVisibleConnections, string? Message);
public sealed record RuntimeDiagnosticsSnapshot(DateTimeOffset SampledAt, long WorkingSetBytes, long PeakWorkingSetBytes,
    long GcHeapBytes, int ThreadPoolThreads, long ThreadPoolPendingWorkItems, double ProcessCpuSeconds,
    IReadOnlyList<RequestMetricsSnapshot> Requests, DatabaseCapacityOptions DatabaseBudget,
    RequestCapacityOptions RequestLimits, int MaxConcurrentWrites, DatabaseActivitySnapshot DatabaseActivity);

public static class DiagnosticsEndpointExtensions
{
    public static void MapExamDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/diagnostics/runtime", async (HttpContext context, ExamRuntimeMetrics metrics,
            DatabaseCapacityOptions budget, RequestCapacityOptions limits, NpgsqlDataSource dataSource,
            ServerNodeEnvelope server, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            // The existing middleware treats this non-device route as operator-only. Keep an explicit guard
            // so mounting it in another host without that middleware cannot silently expose diagnostics.
            _ = ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
            context.Response.Headers.CacheControl = "no-store";
            using var process = Process.GetCurrentProcess();
            var snapshot = new RuntimeDiagnosticsSnapshot(DateTimeOffset.UtcNow, process.WorkingSet64,
                process.PeakWorkingSet64, GC.GetGCMemoryInfo().HeapSizeBytes, ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount, process.TotalProcessorTime.TotalSeconds, metrics.Snapshot(), budget,
                limits, configuration.GetValue("Database:MaxConcurrentWrites", 32),
                await ReadDatabaseActivityAsync(dataSource, cancellationToken));
            return Results.Ok(new ApiEnvelope<RuntimeDiagnosticsSnapshot>(server.ServerId, server.NodeId,
                server.ServerEpoch, context.TraceIdentifier, snapshot, null));
        }).WithTags("Operator diagnostics");
    }

    private static async Task<DatabaseActivitySnapshot> ReadDatabaseActivityAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("""
                SELECT count(*)::integer,
                       count(*) FILTER (WHERE state = 'active')::integer,
                       count(*) FILTER (WHERE wait_event_type = 'Lock')::integer,
                       count(*) FILTER (WHERE state IS NULL)::integer
                  FROM pg_stat_activity
                 WHERE datname = current_database() AND backend_type = 'client backend';
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            if (!await reader.ReadAsync(timeout.Token)) return new(false, null, null, null, null, "数据库聚合观测未返回结果。");
            return new(true, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, null, null, null, null, "数据库聚合观测在两秒内未完成，不能把缺失样本视为零等待。"); }
        catch (Exception error) when (error is NpgsqlException or TimeoutException)
        { return new(false, null, null, null, null, "数据库聚合观测不可用，不能把缺失样本视为零连接或零等待。"); }
    }
}
