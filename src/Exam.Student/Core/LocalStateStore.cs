using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Microsoft.Data.Sqlite;

namespace Exam.Student.Core;

public sealed record LocalStateKey(string ServerKey, string ExamId, string CandidateId);

/// <summary>最后成功保存的完整状态；答案、标记、当前题和快照在同一事务内落盘。</summary>
public sealed record AnswerStateRecord(
    string? ParticipationId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion,
    long AnswerVersion,
    string? CurrentQuestionId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, AnswerValue> Answers,
    IReadOnlySet<string> Marks,
    string? DeviceId = null);

/// <summary>完整的待发送载荷，RequestId 在本快照的所有重试中不变；不含私人标记。</summary>
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
    DateTimeOffset SavedAt,
    string? RequestId = null);

/// <summary>固定后不可覆盖；发送标识、授权元组及原答案共同构成恢复凭据。</summary>
public sealed record FixedSubmissionRecord(
    string SubmissionId,
    long PaperVersion,
    long AnswerVersion,
    IReadOnlyDictionary<string, AnswerValue> FrozenAnswers,
    DateTimeOffset CreatedAt,
    string? RequestId = null,
    string? ParticipationId = null,
    long EntryVersion = 0,
    string? DeviceId = null,
    long AuthorizationVersion = 0,
    SubmissionReceipt? Receipt = null);

/// <summary>兼容的资源准备记录；B02 受保护控制日志仍由 ExamPackageManager 管理。</summary>
public sealed record PreparationRecord(
    long PaperVersion,
    long InstructionsVersion,
    string PackageFile,
    string? ParticipationId,
    string Phase,
    DateTimeOffset UpdatedAt);

/// <summary>B06 试卷修题复核记录：考生确认已复核修订版试卷的时间凭证，首次确认后不可覆盖。</summary>
public sealed record PaperUpdateReviewRecord(
    long PaperVersion,
    DateTimeOffset ReceivedAt,
    DateTimeOffset InstalledAt,
    DateTimeOffset ConfirmedAt);

/// <summary>
/// B06 待上报发件箱：失焦事件与复核确认先落盘，再由同步回合补报；服务器确认后删除。
/// ItemId 唯一（失焦=事件标识，复核=版本号），重试沿用首次写入的载荷与请求标识。
/// </summary>
public sealed record PendingUploadRecord(string ItemId, string Kind, string Payload);

public sealed record StoreWriteResult(bool Succeeded, string? Error)
{
    public static StoreWriteResult Ok() => new(true, null);
    public static StoreWriteResult Fail(string error) => new(false, error);
}

public interface ILocalStateStore : IDisposable
{
    Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default);

    /// <summary>新状态必须携带一致的快照；null 只允许完全不变的重放。</summary>
    Task<StoreWriteResult> SaveAnswerStateAsync(
        LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default);

    /// <summary>旧入口显式拒绝无版本写入；标记也必须通过完整状态事务保存。</summary>
    Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default);

    Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default);
    Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default);
    /// <summary>将尚未成功保存的编辑及固定提交共同提交；失败时三者均不能推进。</summary>
    Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, AnswerStateRecord pendingState,
        PendingSnapshot pendingSnapshot, CancellationToken ct = default);
    Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default);
    Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default);
    Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default);

    /// <summary>保存试卷修题复核确认；同一版本首次确认后保持原记录，不因重复确认覆盖。</summary>
    Task<StoreWriteResult> SavePaperUpdateReviewAsync(
        LocalStateKey key, PaperUpdateReviewRecord record, CancellationToken ct = default);

    /// <summary>按版本升序读取全部复核确认记录。</summary>
    Task<IReadOnlyList<PaperUpdateReviewRecord>> LoadPaperUpdateReviewsAsync(
        LocalStateKey key, CancellationToken ct = default);

    /// <summary>追加待上报记录；同一 ItemId 已存在时保持原载荷（重试不换请求标识）。</summary>
    Task<StoreWriteResult> EnqueueUploadAsync(
        LocalStateKey key, PendingUploadRecord record, CancellationToken ct = default);

    /// <summary>按落盘顺序读取待上报记录（最多 limit 条）。</summary>
    Task<IReadOnlyList<PendingUploadRecord>> LoadUploadsAsync(
        LocalStateKey key, int limit, CancellationToken ct = default);

    /// <summary>删除已被服务器确认的待上报记录；只允许删除确实存在的标识。</summary>
    Task<StoreWriteResult> DeleteUploadAsync(
        LocalStateKey key, string itemId, CancellationToken ct = default);

    /// <summary>只允许清理没有答题状态的准备记录，正常客户端不能删除答案或固定提交。</summary>
    Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default);
}

