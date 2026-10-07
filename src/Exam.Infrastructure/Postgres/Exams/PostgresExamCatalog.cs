using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Participation;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Exams;

/// <summary>
/// 考务端考试目录：名称唯一、完整考试设置、学生端显示开关。
/// 考试本体（起止时间、生命周期、版本）仍由 版本服务负责；本服务在同一事务内
/// 同步写入学生端可见的名称、科目与由结构化规则生成的考试说明。
/// </summary>
public sealed class PostgresExamCatalog(NpgsqlDataSource dataSource, IExamVersioningService versioning, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    // 实际参试 = 已在等待页确认参试（含开始答题与已交卷）；“收齐”= 已有最终答卷。
    private const string CountsSql = """
        (SELECT count(*) FROM exam.participations p
          WHERE p.exam_id = e.exam_id AND p.business_state IN ('waiting', 'answering', 'submitted')) AS actual,
        (SELECT count(*) FROM exam.participations p
           JOIN exam.final_submissions f ON f.participation_id = p.participation_id
          WHERE p.exam_id = e.exam_id AND p.business_state IN ('waiting', 'answering', 'submitted')) AS collected
        """;

    public async ValueTask<IReadOnlyList<ExamSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        // 没有目录行、也没有学生端元数据的考试只可能是创建中途失败的残留，不列出。
        await using var command = dataSource.CreateCommand($"""
            SELECT e.exam_id, COALESCE(c.exam_name, m.display_name, e.exam_id), e.scheduled_start, e.scheduled_end,
                   e.lifecycle, COALESCE(c.visible_to_students, true), COALESCE(c.version, 0), {CountsSql},
                   v.paper_version AS published_paper_version
              FROM exam.exam_entry_access e
              LEFT JOIN exam.exam_catalog c ON c.exam_id = e.exam_id
              LEFT JOIN exam.student_exam_metadata m ON m.exam_id = e.exam_id
              LEFT JOIN exam.version_state v ON v.exam_id = e.exam_id
             WHERE c.exam_id IS NOT NULL OR m.exam_id IS NOT NULL
             ORDER BY e.scheduled_start DESC, 2;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ExamSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadSummary(reader));
        return result;
    }

    public async ValueTask<ExamDetail> GetAsync(string examId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDetailAsync(connection, null, examId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExamDetail> CreateAsync(CreateExamRequest request, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
            throw Rule(ParticipationErrorCodes.InvalidRequest, "请求标识无效。");
        ValidateOrThrow(request.Name, request.Settings);
        if (request.Schedule is null || request.Schedule.EndsAt <= request.Schedule.StartsAt)
            throw Rule(ParticipationErrorCodes.InvalidRequest, "结束时间必须晚于开始时间。");

        // examId 由请求标识决定：同一请求重试得到同一场考试，不会因中途失败留下第二场。
        var examId = "exam-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestId)))[..24].ToLowerInvariant();
        var hash = Hash(new { Operation = "create", Request = request, Actor = actor });
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await FindReplayAsync(connection, null, request.RequestId, hash, cancellationToken).ConfigureAwait(false))
                return await ReadDetailAsync(connection, null, examId, cancellationToken).ConfigureAwait(false);
            await EnsureNameFreeAsync(connection, null, request.Name, null, cancellationToken).ConfigureAwait(false);
        }

        if (!versioning.GetExam(examId).Succeeded)
        {
            var created = versioning.CreateExam(new CreateVersionedExamRequest(
                examId, request.Schedule, time.GetUtcNow(), "exam-catalog-create:" + request.RequestId));
            if (!created.Succeeded)
                throw Rule(created.ErrorCode == VersioningErrorCode.InvalidSchedule
                    ? ParticipationErrorCodes.InvalidRequest : ParticipationErrorCodes.VersionConflict,
                    "考试创建失败：" + (created.ErrorCode == VersioningErrorCode.InvalidSchedule ? "考试时间无效。" : "请重试。"));
        }

        await using var write = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await write.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockAsync(write, transaction, "exam-catalog-request:" + request.RequestId, cancellationToken).ConfigureAwait(false);
        if (await FindReplayAsync(write, transaction, request.RequestId, hash, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadDetailAsync(write, null, examId, cancellationToken).ConfigureAwait(false);
        }
        await RequireScheduledAsync(write, transaction, examId, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO exam.exam_catalog
                    (exam_id, exam_name, name_key, settings, visible_to_students, version,
                     created_at, created_by, updated_at, updated_by)
                VALUES (@exam_id, @name, @name_key, CAST(@settings AS jsonb), false, 1,
                        clock_timestamp(), @actor, clock_timestamp(), @actor);
                """, write, transaction);
            insert.Parameters.AddWithValue("exam_id", examId);
            insert.Parameters.AddWithValue("name", request.Name.Trim());
            insert.Parameters.AddWithValue("name_key", ExamSettingsRules.NameKey(request.Name));
            insert.Parameters.AddWithValue("settings", JsonSerializer.Serialize(request.Settings, Json));
            insert.Parameters.AddWithValue("actor", actor);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw NameTaken(request.Name);
        }
        await WriteStudentMetadataAsync(write, transaction, examId, request.Name, request.Settings, actor, cancellationToken).ConfigureAwait(false);
        await SaveReplayAsync(write, transaction, request.RequestId, hash, examId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDetailAsync(write, null, examId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExamDetail> UpdateSettingsAsync(string examId, UpdateExamSettingsRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
            throw Rule(ParticipationErrorCodes.InvalidRequest, "请求标识无效。");
        ValidateOrThrow(request.Name, request.Settings);
        var hash = Hash(new { Operation = "update", ExamId = examId, Request = request, Actor = actor });

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "exam-catalog-request:" + request.RequestId, cancellationToken).ConfigureAwait(false);
        if (await FindReplayAsync(connection, transaction, request.RequestId, hash, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadDetailAsync(connection, null, examId, cancellationToken).ConfigureAwait(false);
        }
        var current = await ReadCatalogVersionAsync(connection, transaction, examId, cancellationToken).ConfigureAwait(false);
        await RequireScheduledAsync(connection, transaction, examId, cancellationToken).ConfigureAwait(false);
        if (current != request.ExpectedVersion)
            throw Rule(ParticipationErrorCodes.VersionConflict, "考试设置已被其他人修改，请重新读取后再保存。");
        await EnsureNameFreeAsync(connection, transaction, request.Name, examId, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var update = new NpgsqlCommand("""
                UPDATE exam.exam_catalog
                   SET exam_name = @name, name_key = @name_key, settings = CAST(@settings AS jsonb),
                       version = version + 1, updated_at = clock_timestamp(), updated_by = @actor
                 WHERE exam_id = @exam_id;
                """, connection, transaction);
            update.Parameters.AddWithValue("exam_id", examId);
            update.Parameters.AddWithValue("name", request.Name.Trim());
            update.Parameters.AddWithValue("name_key", ExamSettingsRules.NameKey(request.Name));
            update.Parameters.AddWithValue("settings", JsonSerializer.Serialize(request.Settings, Json));
            update.Parameters.AddWithValue("actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw NameTaken(request.Name);
        }
        await WriteStudentMetadataAsync(connection, transaction, examId, request.Name, request.Settings, actor, cancellationToken).ConfigureAwait(false);
        await SaveReplayAsync(connection, transaction, request.RequestId, hash, examId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDetailAsync(connection, null, examId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 设置学生端显示。显示可随时打开；关闭时，若有已确认参试的考生尚无最终答卷则拒绝，
    /// 直到全部实际参试考生的答卷都已回收。
    /// </summary>
    public async ValueTask<ExamSummary> SetVisibilityAsync(string examId, SetExamVisibilityRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await ReadCatalogVersionAsync(connection, transaction, examId, cancellationToken).ConfigureAwait(false);
        if (!request.VisibleToStudents)
        {
            await using var pending = new NpgsqlCommand("""
                SELECT count(*) FROM exam.participations p
                 WHERE p.exam_id = @exam_id AND p.business_state IN ('waiting', 'answering', 'submitted')
                   AND NOT EXISTS (SELECT 1 FROM exam.final_submissions f WHERE f.participation_id = p.participation_id);
                """, connection, transaction);
            pending.Parameters.AddWithValue("exam_id", examId);
            if ((long)(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) > 0)
                throw Rule(ExamCatalogErrorCodes.VisibilityLocked,
                    "已有考生确认参试，全部实际参试考生的答卷回收完成前，不能取消在学生端显示。");
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE exam.exam_catalog
               SET visible_to_students = @visible, updated_at = clock_timestamp(), updated_by = @actor
             WHERE exam_id = @exam_id;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("exam_id", examId);
            update.Parameters.AddWithValue("visible", request.VisibleToStudents);
            update.Parameters.AddWithValue("actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadDetailAsync(connection, null, examId, cancellationToken).ConfigureAwait(false)).Summary;
    }

    private static void ValidateOrThrow(string? name, ExamSettings? settings)
    {
        var errors = ExamSettingsRules.Validate(name, settings);
        if (errors.Count > 0) throw Rule(ParticipationErrorCodes.InvalidRequest, string.Join(" ", errors));
    }

    private static async Task EnsureNameFreeAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string name,
        string? ownExamId, CancellationToken cancellationToken)
    {
        // 旧考试只有学生端元数据，也参与重名检查。
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM exam.exam_catalog WHERE name_key = @key AND exam_id IS DISTINCT FROM @own)
                OR EXISTS (SELECT 1 FROM exam.student_exam_metadata m
                            WHERE upper(btrim(m.display_name)) = @key AND m.exam_id IS DISTINCT FROM @own
                              AND NOT EXISTS (SELECT 1 FROM exam.exam_catalog c WHERE c.exam_id = m.exam_id));
            """, connection, transaction);
        command.Parameters.AddWithValue("key", ExamSettingsRules.NameKey(name));
        command.Parameters.AddWithValue("own", (object?)ownExamId ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) throw NameTaken(name);
    }

    private static async Task<long> ReadCatalogVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string examId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version FROM exam.exam_catalog WHERE exam_id = @exam_id FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long version
            ? version
            : throw Rule(ParticipationErrorCodes.ExamNotFound, "找不到这场考试。");
    }

    private static async Task RequireScheduledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string examId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT lifecycle FROM exam.exam_entry_access WHERE exam_id = @exam_id FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        var lifecycle = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw Rule(ParticipationErrorCodes.ExamNotFound, "找不到这场考试。");
        if (lifecycle != "scheduled")
            throw Rule(ExamCatalogErrorCodes.SettingsLocked, "考试已开始或已结束，不能再修改考试名称和科目题组设置。");
    }

    /// <summary>学生端选择考试页与等待页使用的名称、科目和考试说明；与目录写入同一事务。</summary>
    private static async Task WriteStudentMetadataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string examId, string name, ExamSettings settings, string actor, CancellationToken cancellationToken)
    {
        long instructionsVersion;
        bool ready;
        await using (var current = new NpgsqlCommand(
            "SELECT instructions_version, instructions_ready FROM exam.exam_entry_access WHERE exam_id = @exam_id;",
            connection, transaction))
        {
            current.Parameters.AddWithValue("exam_id", examId);
            await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            instructionsVersion = reader.GetInt64(0);
            ready = reader.GetBoolean(1);
        }
        // 初始的说明版本 1 尚不可用：第一次写入填充版本 1，之后每次递增（与 考试说明 元数据服务一致）。
        var next = ready ? checked(instructionsVersion + 1) : instructionsVersion;
        await using var save = new NpgsqlCommand("""
            INSERT INTO exam.student_exam_metadata
                (exam_id, display_name, category_name, subjects, version, updated_at, updated_by)
            VALUES (@exam_id, @name, NULL, CAST(@subjects AS jsonb), 1, clock_timestamp(), @actor)
            ON CONFLICT (exam_id) DO UPDATE SET
                display_name = EXCLUDED.display_name, category_name = NULL, subjects = EXCLUDED.subjects,
                version = exam.student_exam_metadata.version + 1,
                updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by;

            INSERT INTO exam.student_exam_instructions (exam_id, instructions_version, lines, created_at, created_by)
            VALUES (@exam_id, @instructions_version, CAST(@lines AS jsonb), clock_timestamp(), @actor);

            UPDATE exam.exam_entry_access
               SET instructions_ready = true, instructions_version = @instructions_version
             WHERE exam_id = @exam_id;
            """, connection, transaction);
        save.Parameters.AddWithValue("exam_id", examId);
        save.Parameters.AddWithValue("name", name.Trim());
        save.Parameters.AddWithValue("subjects", JsonSerializer.Serialize(settings.Subjects.Select(subject => subject.Name.Trim())));
        save.Parameters.AddWithValue("actor", actor);
        save.Parameters.AddWithValue("instructions_version", next);
        save.Parameters.AddWithValue("lines", JsonSerializer.Serialize(ExamSettingsRules.BuildInstructions(settings)));
        await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExamDetail> ReadDetailAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string examId, CancellationToken cancellationToken)
    {
        ExamSummary summary;
        ExamSettings settings;
        long scheduleVersion;
        await using (var command = new NpgsqlCommand($"""
            SELECT e.exam_id, COALESCE(c.exam_name, m.display_name, e.exam_id), e.scheduled_start, e.scheduled_end,
                   e.lifecycle, COALESCE(c.visible_to_students, true), COALESCE(c.version, 0), {CountsSql},
                   c.settings::text, v.schedule_version, v.paper_version AS published_paper_version
              FROM exam.exam_entry_access e
              LEFT JOIN exam.exam_catalog c ON c.exam_id = e.exam_id
              LEFT JOIN exam.student_exam_metadata m ON m.exam_id = e.exam_id
              LEFT JOIN exam.version_state v ON v.exam_id = e.exam_id
             WHERE e.exam_id = @exam_id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("exam_id", examId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw Rule(ParticipationErrorCodes.ExamNotFound, "找不到这场考试。");
            summary = ReadSummary(reader);
            settings = reader.IsDBNull(9) ? new ExamSettings([])
                : JsonSerializer.Deserialize<ExamSettings>(reader.GetString(9), Json) ?? throw new DataException("考试设置数据无效。");
            scheduleVersion = reader.IsDBNull(10) ? 0 : reader.GetInt64(10);
        }
        var lines = new List<string>();
        await using (var instructions = new NpgsqlCommand("""
            SELECT i.lines::text FROM exam.student_exam_instructions i
              JOIN exam.exam_entry_access e ON e.exam_id = i.exam_id AND e.instructions_version = i.instructions_version
             WHERE i.exam_id = @exam_id AND e.instructions_ready;
            """, connection, transaction))
        {
            instructions.Parameters.AddWithValue("exam_id", examId);
            if (await instructions.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string text)
                lines.AddRange(JsonSerializer.Deserialize<string[]>(text) ?? []);
        }
        return new ExamDetail(summary, settings, scheduleVersion, lines);
    }

    private static ExamSummary ReadSummary(NpgsqlDataReader reader)
    {
        var actual = (int)reader.GetInt64(7);
        var collected = (int)reader.GetInt64(8);
        return new ExamSummary(
            reader.GetString(0), reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetString(4) switch
            {
                "scheduled" => ExamLifecycle.Scheduled,
                "running" => ExamLifecycle.Running,
                "ended" => ExamLifecycle.Ended,
                var value => throw new DataException($"Unknown exam lifecycle '{value}'."),
            },
            reader.GetBoolean(5), actual > collected, actual, collected, reader.GetInt64(6))
        {
            PaperVersion = reader.IsDBNull(reader.GetOrdinal("published_paper_version"))
                ? null : reader.GetInt64(reader.GetOrdinal("published_paper_version"))
        };
    }

    private static async Task<bool> FindReplayAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string requestId, string hash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT payload_hash FROM exam.exam_catalog_idempotency WHERE request_id = @request_id;", connection, transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            null => false,
            string stored when stored == hash => true,
            _ => throw Rule(ParticipationErrorCodes.IdempotencyConflict, "同一请求标识已用于不同内容，请重新操作。"),
        };
    }

    private static async Task SaveReplayAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string requestId, string hash, string examId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO exam.exam_catalog_idempotency (request_id, payload_hash, response_json)
            VALUES (@request_id, @hash, CAST(@response AS jsonb));
            """, connection, transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("response", JsonSerializer.Serialize(new { examId }));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private static ParticipationRuleException NameTaken(string name) =>
        Rule(ExamCatalogErrorCodes.NameTaken, $"已有名为“{name.Trim()}”的考试，请换一个名称。");

    private static ParticipationRuleException Rule(string code, string message) => new(code, message);
}
