using System.Collections.ObjectModel;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Resources;

public sealed record VersionedEncryptedResource(
    string ResourceId,
    string ExamId,
    string ResourceKind,
    long Version,
    byte[] EncryptedContent,
    EncryptedPackageEnvelope Envelope,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed class PostgresVersionedResourceStore(
    NpgsqlDataSource dataSource,
    ManagedNodeEncryptionPolicy? policy = null)
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();

    public async Task SaveAsync(
        VersionedEncryptedResource resource,
        CancellationToken cancellationToken = default)
    {
        Validate(resource, policy ?? ManagedNodeEncryptionPolicy.None);
        const string sql = """
            INSERT INTO exam.versioned_resources
                (resource_id, exam_id, resource_kind, resource_version, encrypted_content,
                 encryption_envelope, created_at, created_by)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8);
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(resource.ResourceId);
        command.Parameters.AddWithValue(resource.ExamId);
        command.Parameters.AddWithValue(resource.ResourceKind);
        command.Parameters.AddWithValue(resource.Version);
        command.Parameters.AddWithValue(resource.EncryptedContent);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(resource.Envelope, JsonOptions));
        command.Parameters.AddWithValue(resource.CreatedAt);
        command.Parameters.AddWithValue(resource.CreatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VersionedEncryptedResource?> FindAsync(
        string examId,
        string resourceKind,
        long version,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT resource_id, exam_id, resource_kind, resource_version, encrypted_content,
                   encryption_envelope::text, created_at, created_by
            FROM exam.versioned_resources
            WHERE exam_id = $1 AND resource_kind = $2 AND resource_version = $3;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(examId);
        command.Parameters.AddWithValue(resourceKind);
        command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResource(reader) : null;
    }

    public async Task<VersionedEncryptedResource?> FindByIdAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT resource_id, exam_id, resource_kind, resource_version, encrypted_content,
                   encryption_envelope::text, created_at, created_by
              FROM exam.versioned_resources
             WHERE resource_id = $1;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(resourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResource(reader) : null;
    }

    public async Task<VersionedEncryptedResource?> FindForPreparationAsync(
        string resourceId,
        string preparationId,
        string sessionId,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
    {
        const string sql = PreparedEntryAccessSql.CurrentPreparationsCte + """
            SELECT resource.resource_id, resource.exam_id, resource.resource_kind,
                   resource.resource_version, resource.encrypted_content,
                   resource.encryption_envelope::text, resource.created_at, resource.created_by
              FROM exam.versioned_resources AS resource
              JOIN authorized_preparations AS preparation
                ON preparation.preparation_id = $2
             WHERE resource.resource_id = $1
               AND preparation.session_id = $3
               AND preparation.device_id = $4
               AND resource.exam_id = preparation.exam_id
               AND resource.resource_kind = 'paper-package'
               AND resource.resource_version = preparation.paper_version;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(resourceId);
        command.Parameters.AddWithValue(preparationId);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(authenticatedDeviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResource(reader) : null;
    }

    internal static void SaveInTransaction(NpgsqlConnection connection,NpgsqlTransaction transaction,VersionedEncryptedResource resource)
    {
        Validate(resource,ManagedNodeEncryptionPolicy.None);
        using var command=new NpgsqlCommand("""
            INSERT INTO exam.versioned_resources(resource_id,exam_id,resource_kind,resource_version,encrypted_content,
                encryption_envelope,created_at,created_by) VALUES($1,$2,$3,$4,$5,$6,$7,$8);
            """,connection,transaction);
        command.Parameters.AddWithValue(resource.ResourceId);command.Parameters.AddWithValue(resource.ExamId);
        command.Parameters.AddWithValue(resource.ResourceKind);command.Parameters.AddWithValue(resource.Version);
        command.Parameters.AddWithValue(resource.EncryptedContent);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(resource.Envelope,JsonOptions));
        command.Parameters.AddWithValue(resource.CreatedAt);command.Parameters.AddWithValue(resource.CreatedBy);
        command.ExecuteNonQuery();
    }

    private static VersionedEncryptedResource ReadResource(NpgsqlDataReader reader)
    {
        var envelope = JsonSerializer.Deserialize<EncryptedPackageEnvelope>(reader.GetString(5), JsonOptions)
            ?? throw new InvalidDataException("数据库中的题包解锁信封无法解析。");
        return new VersionedEncryptedResource(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            (byte[])reader[4],
            envelope with
            {
                WrappedKeys = new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(envelope.WrappedKeys, StringComparer.Ordinal)),
                CertificateThumbprints = new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(envelope.CertificateThumbprints, StringComparer.Ordinal))
            },
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7));
    }

    private static void Validate(VersionedEncryptedResource resource, ManagedNodeEncryptionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(resource.ResourceId) ||
            string.IsNullOrWhiteSpace(resource.ExamId) ||
            string.IsNullOrWhiteSpace(resource.ResourceKind) ||
            resource.Version < 1 ||
            resource.EncryptedContent.Length == 0 ||
            string.IsNullOrWhiteSpace(resource.CreatedBy))
        {
            throw new ArgumentException("版本化加密资源缺少必要字段。", nameof(resource));
        }

        if (!string.Equals(resource.ResourceId, resource.Envelope.PackageId, StringComparison.Ordinal) ||
            resource.Version != resource.Envelope.PaperVersion)
        {
            throw new ArgumentException("资源标识或版本与加密信封不一致。", nameof(resource));
        }

        if (!policy.RequiredNodeIds.IsSubsetOf(resource.Envelope.WrappedKeys.Keys.ToHashSet(StringComparer.Ordinal)) ||
            !policy.RequiredNodeIds.IsSubsetOf(
                resource.Envelope.CertificateThumbprints.Keys.ToHashSet(StringComparer.Ordinal)))
        {
            throw new ArgumentException("题包解锁信封未覆盖全部受管主备节点。", nameof(resource));
        }
    }
}