/// <summary>
/// 专用线程上的 SQLite WAL/FULL 存储。完整状态以一个 JSON 载荷保存，和待发送快照共同提交。
/// .identity 是数据库初始化凭据，不是备份或内容摘要；原库/凭据缺失时不能重建替代空库。
/// 本地记录不会赋予作答权限，恢复后仍须核对服务端当前授权。
/// </summary>
public sealed class SqliteLocalStateStore : ILocalStateStore, ISubmissionReceiptStore
{
    private const int SchemaVersion = 3;
    private const int LegacySchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();
    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>(), 128);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycleLock = new();
    private readonly Thread _worker;
    private readonly string _databasePath;
    private readonly string _identityPath;
    private bool _disposed;
    private bool _queueDisposed;
    private static readonly object InitializationRegistryLock = new();
    private static readonly Dictionary<string, InitializationGate> InitializationGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private sealed class InitializationGate
    {
        public readonly object Sync = new();
        public int Users;
    }
    private sealed class InitializationLease(string path, InitializationGate gate) : IDisposable
    {
        public void Dispose()
        {
            Monitor.Exit(gate.Sync);
            lock (InitializationRegistryLock)
                if (--gate.Users == 0) InitializationGates.Remove(path);
        }
    }

    private IDisposable EnterLocalInitialization()
    {
        InitializationGate gate;
        lock (InitializationRegistryLock)
        {
            if (!InitializationGates.TryGetValue(_databasePath, out gate!))
                InitializationGates.Add(_databasePath, gate = new());
            gate.Users++;
        }
        // A live initializer in this process is not a stale cross-process lock. Wait on its
        // actual lifetime before starting the bounded file-lock wait; no UI thread blocks.
        Monitor.Enter(gate.Sync);
        return new InitializationLease(_databasePath, gate);
    }
    private Exception? _workerFailure;

    private sealed record StoreIdentity(int SchemaVersion, string DatabaseId);
    private sealed record WorkItem(
        Func<SqliteConnection, object?> Work, TaskCompletionSource<object?> Tcs, CancellationToken Cancellation);

    // 数组用于读取私人标记，避免依赖 JSON 对 IReadOnlySet 接口的实例化行为。
    private sealed record StatePayload(
        string? ParticipationId, long EntryVersion, long AuthorizationVersion, long PaperVersion,
        long AnswerVersion, string? CurrentQuestionId, DateTimeOffset UpdatedAt,
        Dictionary<string, AnswerValue>? Answers, string[]? Marks, string? DeviceId);

    public SqliteLocalStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:" || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("答题状态必须保存在普通本地数据库文件中。", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        _identityPath = _databasePath + ".identity";
        _worker = new Thread(Run) { IsBackground = true, Name = "StudentLocalState" };
        _worker.Start();
    }

    private void Run()
    {
        try
        {
            using var connection = OpenInitializedConnection();
            _ready.TrySetResult(true);

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item.Cancellation.IsCancellationRequested)
                {
                    item.Tcs.TrySetCanceled(item.Cancellation);
                    continue;
                }
                try
                {
                    if (!File.Exists(_databasePath) || !File.Exists(_identityPath))
                        throw new InvalidDataException("本地数据库或初始化记录缺失；已停止保存，请保留现有文件。");
                    // 开始执行后完成事务并报告真实提交结果，不能取消已提交写入而伪报未保存。
                    item.Tcs.TrySetResult(item.Work(connection));
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_lifecycleLock)
            {
                _workerFailure = ex;
                _queue.CompleteAdding();
            }
            _ready.TrySetException(ex);
            while (_queue.TryTake(out var item)) item.Tcs.TrySetException(ex);
        }
    }

    private SqliteConnection OpenInitializedConnection()
    {
        using var local = EnterLocalInitialization();
        using var initializationLock = AcquireInitializationLock();
        var (identity, isNew) = ResolveIdentity();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 5, Pooling = false, ForeignKeys = true,
        }.ToString());
        try
        {
            connection.Open();
            if (!isNew) identity = ValidateDatabase(connection, identity);
            ConfigureDurability(connection);
            if (isNew) CreateSchema(connection, identity);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private FileStream AcquireInitializationLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Keep the empty lock file: deleting it on close introduces a delete-pending
                // race with another opener. Exclusivity comes from the handle, not file presence.
                return new FileStream(_databasePath + ".initializing", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException ex) when (((ex.HResult & 0xffff) is 32 or 33) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                // 只等待另一实例的短期初始化，不在 UI 上等待，也不触碰其初始化文件。
                Thread.Sleep(25);
            }
        }
    }

    private (StoreIdentity Identity, bool IsNew) ResolveIdentity()
    {
        var exists = File.Exists(_databasePath);
        var identityExists = File.Exists(_identityPath);
        var hasRecovery = File.Exists(_databasePath + "-wal") || File.Exists(_databasePath + "-shm") ||
                          File.Exists(_databasePath + "-journal");
        if (exists || identityExists || hasRecovery)
        {
            if (!exists || !identityExists)
                throw new InvalidDataException("已有本地状态文件不完整，不能新建空库；请保留数据库、日志和初始化记录。");
            if (new FileInfo(_identityPath).Length > 4096)
                throw new InvalidDataException("本地数据库初始化记录无效。");
            var identity = JsonSerializer.Deserialize<StoreIdentity>(
                File.ReadAllText(_identityPath, new UTF8Encoding(false, true)), JsonOptions);
            if (identity is null ||
                (identity.SchemaVersion is not (LegacySchemaVersion or 2 or SchemaVersion)) ||
                !Guid.TryParseExact(identity.DatabaseId, "N", out _))
                throw new InvalidDataException("本地数据库初始化记录损坏或版本不受支持。");
            return (identity, false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var created = new StoreIdentity(SchemaVersion, Guid.NewGuid().ToString("N"));
        // CreateNew 不覆盖文件。初始化中途失败也保留证据，下次启动不能悄悄重建。
        using (var marker = new FileStream(_identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                   4096, FileOptions.WriteThrough))
        {
            marker.Write(JsonSerializer.SerializeToUtf8Bytes(created, JsonOptions));
            marker.Flush(flushToDisk: true);
        }
        using (var database = new FileStream(_databasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            database.Flush(flushToDisk: true);
        return (created, true);
    }

    private static void ConfigureDurability(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地数据库不能启用 WAL，未开始答题存储。");
        command.CommandText = "PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA synchronous;";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 2)
            throw new InvalidDataException("本地数据库不能启用 FULL 同步，未开始答题存储。");
    }

    private StoreIdentity ValidateDatabase(SqliteConnection connection, StoreIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new InvalidDataException("本地数据库完整性检查失败；已保留原文件，不能自动重建。");
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is not (LegacySchemaVersion or 2 or SchemaVersion) ||
            (version == LegacySchemaVersion && identity.SchemaVersion != LegacySchemaVersion) ||
            // 侧文件只在新结构的事务提交后才升级：侧文件领先于数据库是不可能状态。
            (version == 2 && identity.SchemaVersion == SchemaVersion))
            throw new InvalidDataException("本地数据库与初始化记录的结构版本不受支持，原文件未修改。");
        command.CommandText = "SELECT database_id FROM store_identity WHERE singleton=1;";
        if (!string.Equals(command.ExecuteScalar() as string, identity.DatabaseId, StringComparison.Ordinal))
            throw new InvalidDataException("本地数据库与初始化记录不匹配，不能迁移或替换其他数据库。");
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("本地答题状态关联损坏；已保留原文件。");
        foreach (var table in new[] { "answer_state", "pending_snapshot", "fixed_submission", "preparation" })
        {
            command.CommandText = $"SELECT server_key,exam_id,candidate_id,payload FROM {table} LIMIT 0;";
            using var reader = command.ExecuteReader();
        }
        if (version == LegacySchemaVersion)
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='paper_update_review';";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("旧版数据库出现非预期复核表，未自动覆盖或迁移。");
            // 身份与旧结构均核对后才改变持久化设置；DDL 和 user_version 同一 FULL 事务提交。
            ConfigureDurability(connection);
            UpgradeSchemaV1ToV2(connection);
            version = 2;
        }
        if (version == 2)
        {
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='upload_outbox';";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("旧版数据库出现非预期发件箱表，未自动覆盖或迁移。");
            ConfigureDurability(connection);
            UpgradeSchemaV2ToV3(connection);
        }
        ValidateReviewTable(connection);
        ValidateOutboxTable(connection);
        if (identity.SchemaVersion != SchemaVersion)
        {
            // DB 结构已领先于侧文件是唯一可恢复组合（事务先提交、侧文件后替换）。
            // 已核对 database_id、完整结构及外键，不能仅凭版本号修复任意文件。
            identity = identity with { SchemaVersion = SchemaVersion };
            PersistIdentityUpgrade(identity);
        }
        return identity;
    }

    private static void ValidateReviewTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(paper_update_review);";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, string Type, long Required, long Primary)>();
        while (reader.Read()) columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(5)));
        (string, string, long, long)[] expected =
        [ ("server_key", "TEXT", 1, 1), ("exam_id", "TEXT", 1, 2), ("candidate_id", "TEXT", 1, 3),
          ("paper_version", "INTEGER", 1, 4), ("payload", "TEXT", 1, 0) ];
        if (!columns.SequenceEqual(expected))
            throw new InvalidDataException("试卷复核表的字段或唯一键不符合已知结构，原库已保留。");
    }

    private static void ValidateOutboxTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(upload_outbox);";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, string Type, long Required, long Primary)>();
        while (reader.Read()) columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(5)));
        (string, string, long, long)[] expected =
        [ ("server_key", "TEXT", 1, 1), ("exam_id", "TEXT", 1, 2), ("candidate_id", "TEXT", 1, 3),
          ("item_id", "TEXT", 1, 4), ("kind", "TEXT", 1, 0), ("payload", "TEXT", 1, 0) ];
        if (!columns.SequenceEqual(expected))
            throw new InvalidDataException("待上报发件箱表的字段或唯一键不符合已知结构，原库已保留。");
    }

    private static void UpgradeSchemaV1ToV2(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE paper_update_review(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL,
                paper_version INTEGER NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id,paper_version));
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private static void UpgradeSchemaV2ToV3(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE upload_outbox(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL,
                item_id TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id,item_id));
            PRAGMA user_version=3;
            """;
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private void PersistIdentityUpgrade(StoreIdentity identity)
    {
        // 与初始化相同的临时文件替换写入，升级后身份记录与数据库结构版本保持一致。
        var temporary = _identityPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions));
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporary, _identityPath, _identityPath + ".bak");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CreateSchema(SqliteConnection connection, StoreIdentity identity)
    {
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE store_identity(
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), database_id TEXT NOT NULL);
            CREATE TABLE answer_state(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id));
            CREATE TABLE pending_snapshot(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id),
                FOREIGN KEY(server_key,exam_id,candidate_id) REFERENCES answer_state(server_key,exam_id,candidate_id));
            CREATE TABLE fixed_submission(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id),
                FOREIGN KEY(server_key,exam_id,candidate_id) REFERENCES answer_state(server_key,exam_id,candidate_id));
            CREATE TABLE preparation(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id));
            CREATE TABLE paper_update_review(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL,
                paper_version INTEGER NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id,paper_version));
            CREATE TABLE upload_outbox(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL,
                item_id TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id,item_id));
            INSERT INTO store_identity(singleton,database_id) VALUES(1,$database_id);
            PRAGMA user_version=3;
            """;
        command.Parameters.AddWithValue("$database_id", identity.DatabaseId);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default) =>
        ReadAsync(key, (conn, tx) => LoadState(conn, tx, key), ct);

    public Task<StoreWriteResult> SaveAnswerStateAsync(
        LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default)
    {
        try
        {
            ValidateKey(key);
            ArgumentNullException.ThrowIfNull(state);
            state = CloneState(state);
            snapshot = snapshot is null ? null : snapshot with { Answers = CloneAnswers(snapshot.Answers) };
            ValidateState(state);
            if (snapshot is not null) ValidateSnapshot(key, state, snapshot);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return Task.FromResult(StoreWriteResult.Fail($"本地保存被拒绝：{ex.Message}"));
        }

        return WriteAsync(key, (conn, tx) => SaveStateCore(conn, tx, key, state, snapshot), ct);
    }

    private static void SaveStateCore(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key,
        AnswerStateRecord state, PendingSnapshot? snapshot)
    {
        if (LoadFixedRaw(conn, tx, key) is not null)
            throw new InvalidDataException("答卷已固定，不能继续修改答案、标记或当前题。");
        var previous = LoadState(conn, tx, key);
        var previousSnapshot = LoadSnapshotRaw(conn, tx, key);
        if (previous is not null)
        {
            RequireSameAuthorization(previous, state);
            Require(state.PaperVersion >= previous.PaperVersion && state.AnswerVersion >= previous.AnswerVersion,
                "本地答案或题包版本不能回退。");
            if (state.AnswerVersion == previous.AnswerVersion)
            {
                Require(SameState(previous, state) &&
                    (snapshot is null || SameSnapshot(previousSnapshot!, snapshot)),
                    "同一答案版本不能携带不同状态或新的请求标识。");
                return;
            }
            Require(snapshot is null || snapshot.RequestId != previousSnapshot!.RequestId,
                "新快照必须使用新请求标识，重试必须保持原版本和原载荷。");
        }
        Require(snapshot is not null, "保存新状态必须同时保存完整待发送快照。");
        Put(conn, tx, key, "answer_state", state);
        Put(conn, tx, key, "pending_snapshot", snapshot!);
    }

    public Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(StoreWriteResult.Fail("标记也需要完整授权、递增版本和同事务快照，请使用完整状态保存接口。"));
    }

    public Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default) =>
        ReadAsync(key, (conn, tx) =>
        {
            _ = LoadState(conn, tx, key);
            return LoadSnapshotRaw(conn, tx, key);
        }, ct);

    public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default) =>
        TryFixSubmissionCoreAsync(key, record, null, null, ct);

    public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, AnswerStateRecord pendingState,
        PendingSnapshot pendingSnapshot, CancellationToken ct = default) =>
        TryFixSubmissionCoreAsync(key, record, pendingState, pendingSnapshot, ct);

    private async Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionCoreAsync(
        LocalStateKey key, FixedSubmissionRecord record, AnswerStateRecord? pendingState,
        PendingSnapshot? pendingSnapshot, CancellationToken ct)
    {
        try
        {
            ValidateKey(key);
            ArgumentNullException.ThrowIfNull(record);
            record = record with { FrozenAnswers = CloneAnswers(record.FrozenAnswers) };
            ValidateFixed(record);
            Require((pendingState is null) == (pendingSnapshot is null), "待保存状态和快照必须一起提供。");
            if (pendingState is not null)
            {
                pendingState = CloneState(pendingState);
                Require(pendingSnapshot is not null, "固定待保存状态必须携带其原始待发送快照。");
                pendingSnapshot = pendingSnapshot! with { Answers = CloneAnswers(pendingSnapshot!.Answers) };
                ValidateState(pendingState);
                ValidateSnapshot(key, pendingState, pendingSnapshot);
            }
            var saved = await Post(conn =>
            {
                using var tx = conn.BeginTransaction();
                var existing = LoadFixedRaw(conn, tx, key);
                if (existing is not null)
                {
                    ValidateFixedState(LoadState(conn, tx, key) ?? throw new InvalidDataException("固定答卷缺少对应本地状态。"), existing);
                    tx.Commit();
                    return existing;
                }
                if (pendingState is not null) SaveStateCore(conn, tx, key, pendingState, pendingSnapshot);
                var state = LoadState(conn, tx, key) ?? throw new InvalidDataException("没有成功保存的状态，不能固定空答卷。");
                ValidateFixedState(state, record);
                Require(record.RequestId != LoadSnapshotRaw(conn, tx, key)!.RequestId,
                    "最终提交与进度快照不能复用同一个请求标识。");
                using var command = KeyCommand(conn, tx, key,
                    "INSERT INTO fixed_submission(server_key,exam_id,candidate_id,payload) VALUES($s,$e,$c,$payload);");
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonOptions));
                command.ExecuteNonQuery();
                tx.Commit();
                return record;
            }, ct).ConfigureAwait(false);
            return (StoreWriteResult.Ok(), saved);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return (StoreWriteResult.Fail($"固定提交保存失败：{ex.Message}"), null);
        }
    }

    public Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default) =>
        ReadAsync(key, (conn, tx) =>
        {
            var record = LoadFixedRaw(conn, tx, key);
            if (record is not null)
                ValidateFixedState(LoadState(conn, tx, key) ?? throw new InvalidDataException("固定答卷缺少对应本地状态。"), record);
            return record;
        }, ct);

    /// <summary>只给既有不可变答卷追加原回执；不允许重写答卷或替换另一份回执。</summary>
    public Task<StoreWriteResult> SaveSubmissionReceiptAsync(
        LocalStateKey key, SubmissionReceipt receipt, CancellationToken ct = default) =>
        WriteAsync(key, (conn, tx) =>
        {
            var record = LoadFixedRaw(conn, tx, key) ?? throw new InvalidDataException("没有对应的固定答卷，不能登记回执。");
            ValidateFixedState(LoadState(conn, tx, key) ?? throw new InvalidDataException("原答卷状态缺失。"), record);
            Require(SubmissionReceiptValidation.Matches(receipt, record, key.ExamId, key.CandidateId),
                "服务器回执与本地固定答卷不一致。");
            Require(record.Receipt is null || record.Receipt == receipt, "不能替换已登记的原回执。");
            if (record.Receipt is null) Put(conn, tx, key, "fixed_submission", record with { Receipt = receipt });
        }, ct);

    public Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default) =>
        WriteAsync(key, (conn, tx) =>
        {
            ValidatePreparation(record);
            var previous = ReadRecord<PreparationRecord>(conn, tx, key, "preparation");
            if (previous is not null)
            {
                ValidatePreparation(previous);
                Require(record.PaperVersion >= previous.PaperVersion && record.InstructionsVersion >= previous.InstructionsVersion,
                    "资源准备版本不能回退。");
            }
            Put(conn, tx, key, "preparation", record);
        }, ct);

    public Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default) =>
        ReadAsync(key, (conn, tx) =>
        {
            var record = ReadRecord<PreparationRecord>(conn, tx, key, "preparation");
            if (record is not null) ValidatePreparation(record);
            return record;
        }, ct);

    public Task<StoreWriteResult> SavePaperUpdateReviewAsync(
        LocalStateKey key, PaperUpdateReviewRecord record, CancellationToken ct = default) =>
        WriteAsync(key, (conn, tx) =>
        {
            Require(record.PaperVersion >= 1, "复核记录的试卷版本无效。");
            // 首次确认保持原记录：重复确认（崩溃后重放）不改变原始确认时间。
            using var command = KeyCommand(conn, tx, key, """
                INSERT INTO paper_update_review(server_key,exam_id,candidate_id,paper_version,payload)
                VALUES($s,$e,$c,$v,$payload)
                ON CONFLICT(server_key,exam_id,candidate_id,paper_version) DO NOTHING;
                """);
            command.Parameters.AddWithValue("$v", record.PaperVersion);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonOptions));
            command.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<PaperUpdateReviewRecord>> LoadPaperUpdateReviewsAsync(
        LocalStateKey key, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<PaperUpdateReviewRecord>>(key, (conn, tx) =>
        {
            using var command = KeyCommand(conn, tx, key, """
                SELECT payload FROM paper_update_review
                 WHERE server_key=$s AND exam_id=$e AND candidate_id=$c
                 ORDER BY paper_version;
                """);
            using var reader = command.ExecuteReader();
            var records = new List<PaperUpdateReviewRecord>();
            while (reader.Read())
            {
                var record = JsonSerializer.Deserialize<PaperUpdateReviewRecord>(reader.GetString(0), JsonOptions);
                if (record is null || record.PaperVersion < 1)
                    throw new InvalidDataException("试卷复核记录损坏，不能作为空记录处理。");
                records.Add(record);
            }
            return (IReadOnlyList<PaperUpdateReviewRecord>)records.AsReadOnly();
        }, ct);

    public Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default) =>
        WriteAsync(key, (conn, tx) =>
        {
            Require(LoadState(conn, tx, key) is null && LoadFixedRaw(conn, tx, key) is null,
                "已有答案或固定提交，正常客户端不能删除；请保留原始恢复资料。");
            using var command = KeyCommand(conn, tx, key,
                "DELETE FROM preparation WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;");
            command.ExecuteNonQuery();
        }, ct);

    public Task<StoreWriteResult> EnqueueUploadAsync(
        LocalStateKey key, PendingUploadRecord record, CancellationToken ct = default) =>
        WriteAsync(key, (conn, tx) =>
        {
            ArgumentNullException.ThrowIfNull(record);
            Require(!string.IsNullOrWhiteSpace(record.ItemId), "待上报记录的标识不能为空。");
            Require(!string.IsNullOrWhiteSpace(record.Kind) && !string.IsNullOrWhiteSpace(record.Payload),
                "待上报记录的类型或载荷不能为空。");
            // 与复核确认相同的首次写入语义：崩溃重放不换请求标识，服务器按 ItemId 幂等。
            using var command = KeyCommand(conn, tx, key, """
                INSERT INTO upload_outbox(server_key,exam_id,candidate_id,item_id,kind,payload)
                VALUES($s,$e,$c,$item,$kind,$payload)
                ON CONFLICT(server_key,exam_id,candidate_id,item_id) DO NOTHING;
                """);
            command.Parameters.AddWithValue("$item", record.ItemId);
            command.Parameters.AddWithValue("$kind", record.Kind);
            command.Parameters.AddWithValue("$payload", record.Payload);
            command.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<PendingUploadRecord>> LoadUploadsAsync(
        LocalStateKey key, int limit, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<PendingUploadRecord>>(key, (conn, tx) =>
        {
            Require(limit > 0, "读取待上报记录的数量必须为正数。");
            using var command = KeyCommand(conn, tx, key, """
                SELECT item_id, kind, payload FROM upload_outbox
                 WHERE server_key=$s AND exam_id=$e AND candidate_id=$c
                 ORDER BY rowid LIMIT $limit;
                """);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var records = new List<PendingUploadRecord>();
            while (reader.Read())
                records.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return (IReadOnlyList<PendingUploadRecord>)records.AsReadOnly();
        }, ct);

    public Task<StoreWriteResult> DeleteUploadAsync(
        LocalStateKey key, string itemId, CancellationToken ct = default) =>
        WriteAsync(key, (conn, tx) =>
        {
            Require(!string.IsNullOrWhiteSpace(itemId), "待删除的上报标识不能为空。");
            using var command = KeyCommand(conn, tx, key,
                "DELETE FROM upload_outbox WHERE server_key=$s AND exam_id=$e AND candidate_id=$c AND item_id=$item;");
            command.Parameters.AddWithValue("$item", itemId);
            var deleted = command.ExecuteNonQuery();
            Require(deleted == 1, "待删除的上报记录不存在或已被确认，不能重复删除。");
        }, ct);

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (!_disposed)
            {
                _disposed = true;
                _queue.CompleteAdding();
            }
        }
        // 接收的工作须完成且连接已关闭，之后才释放队列，不能留下悬挂任务。
        _worker.Join();
        // Every concurrent disposer waits for the connection, including a caller cleaning up
        // after another caller's timeout. Dispose the queue once, after the worker has exited.
        lock (_lifecycleLock)
        {
            if (!_queueDisposed) { _queueDisposed = true; _queue.Dispose(); }
        }
    }

    private async Task<T> Post<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        // AppServices 可以在 UI 线程创建存储；首次读写异步等待初始化并显示实际故障。
        await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        Task<T> pending;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_workerFailure is not null) throw _workerFailure;
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.TryAdd(new WorkItem(conn => work(conn), source, ct)))
                throw new IOException("本地保存队列已满，请等待当前保存完成后重试。");
            pending = ConvertResultAsync<T>(source.Task);
        }
        return await pending.ConfigureAwait(false);
    }

    private static async Task<T> ConvertResultAsync<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;

    private Task<T> ReadAsync<T>(LocalStateKey key, Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken ct)
    {
        ValidateKey(key);
        return Post(conn =>
        {
            using var tx = conn.BeginTransaction(deferred: true);
            var result = read(conn, tx);
            tx.Commit();
            return result;
        }, ct);
    }

    private async Task<StoreWriteResult> WriteAsync(
        LocalStateKey key, Action<SqliteConnection, SqliteTransaction> write, CancellationToken ct)
    {
        try
        {
            ValidateKey(key);
            await Post(conn =>
            {
                using var tx = conn.BeginTransaction();
                write(conn, tx);
                tx.Commit();
                return true;
            }, ct).ConfigureAwait(false);
            return StoreWriteResult.Ok();
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return StoreWriteResult.Fail($"本地保存失败：{ex.Message}");
        }
    }

    private static AnswerStateRecord? LoadState(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key)
    {
        var payload = ReadRecord<StatePayload>(conn, tx, key, "answer_state");
        if (payload is null)
        {
            Require(ReadJson(conn, tx, key, "pending_snapshot") is null &&
                ReadJson(conn, tx, key, "fixed_submission") is null,
                "本地状态缺失但仍有恢复资料，不能按空答卷进入。");
            return null;
        }
        Require(payload.Answers is not null && payload.Marks is not null &&
            payload.Marks.Distinct(StringComparer.Ordinal).Count() == payload.Marks.Length,
            "本地答案或私人标记记录损坏。");
        var state = new AnswerStateRecord(payload.ParticipationId, payload.EntryVersion, payload.AuthorizationVersion,
            payload.PaperVersion, payload.AnswerVersion, payload.CurrentQuestionId, payload.UpdatedAt,
            payload.Answers!, new HashSet<string>(payload.Marks!, StringComparer.Ordinal), payload.DeviceId);
        ValidateState(state);
        ValidateSnapshot(key, state, LoadSnapshotRaw(conn, tx, key) ??
            throw new InvalidDataException("本地答案缺少同事务待发送快照。"));
        return state;
    }

    private static PendingSnapshot? LoadSnapshotRaw(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key) =>
        ReadRecord<PendingSnapshot>(conn, tx, key, "pending_snapshot");

    private static FixedSubmissionRecord? LoadFixedRaw(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key)
    {
        var record = ReadRecord<FixedSubmissionRecord>(conn, tx, key, "fixed_submission");
        if (record is not null)
        {
            ValidateFixed(record);
            Require(record.Receipt is null || SubmissionReceiptValidation.Matches(record.Receipt, record, key.ExamId, key.CandidateId),
                "本地回执与原答卷不一致，不能作为交卷成功依据。");
        }
        return record;
    }

    private static T? ReadRecord<T>(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key, string table) where T : class
    {
        var json = ReadJson(conn, tx, key, table);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions) ??
            throw new InvalidDataException("本地恢复记录损坏，不能作为空状态处理。");
    }

    private static string? ReadJson(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key, string table)
    {
        using var command = KeyCommand(conn, tx, key,
            $"SELECT payload FROM {table} WHERE server_key=$s AND exam_id=$e AND candidate_id=$c;");
        return command.ExecuteScalar() switch
        {
            null => null,
            string json => json,
            _ => throw new InvalidDataException("本地恢复载荷类型无效，不能当作空状态处理。"),
        };
    }

    private static void Put<T>(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key, string table, T record)
    {
        using var command = KeyCommand(conn, tx, key, $"""
            INSERT INTO {table}(server_key,exam_id,candidate_id,payload) VALUES($s,$e,$c,$payload)
            ON CONFLICT(server_key,exam_id,candidate_id) DO UPDATE SET payload=$payload;
            """);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, JsonOptions));
        command.ExecuteNonQuery();
    }

    private static SqliteCommand KeyCommand(SqliteConnection conn, SqliteTransaction tx, LocalStateKey key, string sql)
    {
        var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$s", key.ServerKey);
        command.Parameters.AddWithValue("$e", key.ExamId);
        command.Parameters.AddWithValue("$c", key.CandidateId);
        return command;
    }

    private static void ValidateKey(LocalStateKey key) => Require(key is not null &&
        !string.IsNullOrWhiteSpace(key.ServerKey) && !string.IsNullOrWhiteSpace(key.ExamId) &&
        !string.IsNullOrWhiteSpace(key.CandidateId), "本地状态隔离标识不完整。");

    private static void ValidateState(AnswerStateRecord state)
    {
        ValidateAuthorization(state.ParticipationId, state.EntryVersion, state.DeviceId, state.AuthorizationVersion);
        Require(state.PaperVersion > 0 && state.AnswerVersion > 0, "本地答案和题包版本必须为正数。");
        Require(state.CurrentQuestionId is null || !string.IsNullOrWhiteSpace(state.CurrentQuestionId), "当前题编号无效。");
        ValidateAnswers(state.Answers);
        Require(state.Marks is not null && state.Marks.All(mark => !string.IsNullOrWhiteSpace(mark)), "私人标记无效。");
    }

    private static void ValidateSnapshot(LocalStateKey key, AnswerStateRecord state, PendingSnapshot snapshot)
    {
        Require(!string.IsNullOrWhiteSpace(snapshot.RequestId), "待发送快照必须保留稳定请求标识。");
        Require(snapshot.ExamId == key.ExamId && snapshot.CandidateId == key.CandidateId &&
            snapshot.ParticipationId == state.ParticipationId && snapshot.EntryVersion == state.EntryVersion &&
            snapshot.DeviceId == state.DeviceId && snapshot.AuthorizationVersion == state.AuthorizationVersion &&
            snapshot.PaperVersion == state.PaperVersion && snapshot.AnswerVersion == state.AnswerVersion &&
            snapshot.CurrentQuestionId == state.CurrentQuestionId, "本地状态与快照的身份、授权或版本不一致。");
        ValidateAnswers(snapshot.Answers);
        Require(SameAnswers(state.Answers, snapshot.Answers), "本地状态与待发送快照的答案不一致。");
    }

    private static void ValidateFixed(FixedSubmissionRecord record)
    {
        Require(!string.IsNullOrWhiteSpace(record.SubmissionId) && !string.IsNullOrWhiteSpace(record.RequestId),
            "固定提交的提交标识或请求标识缺失。");
        ValidateAuthorization(record.ParticipationId, record.EntryVersion, record.DeviceId, record.AuthorizationVersion);
        Require(record.PaperVersion > 0 && record.AnswerVersion > 0, "固定提交版本无效。");
        ValidateAnswers(record.FrozenAnswers);
    }

    private static void ValidateFixedState(AnswerStateRecord state, FixedSubmissionRecord record) => Require(
        state.ParticipationId == record.ParticipationId && state.EntryVersion == record.EntryVersion &&
        state.DeviceId == record.DeviceId && state.AuthorizationVersion == record.AuthorizationVersion &&
        state.PaperVersion == record.PaperVersion && state.AnswerVersion == record.AnswerVersion &&
        SameAnswers(state.Answers, record.FrozenAnswers), "固定提交必须使用最后成功保存的原授权、版本及完整答案。");

    private static void ValidatePreparation(PreparationRecord record) => Require(record.PaperVersion > 0 &&
        record.InstructionsVersion > 0 && !string.IsNullOrWhiteSpace(record.PackageFile) &&
        !string.IsNullOrWhiteSpace(record.Phase), "资源准备记录无效。");

    private static void ValidateAuthorization(string? participation, long entry, string? device, long authorization) =>
        Require(!string.IsNullOrWhiteSpace(participation) && entry > 0 &&
            !string.IsNullOrWhiteSpace(device) && authorization > 0, "本地答题状态缺少完整授权元组。");

    private static void RequireSameAuthorization(AnswerStateRecord first, AnswerStateRecord next) => Require(
        first.ParticipationId == next.ParticipationId && first.EntryVersion == next.EntryVersion &&
        first.DeviceId == next.DeviceId && first.AuthorizationVersion == next.AuthorizationVersion,
        "不能用另一次参试或设备授权覆盖已有答案，请先核对服务端权限并保留原记录。");

    private static void ValidateAnswers(IReadOnlyDictionary<string, AnswerValue> answers)
    {
        Require(answers is not null, "答案集合缺失。");
        foreach (var (question, answer) in answers!)
        {
            Require(!string.IsNullOrWhiteSpace(question) && answer is not null &&
                Enum.IsDefined(answer.Kind) && answer.OptionIds is not null, "答案项损坏。");
            var options = answer!.OptionIds!;
            Require(options.All(option => !string.IsNullOrWhiteSpace(option)) &&
                options.Distinct(StringComparer.Ordinal).Count() == options.Count &&
                (answer.Kind == AnswerKind.Selected ? options.Count > 0 : options.Count == 0),
                "答案状态与选项不一致。");
        }
    }

    private static AnswerStateRecord CloneState(AnswerStateRecord state) => state with
    {
        Answers = CloneAnswers(state.Answers),
        Marks = state.Marks is null ? throw new InvalidDataException("私人标记集合缺失。") :
            new HashSet<string>(state.Marks, StringComparer.Ordinal),
    };

    private static IReadOnlyDictionary<string, AnswerValue> CloneAnswers(IReadOnlyDictionary<string, AnswerValue> answers)
    {
        ValidateAnswers(answers);
        return answers.ToDictionary(pair => pair.Key,
            pair => pair.Value with { OptionIds = pair.Value.OptionIds.ToArray() }, StringComparer.Ordinal);
    }

    private static bool SameAnswers(IReadOnlyDictionary<string, AnswerValue> left, IReadOnlyDictionary<string, AnswerValue> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            value.Kind == pair.Value.Kind && value.OptionIds.SequenceEqual(pair.Value.OptionIds, StringComparer.Ordinal));

    private static bool SameState(AnswerStateRecord left, AnswerStateRecord right) =>
        left.ParticipationId == right.ParticipationId && left.EntryVersion == right.EntryVersion &&
        left.DeviceId == right.DeviceId && left.AuthorizationVersion == right.AuthorizationVersion &&
        left.PaperVersion == right.PaperVersion && left.AnswerVersion == right.AnswerVersion &&
        left.CurrentQuestionId == right.CurrentQuestionId && left.UpdatedAt == right.UpdatedAt &&
        left.Marks.SetEquals(right.Marks) && SameAnswers(left.Answers, right.Answers);

    private static bool SameSnapshot(PendingSnapshot left, PendingSnapshot right) =>
        left.RequestId == right.RequestId && left.ExamId == right.ExamId && left.CandidateId == right.CandidateId &&
        left.ParticipationId == right.ParticipationId && left.EntryVersion == right.EntryVersion &&
        left.DeviceId == right.DeviceId && left.AuthorizationVersion == right.AuthorizationVersion &&
        left.PaperVersion == right.PaperVersion && left.AnswerVersion == right.AnswerVersion &&
        left.CurrentQuestionId == right.CurrentQuestionId && left.SavedAt == right.SavedAt &&
        SameAnswers(left.Answers, right.Answers);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static bool IsStoreError(Exception ex) => ex is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or ArgumentException or InvalidOperationException or FormatException;
}
