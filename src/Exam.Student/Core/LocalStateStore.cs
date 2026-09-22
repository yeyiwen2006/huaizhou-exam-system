using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Microsoft.Data.Sqlite;

namespace Exam.Student.Core;

/// <summary>本地状态隔离键：按服务器、考试和考生隔离（需求：学生端本机数据按服务器、考试和考生隔离）。</summary>
public sealed record LocalStateKey(string ServerKey, string ExamId, string CandidateId);

/// <summary>
/// 已保存的本地答题状态：答案、私人标记与版本元数据（同一事务落盘，代表"最后成功保存状态"）。
/// 字段与 A05 契约（SyncProgressRequest/PersistedProgress）的本地状态字段同构。
/// </summary>
public sealed record AnswerStateRecord(
    string? ParticipationId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion,
    long AnswerVersion,
    string? CurrentQuestionId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, AnswerValue> Answers,
    IReadOnlySet<string> Marks);

/// <summary>
/// 待发送快照：本地保存成功时与答案同事务写入，内容即 A05 SyncProgressRequest 的载荷
/// （去掉 RequestId；DeviceId 由会话上下文补齐）。B05 同步时直接取用，失败重试不丢数据。
/// 私人标记不进入快照（需求：私人标记仅本机可见、不上报）。
/// </summary>
public sealed record PendingSnapshot(
    string ExamId,
    string CandidateId,
    string? ParticipationId,
    long EntryVersion,
    string? DeviceId,
    long AuthorizationVersion,
    long AnswerVersion,
    long PaperVersion,
    IReadOnlyDictionary<string, AnswerValue> Answers,
    string? CurrentQuestionId,
    DateTimeOffset SavedAt);

/// <summary>已固定提交（本地固定答卷）：一次固定后不可变更，重复固定返回原记录。</summary>
public sealed record FixedSubmissionRecord(
    string SubmissionId,
    long PaperVersion,
    long AnswerVersion,
    IReadOnlyDictionary<string, AnswerValue> FrozenAnswers,
    DateTimeOffset CreatedAt);

/// <summary>等待/解锁阶段的恢复记录（B02 状态机落 SQLite，供崩溃后恢复与 B08 重连使用）。</summary>
public sealed record PreparationRecord(
    long PaperVersion,
    long InstructionsVersion,
    string PackageFile,
    string? ParticipationId,
    string Phase,
    DateTimeOffset UpdatedAt);

/// <summary>存储写入结果：写失败不抛异常，由上层决定阻止新增修改并明示。</summary>
public sealed record StoreWriteResult(bool Succeeded, string? Error)
{
    public static StoreWriteResult Ok() => new(true, null);

    public static StoreWriteResult Fail(string error) => new(false, error);
}

/// <summary>
/// 本地事务存储（B04）：答案、私人标记、待发送快照与提交记录的持久化接口。
/// 每次保存均为一个事务；实现必须保证崩溃后可恢复"最后成功保存状态"。
/// </summary>
public interface ILocalStateStore : IDisposable
{
    /// <summary>读取最后成功保存的答题状态（含私人标记）；无任何记录时返回 null。</summary>
    Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default);

    /// <summary>同事务写入状态行、全部答案与待发送快照（快照可为 null：仅切当前题）。</summary>
    Task<StoreWriteResult> SaveAnswerStateAsync(
        LocalStateKey key,
        AnswerStateRecord state,
        PendingSnapshot? snapshot,
        CancellationToken ct = default);

    /// <summary>单独保存私人标记（整体替换；仅本机，不进快照）。</summary>
    Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default);

    /// <summary>读取待发送快照（B05 消费；无快照返回 null）。</summary>
    Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default);

    /// <summary>
    /// 固定提交：首次写入 record；已存在固定提交时保留原记录（已固定提交不生成新答案、
    /// 重复提交不改变原答案），返回原记录。Result.Succeeded 表示磁盘操作成功。
    /// </summary>
    Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default);

    /// <summary>读取已固定提交；无则 null。</summary>
    Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default);

    /// <summary>保存等待/解锁恢复记录。</summary>
    Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default);

    /// <summary>读取恢复记录；无则 null。</summary>
    Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default);

    /// <summary>删除该键全部本地数据（测试与数据清理用）。</summary>
    Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default);
}

