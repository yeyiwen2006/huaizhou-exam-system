using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.HighAvailability;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.HighAvailability;

public interface IDurableConfirmationPeer
{
    Task<DurableConfirmation> ConfirmAsync(DurableConfirmation record, CancellationToken token);
    Task<ConfirmationHead?> ReadAsync(CancellationToken token);
    Task SynchronizeResourcesAsync(string directory, CancellationToken token) => Task.CompletedTask;
    Task SynchronizeResourcesAsync(string directory, string verifiedDigest, CancellationToken token) =>
        SynchronizeResourcesAsync(directory, token);

}

public sealed partial class HttpConfirmationPeer(HttpClient http, Uri endpoint, string serverId, string standbyNodeId,
    Func<string?> credential) : IDurableConfirmationPeer
{
    private Uri Address()
    {
        if (endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || endpoint.AbsolutePath != "/")
            throw new InvalidOperationException("备节点确认必须使用固定 HTTPS 根地址。");
        return new(endpoint, "replication/v1/confirmation");
    }
    private async Task<T?> SendAsync<T>(HttpMethod method, DurableConfirmation? record, CancellationToken token)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Bound the entire exchange,
        // including a stalled body, so one peer cannot indefinitely hold the confirmation gate.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var maximum = TimeSpan.FromSeconds(30);
        deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan || http.Timeout > maximum ? maximum : http.Timeout);
        token = deadline.Token;
        using var request = new HttpRequestMessage(method, Address());
        var secret = credential();
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32) throw new IOException("备节点确认凭据未配置。");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("X-Exam-Server-Id", serverId); request.Headers.Add("X-Exam-Node-Id", standbyNodeId);
        if (record is not null) request.Content = JsonContent.Create(record, options: JsonDefaults.Create());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 16384)
            throw new IOException("备节点确认不可用。");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096]; int count;
        while ((count = await body.ReadAsync(bytes, token)) > 0)
        { if (buffer.Length + count > 16384) throw new IOException("备节点确认响应超限。"); buffer.Write(bytes, 0, count); }
        return System.Text.Json.JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonDefaults.Create());
    }
    public async Task<DurableConfirmation> ConfirmAsync(DurableConfirmation record, CancellationToken token)
    {
        _latestRequest = record;
        return await SendAsync<DurableConfirmation>(HttpMethod.Post, record, token) ?? throw new IOException("备节点未返回确认。");
    }
    public Task<ConfirmationHead?> ReadAsync(CancellationToken token) => SendAsync<ConfirmationHead>(HttpMethod.Get, null, token);
}

