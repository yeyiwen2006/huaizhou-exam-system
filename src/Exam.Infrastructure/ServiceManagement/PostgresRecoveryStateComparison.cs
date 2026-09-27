using System.Data;
using System.Security.Cryptography;
using System.Text;
using Exam.Contracts.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Read-only proof for a stopped business service. An unavailable original is not evidence of completeness.</summary>
public sealed class PostgresRecoveryStateComparison(string originalConnection, string recoveryConnection, long epoch)
{
    public async Task<StartupCheckResult> RunAsync(ServiceConfiguration configuration, CancellationToken token)
    {
        try
        {
            await using var original = ManagementProbeConnection.Create(originalConnection, 30);
            await using var recovery = ManagementProbeConnection.Create(recoveryConnection, 30);
            var before = await SnapshotAsync(original, configuration, token);
            var after = await SnapshotAsync(recovery, configuration, token);
            var equal = CryptographicOperations.FixedTimeEquals(before, after);
            return new("recovery-confirmed-state", equal, equal
                ? "停止业务后的原库与恢复库业务状态一致；保留既有单机应急授权，未提升节点或改变代次。"
                : "原库与恢复库业务状态不同，不能证明已确认数据完整，禁止开放业务。", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new("recovery-confirmed-state", false,
                "无法只读核实原库及恢复库的完整业务状态或既有单机应急授权；禁止开放业务。", true);
        }
    }

    private async Task<byte[]> SnapshotAsync(NpgsqlDataSource source, ServiceConfiguration configuration, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL TIME ZONE 'UTC'", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token);
        await using (var authority = new NpgsqlCommand("SELECT active_node_id, server_epoch, protection_mode FROM exam.server_state WHERE server_id=@server", connection, transaction))
        {
            authority.Parameters.AddWithValue("server", configuration.ServerId);
            await using var row = await authority.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetString(0) != configuration.NodeId || row.GetInt64(1) != epoch ||
                row.GetString(2) != "EmergencySingleNode" || await row.ReadAsync(token))
                throw new InvalidDataException("恢复不能自行更改原主备授权。");
        }
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);
        }
        var tables = new List<string>();
        await using (var inventory = new NpgsqlCommand("""
            SELECT tablename FROM pg_tables WHERE schemaname='exam'
                AND tablename NOT IN ('backup_records','restore_records') ORDER BY tablename COLLATE "C"
            """, connection, transaction))
        await using (var rows = await inventory.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token)) tables.Add(rows.GetString(0));
        if (tables.Count is < 1 or > 512) throw new InvalidDataException("业务表清单无效。");
        foreach (var table in tables)
        {
            Append(table);
            await using (var schema = new NpgsqlCommand("""
                SELECT column_name || ':' || udt_schema || ':' || udt_name || ':' || is_nullable || ':' || coalesce(column_default,'')
                FROM information_schema.columns WHERE table_schema='exam' AND table_name=@table ORDER BY ordinal_position
                """, connection, transaction))
            {
                schema.Parameters.AddWithValue("table", table);
                await using var rows = await schema.ExecuteReaderAsync(token);
                while (await rows.ReadAsync(token)) Append(rows.GetString(0));
            }
            var quoted = '"' + table.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            await using var data = new NpgsqlCommand($"SELECT sha256(convert_to(to_jsonb(t)::text,'UTF8')) AS digest FROM exam.{quoted} t ORDER BY digest", connection, transaction);
            await using var reader = await data.ExecuteReaderAsync(token);
            long count = 0;
            while (await reader.ReadAsync(token)) { digest.AppendData(reader.GetFieldValue<byte[]>(0)); count++; }
            Append(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // Sequences are outside MVCC; the caller keeps both business hosts stopped throughout this check.
        var sequenceNames = new List<string>();
        await using (var sequences = new NpgsqlCommand("""
            SELECT sequencename, start_value::text, increment_by::text, min_value::text, max_value::text, cycle::text, cache_size::text
            FROM pg_sequences WHERE schemaname='exam' ORDER BY sequencename COLLATE "C"
            """, connection, transaction))
        await using (var rows = await sequences.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
            {
                sequenceNames.Add(rows.GetString(0));
                for (var i = 0; i < 7; i++) Append(rows.GetString(i));
            }
        if (sequenceNames.Count > 512) throw new InvalidDataException("业务序列清单无效。");
        foreach (var name in sequenceNames)
        {
            var quoted = '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            await using var sequence = new NpgsqlCommand($"SELECT last_value::text, is_called::text FROM exam.{quoted}", connection, transaction);
            await using var row = await sequence.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token)) throw new InvalidDataException("业务序列不可核实。");
            Append(name); Append(row.GetString(0)); Append(row.GetString(1));
        }
        await transaction.CommitAsync(token);
        return digest.GetHashAndReset();
    }
}
