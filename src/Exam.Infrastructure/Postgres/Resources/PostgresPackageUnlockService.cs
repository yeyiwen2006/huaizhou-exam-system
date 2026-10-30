using System.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Resources;
using Exam.Contracts.Submissions;
using Exam.Infrastructure.Security;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Resources;

public sealed record PackageUnlockIdentity(string ServerId, string NodeId, long ServerEpoch);

public sealed class PackageUnlockException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IPackageKeyUnwrapper
{
    byte[] Unwrap(EncryptedPackageEnvelope envelope);
}

/// <summary>Loads only the current server's private key from its protected certificate source.</summary>
public sealed class CertificateStorePackageKeyUnwrapper(
    string nodeId,
    ServerCertificateSource certificateSource) : IPackageKeyUnwrapper
{
    public CertificateStorePackageKeyUnwrapper(string nodeId, string certificateThumbprint,
        StoreLocation storeLocation = StoreLocation.LocalMachine)
        : this(nodeId, new ServerCertificateSource(certificateThumbprint, StoreLocation: storeLocation))
    {
    }

    public byte[] Unwrap(EncryptedPackageEnvelope envelope)
    {
        using var certificate = certificateSource.Load();
        return PackageEncryptionService.UnwrapDataKey(envelope, nodeId, certificate);
    }
}

