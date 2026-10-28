using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Verifies the local standby listener without requiring its primary to be online.</summary>
public sealed class StandbyBusinessReadinessProbe(string connectionString, HttpClient http, Uri address,
    string serverId, string nodeId, long epoch, Func<string> peerToken)
{
    public async Task<bool> CheckAsync(ServiceConfiguration expected, CancellationToken token)
    {
        await using var source = ManagementProbeConnection.Create(connectionString, 5);
        await using var role = source.CreateCommand("""
            SELECT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'on'
              AND s.server_epoch = $3 AND s.active_node_id <> $2 AND n.node_role = 'Standby'
            FROM exam.server_state s JOIN exam.server_nodes n ON n.server_id = s.server_id AND n.node_id = $2
            WHERE s.server_id = $1
            """);
        role.Parameters.AddWithValue(serverId);
        role.Parameters.AddWithValue(nodeId);
        role.Parameters.AddWithValue(epoch);
        if (await role.ExecuteScalarAsync(token) is not true) return false;
        // A fresh probe must actually reach the newly started listener. A cached
        // observation from a previous process cannot acknowledge this start.
        var checks = await new RunningBusinessMaterialProbe(http, address, serverId, nodeId,
            () => epoch, peerToken, expectedConfiguration: expected).RunAsync(expected, token);
        return checks.Count > 0 && checks.All(check => check.Passed || !check.BlocksWrites);
    }
}
