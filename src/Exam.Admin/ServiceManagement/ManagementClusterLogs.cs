using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    private sealed record NodeLog(DateTimeOffset Timestamp, ManagementLogRow Row);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NodeLog[]> _nodeAuditLogs = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NodeLog[]> _nodeRuntimeLogs = new(StringComparer.Ordinal);

    private async Task RefreshClusterLogsAsync(CancellationToken token)
    {
        var version = _managementSessionVersion;
        var connections = ConfiguredManagementTargets.Select(target => NodeSession(target).Connection)
            .OfType<IManagementConnection>().DistinctBy(connection => connection.Target.Key).ToArray();
        await Task.WhenAll(connections.Select(async connection =>
        {
            var target = connection.Target;
            try
            {
                var audit = await connection.AuditAsync(50, token);
                if (version != _managementSessionVersion || token.IsCancellationRequested) return;
                if (audit.Envelope is { Error: null, Result: { } entries })
                    _nodeAuditLogs[target.Key] = entries.Select(entry => new NodeLog(entry.AcceptedAt,
                        new(entry.AcceptedAt.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"),
                            "操作", KindText(entry.Kind), PhaseText(entry.Phase),
                            entry.ErrorCode is not null || entry.Phase == ManagementOperationPhase.Failed ? "未完成" :
                                entry.Phase == ManagementOperationPhase.Completed ? "成功" : entry.Phase == ManagementOperationPhase.Canceled ? "已取消" : "—",
                            entry.RequestId, target.NodeId))).ToArray();
            }
            catch { /* Retain this node's prior rows while other nodes continue refreshing. */ }
            try
            {
                var logs = await connection.LogsAsync(50, token);
                if (version != _managementSessionVersion || token.IsCancellationRequested) return;
                if (logs.Envelope is { Error: null, Result: { } entries })
                    _nodeRuntimeLogs[target.Key] = entries.Where(entry => entry.Source != "management" ||
                        entry.Category != "Exam.Management.Operation.Failed").Select(entry => new NodeLog(entry.Timestamp,
                        new(entry.Timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"),
                            entry.Source == "business" ? "业务服务" : "服务器管理", RuntimeEventText(entry),
                            RuntimeEventLevel(entry), "—", entry.EventId.ToString(), target.NodeId))).ToArray();
            }
            catch { }
        }));
        if (version != _managementSessionVersion || token.IsCancellationRequested) return;
        var keys = ConfiguredManagementTargets.Select(target => target.Key).ToHashSet(StringComparer.Ordinal);
        var rows = _nodeAuditLogs.Concat(_nodeRuntimeLogs).Where(pair => keys.Contains(pair.Key))
            .SelectMany(pair => pair.Value).OrderByDescending(entry => entry.Timestamp).Take(100).ToArray();
        Logs.Clear();
        foreach (var entry in rows) Logs.Add(entry.Row);
    }

    private static string RuntimeEventText(ProtectedRuntimeLogEntry entry) => entry.Category switch
    {
        "Exam.Management.Rebuild.PgHbaDenied" => "重建连接被数据库访问规则拒绝",
        var value when value.StartsWith("Exam.Management.Database.", StringComparison.Ordinal) =>
            (value.Contains(".Peer.", StringComparison.Ordinal) ? "对端数据库" : "本节点数据库") +
            (value.EndsWith("AuthenticationFailed", StringComparison.Ordinal) ? "认证失败" :
                value.EndsWith("Timeout", StringComparison.Ordinal) ? "状态查询超时" : "连接暂不可用"),
        "Exam.Server.Runtime.GradingWorker" => "计分任务失败，等待重试",
        "Exam.Server.Runtime.RecalculationWorker" => "成绩重算失败，等待重试",
        "Exam.Server.Runtime.CollectionCompletionWorker" => "收卷检查失败，等待重试",
        "Exam.Server.Runtime.ApiExceptionMiddleware" => "业务请求处理失败",
        "Exam.Server.Runtime.PostgresScheduleWorker" => "考试计划处理异常",
        "Exam.Server.Runtime.RecalculationProcessor" => "成绩重算异常，等待重试",
        "Exam.Server.Runtime.GradingProcessor" => "计分处理警告",
        "Exam.Server.Runtime.GradingRecalculationTrigger" => "成绩重算调度异常，等待后台重试",
        _ => entry.Source == "business" ? "业务服务运行事件" : "服务器管理运行事件"
    };

    private static string RuntimeEventLevel(ProtectedRuntimeLogEntry entry) =>
        entry.Source == "management" && entry.Category.StartsWith("Exam.Management.Database.", StringComparison.Ordinal) &&
        !entry.Category.EndsWith("AuthenticationFailed", StringComparison.Ordinal) ? "警告" :
        entry.Level switch { "Warning" => "警告", "Error" => "错误", _ => "严重错误" };

    private void ReleaseTerminalWait(IManagementConnection connection, ManagementOperation operation)
    {
        if (!ReferenceEquals(connection, _connection) || !IsBusy || operation.Phase is not
            (ManagementOperationPhase.Completed or ManagementOperationPhase.Failed or ManagementOperationPhase.Canceled)) return;
        _generation++; _reads?.Cancel(); _reads = null; _readBusy = false; IsBusy = false;
    }
}