/// <summary>
/// Read-only key release is deliberately reauthorized on every retry. A previous successful
/// response is never replayed from an idempotency cache after its device authorization is revoked.
/// Device, exam projection, participation and resource locks are held through key unwrapping.
/// </summary>
public sealed class PostgresPackageUnlockService(
    NpgsqlDataSource dataSource,
    PackageUnlockIdentity identity,
    IPackageKeyUnwrapper keyUnwrapper,
    bool allowUninitializedState = false)
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();

    public async Task<PackageUnlockMaterial> UnlockAsync(
        PackageUnlockRequest request,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.ParticipationId) ||
            string.IsNullOrWhiteSpace(request.DeviceId) ||
            string.IsNullOrWhiteSpace(request.UnlockReference) ||
            request.EntryVersion < 1 || request.AuthorizationVersion < 1 || request.PaperVersion < 1)
        {
            throw Reject(ErrorCodes.InvalidInput, "解锁请求缺少必要的身份或版本信息。");
        }

        if (!string.Equals(request.DeviceId, authenticatedDeviceId, StringComparison.Ordinal))
        {
            throw Reject(ErrorCodes.AuthorizationRevoked, "解锁请求与已认证设备不一致。");
        }

        if (!string.Equals(request.ServerId, identity.ServerId, StringComparison.Ordinal) ||
            request.ServerEpoch != identity.ServerEpoch)
        {
            throw Reject(ErrorCodes.StaleServerEpoch, "服务器身份或服务代次已变化，请重新同步。");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await RequireActiveNodeAsync(connection, transaction, cancellationToken);
        await RequireActiveDeviceAsync(connection, transaction, request.DeviceId, cancellationToken);

        // Read the immutable exam identifier before acquiring the exam lock; publication and
        // start/end use exam-entry -> participation lock order, so key release uses the same order.
        string examId;
        await using (var lookup = new NpgsqlCommand(
                         "SELECT exam_id FROM exam.participations WHERE participation_id = $1;", connection, transaction))
        {
            lookup.Parameters.AddWithValue(request.ParticipationId);
            examId = await lookup.ExecuteScalarAsync(cancellationToken) as string
                ?? throw Reject(ErrorCodes.NotFound, "未找到当前参加考试登记。");
        }

        await RequireCurrentExamAsync(connection, transaction, request, examId, cancellationToken);
        var recovery = await RequireCurrentParticipationAsync(connection, transaction, request, examId, cancellationToken);

        const string prefix = "resource-envelope:";
        if (!request.UnlockReference.StartsWith(prefix, StringComparison.Ordinal) ||
            request.UnlockReference.Length == prefix.Length)
        {
            throw Reject(ErrorCodes.KeyNotReleased, "解锁引用无效。");
        }

        var resourceId = request.UnlockReference[prefix.Length..];
        EncryptedPackageEnvelope envelope;
        await using (var resource = new NpgsqlCommand("""
                         SELECT encryption_envelope::text
                           FROM exam.versioned_resources
                          WHERE resource_id = $1 AND exam_id = $2
                            AND resource_kind = 'paper-package' AND resource_version = $3
                            FOR SHARE;
                         """, connection, transaction))
        {
            resource.Parameters.AddWithValue(resourceId);
            resource.Parameters.AddWithValue(examId);
            resource.Parameters.AddWithValue(request.PaperVersion);
            var json = await resource.ExecuteScalarAsync(cancellationToken) as string
                ?? throw Reject(ErrorCodes.KeyNotReleased, "当前题包的解锁材料尚未就绪。");
            envelope = JsonSerializer.Deserialize<EncryptedPackageEnvelope>(json, JsonOptions)
                ?? throw Reject(ErrorCodes.ServiceRecovering, "题包解锁材料不可用。");
        }

        if (!string.Equals(envelope.PackageId, resourceId, StringComparison.Ordinal) ||
            envelope.PaperVersion != request.PaperVersion ||
            envelope.ChunkSize is < 4096 or > 4 * 1024 * 1024 ||
            Convert.FromBase64String(envelope.NoncePrefix).Length != 8)
        {
            throw Reject(ErrorCodes.ServiceRecovering, "题包与持久化解锁材料不一致。");
        }

        var dataKey = keyUnwrapper.Unwrap(envelope);
        try
        {
            if (dataKey.Length != 32)
            {
                throw new CryptographicException("题包数据密钥长度无效。");
            }

            // Lock acquisition and certificate access may cross the deadline. Transaction time
            // is fixed at BEGIN, so the last authorization check must use the database wall clock.
            await RequireBeforeDeadlineAsync(connection, transaction, examId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PackageUnlockMaterial(
                envelope.PackageId, envelope.PaperVersion, envelope.ChunkSize, envelope.NoncePrefix, dataKey)
                { AnswerRecovery = recovery };
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }
    }

    private async Task RequireActiveNodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var writable = new NpgsqlCommand(
                         "SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'off';",
                         connection, transaction))
        {
            if (await writable.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw Reject(ErrorCodes.NotActiveServer, "当前节点不能发放题包解锁材料。");
            }
        }

        await using var command = new NpgsqlCommand("""
            SELECT active_node_id, server_epoch, protection_mode
              FROM exam.server_state WHERE server_id = $1 FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue(identity.ServerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            if (allowUninitializedState) return;
            throw Reject(ErrorCodes.NotActiveServer, "当前节点不是已就绪的权威活动节点。");
        }
        if (!string.Equals(reader.GetString(0), identity.NodeId, StringComparison.Ordinal) ||
            reader.GetString(2) == "Recovering")
        {
            throw Reject(ErrorCodes.NotActiveServer, "当前节点不是已就绪的权威活动节点。");
        }

        if (reader.GetInt64(1) != identity.ServerEpoch)
        {
            throw Reject(ErrorCodes.StaleServerEpoch, "当前进程服务代次已失效。");
        }
    }

    private static async Task RequireActiveDeviceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT activation_status FROM exam.activation_devices WHERE device_id = $1 FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue(deviceId);
        if (await command.ExecuteScalarAsync(cancellationToken) as string != "active")
        {
            throw Reject(ErrorCodes.NotActivated, "设备尚未激活或激活已撤销。");
        }
    }

    private static async Task RequireCurrentExamAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PackageUnlockRequest request,
        string examId,
        CancellationToken cancellationToken)
    {
        await using (var entry = new NpgsqlCommand("""
                         SELECT lifecycle, package_ready, instructions_ready, paper_version, unlock_reference,
                                clock_timestamp() < scheduled_end
                           FROM exam.exam_entry_access WHERE exam_id = $1 FOR SHARE;
                         """, connection, transaction))
        {
            entry.Parameters.AddWithValue(examId);
            await using var reader = await entry.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "running" ||
                !reader.GetBoolean(1) || !reader.GetBoolean(2) || !reader.GetBoolean(5))
            {
                throw Reject(ErrorCodes.KeyNotReleased, "考试尚未开始、已经结束或资源未就绪。");
            }

            if (reader.GetInt64(3) != request.PaperVersion || reader.IsDBNull(4) ||
                !string.Equals(reader.GetString(4), request.UnlockReference, StringComparison.Ordinal))
            {
                throw Reject(ErrorCodes.VersionConflict, "解锁引用或试卷版本已变化，请重新同步。");
            }
        }

        await using var version = new NpgsqlCommand("""
            SELECT lifecycle = 'Running' AND actual_started_at IS NOT NULL AND paper_version = $2
              FROM exam.version_state WHERE exam_id = $1;
            """, connection, transaction);
        version.Parameters.AddWithValue(examId);
        version.Parameters.AddWithValue(request.PaperVersion);
        if (await version.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw Reject(ErrorCodes.KeyNotReleased, "尚未确认当前试卷版本已经开考。");
        }
    }

    private static async Task RequireBeforeDeadlineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string examId,
        CancellationToken cancellationToken)
    {
        // RequireCurrentExamAsync already holds this row FOR SHARE until commit. Re-read after
        // every potentially blocking operation so an earlier successful check cannot release a
        // new key for an exam whose scheduled end has since passed.
        await using var deadline = new NpgsqlCommand("""
            SELECT clock_timestamp() < scheduled_end
              FROM exam.exam_entry_access WHERE exam_id = $1;
            """, connection, transaction);
        deadline.Parameters.AddWithValue(examId);
        if (await deadline.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw Reject(ErrorCodes.KeyNotReleased, "考试已到截止时间，不能重新解锁试题。");
        }
    }

    private static async Task<AuthorizedAnswerRecovery> RequireCurrentParticipationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PackageUnlockRequest request,
        string examId,
        CancellationToken cancellationToken)
    {
        // 等待并发裁决完成后再读取答案和裁决历史，避免把新授权与等待前的旧快照拼在一起。
        // 此锁也与进度写入互斥；复用现有解锁请求，不增加客户端网络往返。
        await using (var participationLock = new NpgsqlCommand(
            "SELECT participation_id FROM exam.participations WHERE participation_id = $1 FOR SHARE;", connection, transaction))
        {
            participationLock.Parameters.AddWithValue(request.ParticipationId);
            await participationLock.ExecuteScalarAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            SELECT p.entry_version, p.authorized_device_id, p.authorization_version, p.business_state,
                   p.candidate_id, progress.answer_version, progress.paper_version, progress.device_id,
                   progress.authorization_version, progress.answers::text, progress.current_question_id,
                   progress.last_synced_at,
                   COALESCE((SELECT MAX(a.authorization_version)
                               FROM exam.device_decision_audits a
                              WHERE a.participation_id = p.participation_id
                                AND a.selected_device_id <> p.authorized_device_id), 0) + 1
              FROM exam.participations p
              LEFT JOIN exam.participation_progress progress
                ON progress.participation_id = p.participation_id AND progress.entry_version = p.entry_version
             WHERE p.participation_id = $1;
            """, connection, transaction);
        command.Parameters.AddWithValue(request.ParticipationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(0) != request.EntryVersion ||
            !string.Equals(reader.GetString(1), request.DeviceId, StringComparison.Ordinal) ||
            reader.GetInt64(2) != request.AuthorizationVersion)
        {
            throw Reject(ErrorCodes.AuthorizationRevoked, "参加考试登记或设备授权已变化。");
        }

        if (reader.GetString(3) != "answering")
        {
            throw Reject(ErrorCodes.KeyNotReleased, "当前参加考试状态不能解锁试题。");
        }

        var candidateId = reader.GetString(4);
        PersistedProgress? progress = reader.IsDBNull(5) ? null : new(
            examId, candidateId, request.ParticipationId, request.EntryVersion, reader.GetString(7),
            reader.GetInt64(8), reader.GetInt64(5), reader.GetInt64(6),
            JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(reader.GetString(9), JsonOptions)
                ?? throw Reject(ErrorCodes.ServiceRecovering, "服务器已保存答案无法读取。"),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11));
        return new(examId, candidateId, request.ParticipationId, request.EntryVersion,
            request.DeviceId, request.AuthorizationVersion, progress)
            { RetainLocalAnswersFromAuthorizationVersion = reader.GetInt64(12) };
    }

    private static PackageUnlockException Reject(string code, string message) => new(code, message);
}
