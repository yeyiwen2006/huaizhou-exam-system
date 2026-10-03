using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Resources;

/// <summary>
/// 一个已发布试卷版本的源材料归档行。摘要（<see cref="QuestionDigests"/>）与该版本的材料一一对应，
/// 供下一次修订核对「实际变更题号」；<see cref="PackageContentSha256"/> 是该版本学生包明文哈希，
/// 用于与包相互印证（归档与包必须来自同一次发布）。
/// </summary>
public sealed record PaperMaterialsRow(
    string ExamId,
    long PaperVersion,
    byte[] Archive,
    string ArchiveSha256,
    string PackageContentSha256,
    int FormatVersion,
    int FileCount,
    IReadOnlyDictionary<string, string> QuestionDigests,
    DateTimeOffset CreatedAt,
    string CreatedBy);

/// <summary>归档的元信息（不含归档字节）：取回端点先报哈希与身份，再流式返回内容。</summary>
public sealed record PaperMaterialsInfo(
    string ExamId,
    long PaperVersion,
    string ArchiveSha256,
    string PackageContentSha256,
    int FormatVersion,
    int FileCount,
    int ArchiveBytes,
    DateTimeOffset CreatedAt,
    string CreatedBy);

/// <summary>某版本已存基线摘要（修订核对用）：格式版本 + questionId → 摘要。</summary>
public sealed record PaperMaterialsBaseline(
    long PaperVersion,
    int FormatVersion,
    IReadOnlyDictionary<string, string> QuestionDigests);

/// <summary>
/// 源材料归档的存储。写入路径只有 <see cref="SaveInTransaction"/>：与版本发布同一事务，
/// 发布失败就一起回滚；同版本重复插入会撞主键，绝不覆盖。
/// </summary>
public sealed class PostgresPaperMaterialStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = Exam.Contracts.Common.JsonDefaults.Create();

    internal const string InsertSql = """
        INSERT INTO exam.paper_materials
            (exam_id, paper_version, archive, archive_sha256, package_content_sha256, format_version,
             file_count, archive_bytes, question_digests, created_at, created_by)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb, $10, $11);
        """;

    private const string SelectSql = """
        SELECT exam_id, paper_version, archive, archive_sha256, package_content_sha256, format_version,
               file_count, archive_bytes, question_digests::text, created_at, created_by
          FROM exam.paper_materials
         WHERE exam_id = $1 AND paper_version = $2;
        """;

    /// <summary>与版本发布同一事务地写入归档；由发布事务提交或回滚。</summary>
    internal static void SaveInTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PaperMaterialsRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Validate(row);
        using var command = new NpgsqlCommand(InsertSql, connection, transaction);
        Bind(command, row);
        command.ExecuteNonQuery();
    }

    /// <summary>独立写入（用例与后续维护路径用）。</summary>
    public async Task SaveAsync(PaperMaterialsRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        Validate(row);
        await using var connection = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(InsertSql, connection);
        Bind(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PaperMaterialsRow?> FindAsync(string examId, long paperVersion, CancellationToken ct = default)
    {
        await using var connection = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(SelectSql, connection);
        command.Parameters.AddWithValue(examId);
        command.Parameters.AddWithValue(paperVersion);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        return await reader.ReadAsync(ct) ? await ReadAsync(reader, ct) : null;
    }

    /// <summary>读修订核对所需的基线：只取摘要与格式版本，不把归档字节读进内存。</summary>
    public async Task<PaperMaterialsBaseline?> FindBaselineAsync(string examId, long paperVersion,
        CancellationToken ct = default)
    {
        await using var connection = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT paper_version, format_version, question_digests::text FROM exam.paper_materials " +
            "WHERE exam_id = $1 AND paper_version = $2;", connection);
        command.Parameters.AddWithValue(examId);
        command.Parameters.AddWithValue(paperVersion);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new PaperMaterialsBaseline(reader.GetInt64(0), reader.GetInt32(1), Digests(reader.GetString(2)));
    }

    /// <summary>只读元信息：不把归档字节读进内存。</summary>
    public async Task<PaperMaterialsInfo?> FindInfoAsync(string examId, long paperVersion,
        CancellationToken ct = default)
    {
        await using var connection = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT exam_id, paper_version, archive_sha256, package_content_sha256, format_version, " +
            "file_count, archive_bytes, created_at, created_by FROM exam.paper_materials " +
            "WHERE exam_id = $1 AND paper_version = $2;", connection);
        command.Parameters.AddWithValue(examId);
        command.Parameters.AddWithValue(paperVersion);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new PaperMaterialsInfo(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8));
    }

    /// <summary>归档的只读流：取回端点按流返回，不整包缓冲。</summary>
    public async Task<Stream?> OpenArchiveAsync(string examId, long paperVersion, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)await dataSource.OpenConnectionAsync(ct);
        try
        {
            var command = new NpgsqlCommand(
                "SELECT archive FROM exam.paper_materials WHERE exam_id = $1 AND paper_version = $2;", connection);
            command.Parameters.AddWithValue(examId);
            command.Parameters.AddWithValue(paperVersion);
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct))
            {
                await reader.DisposeAsync();
                await command.DisposeAsync();
                await connection.DisposeAsync();
                return null;
            }

            return new OwnedArchiveStream(await reader.GetStreamAsync(0, ct), reader, command, connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>按仓库既有风格用**位置参数**绑定 `$1..$11`。</summary>
    private static void Bind(NpgsqlCommand command, PaperMaterialsRow row)
    {
        command.Parameters.AddWithValue(row.ExamId);
        command.Parameters.AddWithValue(row.PaperVersion);
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, row.Archive);
        command.Parameters.AddWithValue(row.ArchiveSha256);
        command.Parameters.AddWithValue(row.PackageContentSha256);
        command.Parameters.AddWithValue(row.FormatVersion);
        command.Parameters.AddWithValue(row.FileCount);
        command.Parameters.AddWithValue(row.Archive.Length);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(row.QuestionDigests, Json));
        command.Parameters.AddWithValue(row.CreatedAt);
        command.Parameters.AddWithValue(row.CreatedBy);
    }

    private static async Task<PaperMaterialsRow> ReadAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var stream = await reader.GetStreamAsync(2, ct))
        {
            await stream.CopyToAsync(buffer, ct);
        }

        return new PaperMaterialsRow(
            reader.GetString(0),
            reader.GetInt64(1),
            buffer.ToArray(),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            Digests(reader.GetString(8)),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetString(10));
    }

    private static IReadOnlyDictionary<string, string> Digests(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static void Validate(PaperMaterialsRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ExamId) || string.IsNullOrWhiteSpace(row.CreatedBy) ||
            row.PaperVersion < 1 || row.Archive.Length == 0 ||
            row.ArchiveSha256.Length != 64 || row.PackageContentSha256.Length != 64 ||
            row.FormatVersion < 1 || row.QuestionDigests.Count == 0)
        {
            throw new ArgumentException("源材料归档行不完整或标识非法。", nameof(row));
        }
    }

    /// <summary>把归档流与其数据库资源绑在一起释放，避免取回端点提前归还连接。</summary>
    private sealed class OwnedArchiveStream(Stream inner, NpgsqlDataReader reader, NpgsqlCommand command,
        NpgsqlConnection connection) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                reader.Dispose();
                command.Dispose();
                connection.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await reader.DisposeAsync();
            await command.DisposeAsync();
            await connection.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