/// <summary>
/// SQLite 本地状态存储：WAL 日志 + synchronous=FULL，磁盘操作全部在独立工作线程串行执行
/// （执行指南：学生端使用 Microsoft.Data.Sqlite、本地 SQLite 的 WAL 与 FULL 同步，
/// 磁盘操作在独立工作线程执行）。写失败返回 StoreWriteResult.Fail，不向调用方抛异常。
/// </summary>
public sealed class SqliteLocalStateStore : ILocalStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();

    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly Thread _worker;
    private volatile bool _disposed;

    private sealed record WorkItem(Func<SqliteConnection, object?> Work, TaskCompletionSource<object?> Tcs);

    public SqliteLocalStateStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 10,
            // 关闭连接池：Dispose 后立即释放数据库文件句柄（测试删除临时目录；桌面单连接无池化收益）
            Pooling = false,
        }.ToString();

        _worker = new Thread(Run) { IsBackground = true, Name = "StudentLocalState" };
        _worker.Start();

        // 等待工作线程完成连接、PRAGMA 与建表（失败即向上抛，应用按"本地存储不可用"处理）
        Post<object?>(static _ => null).GetAwaiter().GetResult();
    }

    private string ConnectionString { get; }

    private void Run()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using (var journal = new SqliteCommand("PRAGMA journal_mode=WAL;", connection))
        {
            journal.ExecuteScalar();
        }

        using (var sync = new SqliteCommand("PRAGMA synchronous=FULL;", connection))
        {
            sync.ExecuteNonQuery();
        }

        EnsureSchema(connection);

        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item.Tcs.SetResult(item.Work(connection));
            }
            catch (Exception ex)
            {
                item.Tcs.SetException(ex);
            }
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS answer_state (
                server_key           TEXT NOT NULL,
                exam_id              TEXT NOT NULL,
                candidate_id         TEXT NOT NULL,
                participation_id     TEXT,
                entry_version        INTEGER NOT NULL,
                authorization_version INTEGER NOT NULL,
                paper_version        INTEGER NOT NULL,
                answer_version       INTEGER NOT NULL,
                current_question_id  TEXT,
                updated_at           TEXT NOT NULL,
                PRIMARY KEY (server_key, exam_id, candidate_id)
            );

            CREATE TABLE IF NOT EXISTS answers (
                server_key   TEXT NOT NULL,
                exam_id      TEXT NOT NULL,
                candidate_id TEXT NOT NULL,
                question_id  TEXT NOT NULL,
                kind         INTEGER NOT NULL,
                option_ids   TEXT NOT NULL,
                PRIMARY KEY (server_key, exam_id, candidate_id, question_id)
            );

            CREATE TABLE IF NOT EXISTS marks (
                server_key   TEXT NOT NULL,
                exam_id      TEXT NOT NULL,
                candidate_id TEXT NOT NULL,
                question_id  TEXT NOT NULL,
                PRIMARY KEY (server_key, exam_id, candidate_id, question_id)
            );

            CREATE TABLE IF NOT EXISTS pending_snapshot (
                server_key     TEXT NOT NULL,
                exam_id        TEXT NOT NULL,
                candidate_id   TEXT NOT NULL,
                answer_version INTEGER NOT NULL,
                payload        TEXT NOT NULL,
                updated_at     TEXT NOT NULL,
                PRIMARY KEY (server_key, exam_id, candidate_id)
            );

            CREATE TABLE IF NOT EXISTS fixed_submission (
                server_key     TEXT NOT NULL,
                exam_id        TEXT NOT NULL,
                candidate_id   TEXT NOT NULL,
                submission_id  TEXT NOT NULL,
                paper_version  INTEGER NOT NULL,
                answer_version INTEGER NOT NULL,
                answers_json   TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                PRIMARY KEY (server_key, exam_id, candidate_id)
            );

            CREATE TABLE IF NOT EXISTS preparation (
                server_key           TEXT NOT NULL,
                exam_id              TEXT NOT NULL,
                candidate_id         TEXT NOT NULL,
                paper_version        INTEGER NOT NULL,
                instructions_version INTEGER NOT NULL,
                package_file         TEXT NOT NULL,
                participation_id     TEXT,
                phase                TEXT NOT NULL,
                updated_at           TEXT NOT NULL,
                PRIMARY KEY (server_key, exam_id, candidate_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    public Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default) =>
        Post(conn => LoadState(conn, key));

    public Task<StoreWriteResult> SaveAnswerStateAsync(
        LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default) =>
        Post(conn =>
        {
            try
            {
                using var tx = conn.BeginTransaction();
                UpsertState(conn, tx, key, state);
                ReplaceRows(conn, tx, key, "answers", state.Answers.Keys,
                    (cmd, questionId) =>
                    {
                        cmd.Parameters.AddWithValue("$question_id", questionId);
                        cmd.Parameters.AddWithValue("$kind", (int)state.Answers[questionId].Kind);
                        cmd.Parameters.AddWithValue("$option_ids",
                            JsonSerializer.Serialize(state.Answers[questionId].OptionIds, JsonOptions));
                    });
                if (snapshot is not null)
                {
                    UpsertSnapshot(conn, tx, key, snapshot);
                }

                tx.Commit();
                return StoreWriteResult.Ok();
            }
            catch (Exception e) when (e is SqliteException or IOException)
            {
                return StoreWriteResult.Fail($"本地保存失败：{e.Message}");
            }
        });

    public Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default) =>
        Post(conn =>
        {
            try
            {
                using var tx = conn.BeginTransaction();
                ReplaceRows(conn, tx, key, "marks", marks,
                    (cmd, questionId) => cmd.Parameters.AddWithValue("$question_id", questionId));
                tx.Commit();
                return StoreWriteResult.Ok();
            }
            catch (Exception e) when (e is SqliteException or IOException)
            {
                return StoreWriteResult.Fail($"私人标记保存失败：{e.Message}");
            }
        });

    public Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default) =>
        Post(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM pending_snapshot WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;";
            AddKey(cmd, key);
            return cmd.ExecuteScalar() is string json
                ? JsonSerializer.Deserialize<PendingSnapshot>(json, JsonOptions)
                : null;
        });

    public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default) =>
        Post<(StoreWriteResult, FixedSubmissionRecord?)>(conn =>
        {
            try
            {
                using var tx = conn.BeginTransaction();

                // 已固定提交不生成新答案：存在即返回原记录（幂等，保留第一次）
                var existing = LoadFixedSubmission(conn, tx, key);
                if (existing is not null)
                {
                    tx.Commit();
                    return (StoreWriteResult.Ok(), existing);
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO fixed_submission
                            (server_key, exam_id, candidate_id, submission_id, paper_version,
                             answer_version, answers_json, created_at)
                        VALUES ($s, $e, $c, $submission_id, $paper_version, $answer_version, $answers_json, $created_at);
                        """;
                    AddKey(cmd, key);
                    cmd.Parameters.AddWithValue("$submission_id", record.SubmissionId);
                    cmd.Parameters.AddWithValue("$paper_version", record.PaperVersion);
                    cmd.Parameters.AddWithValue("$answer_version", record.AnswerVersion);
                    cmd.Parameters.AddWithValue("$answers_json", SerializeAnswers(record.FrozenAnswers));
                    cmd.Parameters.AddWithValue("$created_at", Iso(record.CreatedAt));
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return (StoreWriteResult.Ok(), record);
            }
            catch (Exception e) when (e is SqliteException or IOException)
            {
                return (StoreWriteResult.Fail($"固定提交保存失败：{e.Message}"), null);
            }
        });

    public Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default) =>
        Post(conn =>
        {
            using var tx = conn.BeginTransaction();
            var record = LoadFixedSubmission(conn, tx, key);
            tx.Commit();
            return record;
        });

    public Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default) =>
        Post(conn =>
        {
            try
            {
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO preparation
                        (server_key, exam_id, candidate_id, paper_version, instructions_version,
                         package_file, participation_id, phase, updated_at)
                    VALUES ($s, $e, $c, $paper_version, $instructions_version, $package_file,
                            $participation_id, $phase, $updated_at)
                    ON CONFLICT(server_key, exam_id, candidate_id) DO UPDATE SET
                        paper_version=$paper_version, instructions_version=$instructions_version,
                        package_file=$package_file, participation_id=$participation_id,
                        phase=$phase, updated_at=$updated_at;
                    """;
                AddKey(cmd, key);
                cmd.Parameters.AddWithValue("$paper_version", record.PaperVersion);
                cmd.Parameters.AddWithValue("$instructions_version", record.InstructionsVersion);
                cmd.Parameters.AddWithValue("$package_file", record.PackageFile);
                cmd.Parameters.AddWithValue("$participation_id", (object?)record.ParticipationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$phase", record.Phase);
                cmd.Parameters.AddWithValue("$updated_at", Iso(record.UpdatedAt));
                cmd.ExecuteNonQuery();
                tx.Commit();
                return StoreWriteResult.Ok();
            }
            catch (Exception e) when (e is SqliteException or IOException)
            {
                return StoreWriteResult.Fail($"恢复记录保存失败：{e.Message}");
            }
        });

    public Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default) =>
        Post(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT paper_version, instructions_version, package_file, participation_id, phase, updated_at
                FROM preparation WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;
                """;
            AddKey(cmd, key);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PreparationRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)));
        });

    public Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default) =>
        Post(conn =>
        {
            try
            {
                using var tx = conn.BeginTransaction();
                foreach (var table in new[] { "answer_state", "answers", "marks", "pending_snapshot", "fixed_submission", "preparation" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM {table} WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;";
                    AddKey(cmd, key);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return StoreWriteResult.Ok();
            }
            catch (Exception e) when (e is SqliteException or IOException)
            {
                return StoreWriteResult.Fail($"本地数据删除失败：{e.Message}");
            }
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private Task<T> Post<T>(Func<SqliteConnection, T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(conn => work(conn), tcs));
        return InterceptAsync<T>(tcs.Task);
    }

    private static async Task<T> InterceptAsync<T>(Task<object?> task)
    {
        var result = await task.ConfigureAwait(false);
        return result is null ? default! : (T)result;
    }

    private static AnswerStateRecord? LoadState(SqliteConnection conn, LocalStateKey key)
    {
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT participation_id, entry_version, authorization_version, paper_version,
                       answer_version, current_question_id, updated_at
                FROM answer_state WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;
                """;
            AddKey(cmd, key);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var record = new AnswerStateRecord(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(6)),
                    LoadAnswers(conn, tx, key),
                    LoadMarks(conn, tx, key));
                tx.Commit();
                return record;
            }
        }

        // 无状态行但存在私人标记（如仅标记过）：返回仅含标记的最小记录，避免标记丢失
        var marksOnly = LoadMarks(conn, tx, key);
        tx.Commit();
        return marksOnly.Count > 0
            ? new AnswerStateRecord(
                null, 0, 0, 0, 0, null, DateTimeOffset.MinValue, new Dictionary<string, AnswerValue>(), marksOnly)
            : null;
    }

    private static IReadOnlyDictionary<string, AnswerValue> LoadAnswers(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT question_id, kind, option_ids FROM answers WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;";
        AddKey(cmd, key);
        using var reader = cmd.ExecuteReader();
        var answers = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var optionIds = JsonSerializer.Deserialize<string[]>(reader.GetString(2), JsonOptions) ?? Array.Empty<string>();
            answers[reader.GetString(0)] = new AnswerValue((AnswerKind)reader.GetInt64(1), optionIds);
        }

        return answers;
    }

    private static IReadOnlySet<string> LoadMarks(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT question_id FROM marks WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;";
        AddKey(cmd, key);
        using var reader = cmd.ExecuteReader();
        var marks = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            marks.Add(reader.GetString(0));
        }

        return marks;
    }

    private static FixedSubmissionRecord? LoadFixedSubmission(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT submission_id, paper_version, answer_version, answers_json, created_at
            FROM fixed_submission WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;
            """;
        AddKey(cmd, key);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var json = reader.GetString(3);
        using var doc = JsonDocument.Parse(json);
        var answers = doc.RootElement.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.Deserialize<AnswerValue>(JsonOptions)!,
            StringComparer.Ordinal);
        return new FixedSubmissionRecord(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            answers,
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    private static void UpsertState(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key, AnswerStateRecord state)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO answer_state
                (server_key, exam_id, candidate_id, participation_id, entry_version, authorization_version,
                 paper_version, answer_version, current_question_id, updated_at)
            VALUES ($s, $e, $c, $participation_id, $entry_version, $authorization_version,
                    $paper_version, $answer_version, $current_question_id, $updated_at)
            ON CONFLICT(server_key, exam_id, candidate_id) DO UPDATE SET
                participation_id=$participation_id, entry_version=$entry_version,
                authorization_version=$authorization_version, paper_version=$paper_version,
                answer_version=$answer_version, current_question_id=$current_question_id,
                updated_at=$updated_at;
            """;
        AddKey(cmd, key);
        cmd.Parameters.AddWithValue("$participation_id", (object?)state.ParticipationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$entry_version", state.EntryVersion);
        cmd.Parameters.AddWithValue("$authorization_version", state.AuthorizationVersion);
        cmd.Parameters.AddWithValue("$paper_version", state.PaperVersion);
        cmd.Parameters.AddWithValue("$answer_version", state.AnswerVersion);
        cmd.Parameters.AddWithValue("$current_question_id", (object?)state.CurrentQuestionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", Iso(state.UpdatedAt));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertSnapshot(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key, PendingSnapshot snapshot)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO pending_snapshot
                (server_key, exam_id, candidate_id, answer_version, payload, updated_at)
            VALUES ($s, $e, $c, $answer_version, $payload, $updated_at)
            ON CONFLICT(server_key, exam_id, candidate_id) DO UPDATE SET
                answer_version=$answer_version, payload=$payload, updated_at=$updated_at;
            """;
        AddKey(cmd, key);
        cmd.Parameters.AddWithValue("$answer_version", snapshot.AnswerVersion);
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot, JsonOptions));
        cmd.Parameters.AddWithValue("$updated_at", Iso(snapshot.SavedAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>整体替换某键下的子表行（answers/marks），保证落盘结果与内存快照一致。</summary>
    private static void ReplaceRows(
        SqliteConnection conn,
        SqliteTransaction tx,
        LocalStateKey key,
        string table,
        IEnumerable<string> questionIds,
        Action<SqliteCommand, string> bindValues)
    {
        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = $"DELETE FROM {table} WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;";
            AddKey(clear, key);
            clear.ExecuteNonQuery();
        }

        foreach (var questionId in questionIds)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = table switch
            {
                "answers" => "INSERT INTO answers (server_key, exam_id, candidate_id, question_id, kind, option_ids) VALUES ($s, $e, $c, $question_id, $kind, $option_ids);",
                _ => "INSERT INTO marks (server_key, exam_id, candidate_id, question_id) VALUES ($s, $e, $c, $question_id);",
            };
            AddKey(insert, key);
            bindValues(insert, questionId);
            insert.ExecuteNonQuery();
        }
    }

    private static string SerializeAnswers(IReadOnlyDictionary<string, AnswerValue> answers) =>
        JsonSerializer.Serialize(
            answers.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            JsonOptions);

    private static void AddKey(SqliteCommand cmd, LocalStateKey key)
    {
        cmd.Parameters.AddWithValue("$s", key.ServerKey);
        cmd.Parameters.AddWithValue("$e", key.ExamId);
        cmd.Parameters.AddWithValue("$c", key.CandidateId);
    }

    private static string Iso(DateTimeOffset time) => time.ToString("O");
}