/// <summary>Flushes earlier local commits and records the actual protection level.
/// Peer failure leaves a durable local confirmation; local failures never produce success.</summary>
public sealed class PostgresBusinessConfirmation(NpgsqlDataSource source, FileConfirmationStore store,
    string serverId, string primaryNodeId, string standbyNodeId, long epoch, string resourcesDirectory,
    IDurableConfirmationPeer? peer, string? timelineConnection = null, string? postgresBinDirectory = null) : IBusinessConfirmation, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ResourceDirectoryDigestCache _resources = new(resourcesDirectory);
    private long _nextPeerAttempt;
    public async Task MaintainReplicationAsync(CancellationToken token)
    {
        // Reuse the same content-and-change-verified inventory as business confirmation.
        // An unchanged maintenance tick must not hash and flush every resource again.
        if (peer is not null) await peer.SynchronizeResourcesAsync(resourcesDirectory, _resources.Read(), token);
        await ConfirmAsync(token);
    }
    public async Task ConfirmAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            using var locked = store.Lock();
            var previous = store.Read();
            var resources = _resources.Read();
            await using var connection = await source.OpenConnectionAsync(token);
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                // This fence must flush locally even if the configured synchronous standby is absent.
                // A healthy peer adds a second durable copy. Its absence cannot block local success.
                await using var durability = new NpgsqlCommand("SET LOCAL synchronous_commit=local", connection, transaction);
                await durability.ExecuteNonQueryAsync(token);
                await using var fence = new NpgsqlCommand("""
                    UPDATE exam.server_state SET updated_at=updated_at
                    WHERE server_id=$1 AND active_node_id=$2 AND server_epoch=$3
                      AND protection_mode IN ('SynchronousDualNode','EmergencySingleNode','Recovering')
                      AND current_setting('fsync')='on' AND current_setting('full_page_writes')='on';
                    """, connection, transaction);
                fence.Parameters.AddWithValue(serverId); fence.Parameters.AddWithValue(primaryNodeId); fence.Parameters.AddWithValue(epoch);
                if (await fence.ExecuteNonQueryAsync(token) != 1) throw new IOException("确认时权威活动节点不匹配。");
                await transaction.CommitAsync(token);
            }
            await using var command = new NpgsqlCommand("""
                SELECT s.protection_mode,s.state_version,c.system_identifier::text,p.timeline_id,
                       pg_current_wal_flush_lsn()::text,pg_is_in_recovery(),current_database()
                FROM exam.server_state s CROSS JOIN pg_control_system() c CROSS JOIN pg_control_checkpoint() p
                WHERE s.server_id=$1 AND s.active_node_id=$2 AND s.server_epoch=$3
                """, connection);
            command.Parameters.AddWithValue(serverId); command.Parameters.AddWithValue(primaryNodeId); command.Parameters.AddWithValue(epoch);
            DurableConfirmation record;
            await using (var row = await command.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token) || row.GetBoolean(5)) throw new IOException("确认源不是活动主库。");
                record = new(Guid.NewGuid().ToString("N"), serverId, primaryNodeId, standbyNodeId, epoch,
                    row.GetInt64(1), ProtectionMode.EmergencySingleNode, row.GetString(2), row.GetString(6), row.GetInt32(3), row.GetString(4), resources);
            }
            var continuation = previous is not null && previous.Record.Timeline != record.Timeline
                ? await VerifiedTimelineContinuation.ReadAsync(source, previous.Record, record.Timeline, timelineConnection, postgresBinDirectory, token) : null;
            await store.SaveAsync(new(record, false), token, continuation);
            if (peer is not null && Environment.TickCount64 >= Interlocked.Read(ref _nextPeerAttempt))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
                var dual = record with { Mode = ProtectionMode.SynchronousDualNode };
                try
                {
                    if (await peer.ConfirmAsync(dual, deadline.Token) != dual)
                        throw new IOException("备节点确认边界与原请求不同。");
                    record = dual;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Only peer work is recoverable here. Local I/O, ownership, WAL and resource failures
                    // remain outside this catch and still refuse success.
                    Interlocked.Exchange(ref _nextPeerAttempt, Environment.TickCount64 + 3000);
                }
            }
            token.ThrowIfCancellationRequested();
            // A local-only head differs from the peer's older head and cannot claim dual protection.
            if (_resources.Read() != resources)
                throw new IOException("确认期间资源发生变化。");
            await store.SaveAsync(new(record, true), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new DomainRuleException(ErrorCodes.ServiceRecovering, "持久确认尚未完成，保留原请求重试。", retryable: true); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _resources.Dispose();
}

