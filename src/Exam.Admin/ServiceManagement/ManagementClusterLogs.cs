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
                            target.NodeId + " · 操作", KindText(entry.Kind), PhaseText(entry.Phase),
                            entry.ErrorCode is not null || entry.Phase == ManagementOperationPhase.Failed ? "未完成" :
                                entry.Phase == ManagementOperationPhase.Completed ? "成功" : entry.Phase == ManagementOperationPhase.Canceled ? "已取消" : "—",
                            entry.RequestId))).ToArray();
            }
            catch { /* Retain this node's prior rows while other nodes continue refreshing. */ }
            try
            {
                var logs = await connection.LogsAsync(50, token);
                if (version != _managementSessionVersion || token.IsCancellationRequested) return;
                if (logs.Envelope is { Error: null, Result: { } entries })
                    _nodeRuntimeLogs[target.Key] = entries.Select(entry => new NodeLog(entry.Timestamp,
                        new(entry.Timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"),
                            target.NodeId + (entry.Source == "business" ? " · 业务服务" : " · 服务器管理"), "运行记录",
                            entry.Level switch { "Warning" => "警告", "Error" => "错误", _ => "严重错误" }, "—", entry.EventId.ToString()))).ToArray();
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

    private void ReleaseTerminalWait(IManagementConnection connection, ManagementOperation operation)
    {
        if (!ReferenceEquals(connection, _connection) || !IsBusy || operation.Phase is not
            (ManagementOperationPhase.Completed or ManagementOperationPhase.Failed or ManagementOperationPhase.Canceled)) return;
        _generation++; _reads?.Cancel(); _reads = null; _readBusy = false; IsBusy = false;
    }
}