/// <summary>Runs on the standby's business listener but never writes its PostgreSQL database.</summary>
public sealed class PostgresStandbyConfirmation(NpgsqlDataSource source, FileConfirmationStore store,
    string serverId, string standbyNodeId, string primaryNodeId, long epoch, string resourcesDirectory,
    IPackageKeyUnwrapper unwrapper, string? timelineConnection = null, string? postgresBinDirectory = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ResourceDirectoryDigestCache _resources = new(resourcesDirectory);
    private readonly PostgresRecoveryReadinessProbe _readiness = new(source, unwrapper);
    private bool _recoveryVerified;
    public async Task<DurableConfirmation> ConfirmAsync(DurableConfirmation record, CancellationToken token)
    {
        FileConfirmationStore.Validate(record);
        if (record.ServerId != serverId || record.PrimaryNodeId != primaryNodeId || record.StandbyNodeId != standbyNodeId ||
            record.Epoch != epoch || record.Mode != ProtectionMode.SynchronousDualNode)
            throw new IOException("确认的服务器、节点、代次或保护模式不同。");
        await _gate.WaitAsync(token);
        try
        {
            using var locked = store.Lock();
            var previous = store.Read();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try { await VerifyPositionAsync(source, record, standbyNodeId, token); break; }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(50, token); }
            }
            if (_resources.Read() != record.ResourcesDigest)
                throw new IOException("备节点资源尚未持久追平。");
            if (!_recoveryVerified)
            {
                var config = new ServiceConfiguration(serverId, standbyNodeId, "127.0.0.1", 1, resourcesDirectory, true, 1);
                var checks = await _readiness.RunAsync(config, token);
                if (checks.Count == 0 || checks.Any(c => c.BlocksWrites && !c.Passed))
                    throw new IOException("备节点资源、解锁或持久数据核对失败。");
                _recoveryVerified = true;
            }
            else if (!await _readiness.VerifyConfirmationResourcesAsync(token))
                throw new IOException("备节点新增或变更的试题资源核对失败。");
            await VerifyPositionAsync(source, record, standbyNodeId, token);
            if (_resources.Read() != record.ResourcesDigest)
                throw new IOException("备节点资源在确认期间改变。");
            var continuation = previous is not null && previous.Record.Timeline != record.Timeline
                ? await VerifiedTimelineContinuation.ReadAsync(source, previous.Record, record.Timeline, timelineConnection, postgresBinDirectory, token) : null;
            await store.SaveAsync(new(record, true), token, continuation);
            return record;
        }
        finally { _gate.Release(); }
    }

    public ConfirmationHead? Read() => store.Read();
    public string ReadResourcesDigest() => _resources.Read();

    public void Dispose() => _resources.Dispose();

    public static async Task VerifyPositionAsync(NpgsqlDataSource source, DurableConfirmation record,
        string standbyNodeId, CancellationToken token, string? timelineConnection = null, string? postgresBinDirectory = null)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT pg_is_in_recovery(),pg_is_wal_replay_paused(),pg_last_wal_replay_lsn()::text,
                   c.system_identifier::text,p.timeline_id,s.active_node_id,s.server_epoch,s.protection_mode,s.state_version,
                   n.node_role,current_database(),pg_last_wal_receive_lsn()::text
            FROM exam.server_state s CROSS JOIN pg_control_system() c CROSS JOIN pg_control_checkpoint() p
              JOIN exam.server_nodes n ON n.server_id=s.server_id AND n.node_id=$2
            WHERE s.server_id=$1
            """, connection);
        command.Parameters.AddWithValue(record.ServerId); command.Parameters.AddWithValue(standbyNodeId);
        await using var row = await command.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token) || !row.GetBoolean(0) || row.GetBoolean(1) || row.IsDBNull(2) ||
            FileConfirmationStore.Lsn(row.GetString(2)) < FileConfirmationStore.Lsn(record.FlushLsn) ||
            row.GetString(3) != record.SystemIdentifier ||
            row.GetString(5) != record.PrimaryNodeId || row.GetInt64(6) != record.Epoch ||
            row.GetString(7) is not ("SynchronousDualNode" or "EmergencySingleNode" or "Recovering") || row.GetInt64(8) < record.StateVersion || row.GetString(9) != "Standby" ||
            row.GetString(10) != record.DatabaseName || row.IsDBNull(11) ||
            FileConfirmationStore.Lsn(row.GetString(11)) < FileConfirmationStore.Lsn(record.FlushLsn))
            throw new IOException("实际备库未覆盖确认边界或身份不匹配。");
        var timeline = row.GetInt32(4);
        await row.DisposeAsync();
        if (timeline != record.Timeline)
            _ = await VerifiedTimelineContinuation.ReadAsync(source, record, timeline, timelineConnection, postgresBinDirectory, token);
    }
}
