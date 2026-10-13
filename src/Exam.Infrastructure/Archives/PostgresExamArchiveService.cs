using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Application.Participation;
using Exam.Contracts.Archives;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Participation;
using Exam.Domain.Exams;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Archives;

/// <summary>Consistent, read-only exports and transactional single-exam imports.</summary>
public sealed class PostgresExamArchiveService(NpgsqlDataSource dataSource, PortableExamResources resources, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<ExamArchiveDocument> ExportAsync(string examId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", ct);
        var tables = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var table in ExamArchiveTables.All)
        {
            var expression = table.Name == "paper_materials" ? "to_jsonb(t)-'archive'" : "to_jsonb(t)";
            var extra = table.Name == "paper_materials" ? ", t.archive" : "";
            await using var command = Command(connection, transaction,
                $"SELECT ({expression})::text{extra} FROM exam.{table.Name} t WHERE {table.Predicate}", ("exam", examId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new JsonArray();
            while (await reader.ReadAsync(ct))
            {
                var row = JsonNode.Parse(reader.GetString(0))!.AsObject();
                if (table.Name == "paper_materials")
                {
                    var path = $"content/materials-{rows.Count}.zip";
                    contents.Add(path, reader.GetFieldValue<byte[]>(1)); row["archive_entry"] = path;
                }
                if (table.Name == "student_sessions")
                {
                    var candidate = row["candidate_ids_by_exam"]?[examId]?.DeepClone()
                        ?? throw new InvalidDataException("学生会话缺少本场考试身份。");
                    row["exam_ids"] = new JsonArray(examId);
                    row["candidate_ids_by_exam"] = new JsonObject { [examId] = candidate };
                }
                rows.Add(row);
            }
            tables.Add(table.Name, rows);
        }
        var catalog = tables["exam_catalog"].SingleOrDefault()?.AsObject()
            ?? throw new ParticipationRuleException(ParticipationErrorCodes.ExamNotFound, "未找到可归档的考试。");
        var deviceIds = DeviceIds(tables).Distinct(StringComparer.Ordinal).ToArray();
        var devices = new JsonArray();
        await using (var command = Command(connection, transaction,
            "SELECT (to_jsonb(t)-'protected_credential'-'room_group_id')::text FROM exam.activation_devices t WHERE device_id=ANY(@ids)", ("ids", deviceIds)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) devices.Add(JsonNode.Parse(reader.GetString(0)));
        tables.Add("activation_devices", devices);
        // Session identifiers are historical links here, never transferable source login tokens.
        var sessionAliases = tables["student_sessions"].ToDictionary(row => row!["session_id"]!.GetValue<string>(),
            _ => "archive-session-" + Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        foreach (var rows in tables.Values) ReplaceSessionIdentifiers(rows, sessionAliases);
        var archivedResources = new List<ArchiveResource>();
        await using (var command = Command(connection, transaction, """
            SELECT resource_id,resource_kind,resource_version,encrypted_content,encryption_envelope::text,created_at,created_by
            FROM exam.versioned_resources WHERE exam_id=@exam ORDER BY resource_kind,resource_version
            """, ("exam", examId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var resource = new VersionedEncryptedResource(reader.GetString(0), examId, reader.GetString(1), reader.GetInt64(2),
                    reader.GetFieldValue<byte[]>(3), JsonSerializer.Deserialize<EncryptedPackageEnvelope>(reader.GetString(4), Json)!,
                    reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6));
                var portable = await resources.ExportAsync(resource, ct);
                var path = $"content/resource-{archivedResources.Count}.zip";
                contents.Add(path, portable.Content);
                archivedResources.Add(new(resource.ResourceId, resource.ResourceKind, resource.Version, resource.CreatedAt, resource.CreatedBy, path));
            }
        }
        string origin;
        await using (var command = Command(connection, transaction, "SELECT origin_exam_id FROM exam.exam_archive_origins WHERE exam_id=@exam", ("exam", examId)))
            origin = await command.ExecuteScalarAsync(ct) as string ?? examId;
        var document = new ExamArchiveDocument(new(ExamArchiveFormat.Version, Guid.NewGuid().ToString("N"), examId, origin,
            catalog["exam_name"]!.GetValue<string>(), time.GetUtcNow(), tables, archivedResources), contents);
        Validate(document);
        await transaction.CommitAsync(ct);
        return document;
    }

    public async Task<ExamArchivePreview> PreviewAsync(ExamArchiveDocument document, CancellationToken ct = default)
    {
        Validate(document);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return new(ExamArchiveFormat.Version, document.Manifest.SourceExamId, document.Manifest.ExamName,
            document.Manifest.ExportedAt, await ConflictsAsync(connection, null, document.Manifest.OriginExamId, ct));
    }

    public async Task<ExamArchiveImportResult> ImportAsync(ExamArchiveDocument document, ExamArchiveImportRequest request, string actor, CancellationToken ct = default)
    {
        Validate(document);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 || string.IsNullOrWhiteSpace(actor) ||
            request.Mode is not ("copy" or "overwrite" or "skip")) throw new InvalidDataException("考试归档导入请求无效。");
        if (request.Mode == "skip") return new(document.Manifest.SourceExamId, document.Manifest.ExamName, false, true);
        var sourceId = document.Manifest.SourceExamId;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", ct, ("key", "exam-archive:" + request.RequestId));
        var replay = await ReplayAsync(connection, transaction, document, request, actor, ct);
        if (replay is not null) { await transaction.CommitAsync(ct); return replay; }
        var conflicts = await ConflictsAsync(connection, transaction, document.Manifest.OriginExamId, ct);
        var targetId = "exam-" + Guid.NewGuid().ToString("N");
        if (request.Mode == "overwrite")
        {
            var target = conflicts.SingleOrDefault(value => value.ExamId == request.ExpectedTargetExamId);
            if (target is null || request.ExpectedTargetVersion != target.Version) throw Conflict("目标考试已变化，请重新预览后选择覆盖。");
            targetId = target.ExamId;
            // Existing in-flight participant writes must finish before the target rows are replaced.
            await ExecuteAsync(connection, transaction, "SELECT exam_id FROM exam.version_streams WHERE exam_id=@exam FOR UPDATE", ct, ("exam", targetId));
            await ExecuteAsync(connection, transaction, "SELECT exam_id FROM exam.exam_catalog WHERE exam_id=@exam FOR UPDATE", ct, ("exam", targetId));
            await ExecuteAsync(connection, transaction, "SELECT exam_id FROM exam.exam_entry_access WHERE exam_id=@exam FOR UPDATE", ct, ("exam", targetId));
            await ExecuteAsync(connection, transaction, "SELECT participation_id FROM exam.participations WHERE exam_id=@exam ORDER BY participation_id FOR UPDATE", ct, ("exam", targetId));
        }
        var name = await UniqueNameAsync(connection, transaction, document.Manifest.ExamName, targetId, ct);
        var identity = new ExamArchiveIdentityMap(sourceId, targetId, name);
        var tables = document.Manifest.Tables.ToDictionary(pair => pair.Key, pair => (JsonArray)pair.Value.DeepClone(), StringComparer.Ordinal);
        foreach (var node in tables["activation_devices"])
        {
            var deviceId = node!["device_id"]!.GetValue<string>();
            if (!tables["exam_archive_device_history"].Any(history => history!["device_id"]!.GetValue<string>() == deviceId))
                tables["exam_archive_device_history"].Add(new JsonObject
                {
                    ["exam_id"] = sourceId, ["device_id"] = deviceId, ["source_device_id"] = deviceId,
                    ["source_client_number"] = node["client_number"]!.GetValue<string>()
                });
        }
        foreach (var node in tables["participation_device_conflicts"])
        {
            var history = node!.DeepClone().AsObject(); history["exam_id"] = sourceId;
            tables["exam_archive_pending_device_conflicts"].Add(history);
        }
        tables["participation_device_conflicts"].Clear();
        var reboundResources = new List<ReboundExamResource>();
        foreach (var resource in document.Manifest.Resources)
        {
            var portable = new PortableExamResource(resource.ResourceId, sourceId, resource.ResourceKind, resource.Version,
                document.Contents[resource.ContentEntry], resource.CreatedAt, resource.CreatedBy);
            reboundResources.Add(await resources.RebindAsync(portable, targetId, name, identity.Id(resource.ResourceId), ct));
        }
        foreach (var node in tables["paper_materials"])
        {
            var row = node!.AsObject();
            var original = ReadMaterials(row, document);
            var package = reboundResources.SingleOrDefault(value => value.Resource.ResourceKind == "paper-package" && value.Resource.Version == original.PaperVersion)
                ?? throw new InvalidDataException("源材料缺少对应的试卷资源。");
            var rebound = PortableExamResources.RebindMaterials(original, targetId, name, package.StudentPackageSha256);
            identity.MaterialHashes[original.ArchiveSha256] = rebound.ArchiveSha256;
            row.Remove("archive_entry"); row["archive"] = "\\x" + Convert.ToHexString(rebound.Archive);
            row["archive_sha256"] = rebound.ArchiveSha256; row["package_content_sha256"] = rebound.PackageContentSha256;
            row["archive_bytes"] = rebound.Archive.Length; row["file_count"] = rebound.FileCount;
            row["question_digests"] = JsonSerializer.SerializeToNode(rebound.QuestionDigests, Json);
        }
        foreach (var table in tables.Values) identity.Rewrite(table);
        foreach (var node in tables["student_sessions"])
        {
            var row = node!.AsObject();
            var candidate = row["candidate_ids_by_exam"]![sourceId]!.DeepClone();
            row["exam_ids"] = new JsonArray(targetId); row["candidate_ids_by_exam"] = new JsonObject { [targetId] = candidate };
        }
        foreach (var node in tables["exam_catalog"])
        {
            var row = node!.AsObject(); row["exam_name"] = name; row["name_key"] = ExamSettingsRules.NameKey(name);
            row["version"] = row["version"]!.GetValue<long>() + 1;
        }
        foreach (var node in tables["student_exam_metadata"]) node!["display_name"] = name;
        foreach (var node in tables["grading_outbox"]) { node!["claimed_at"] = null; node["lease_expires_at"] = null; }
        foreach (var node in tables["exam_entry_access"])
        {
            var current = reboundResources.SingleOrDefault(item => item.Resource.ResourceKind == "paper-package" && item.Resource.Version == node!["paper_version"]!.GetValue<long>());
            if (current is not null) { node!["encrypted_package_uri"] = "/api/v1/resources/" + Uri.EscapeDataString(current.Resource.ResourceId); node["package_length"] = current.Resource.EncryptedContent.LongLength; node["unlock_reference"] = "resource-envelope:" + current.Resource.ResourceId; }
        }
        if (request.Mode == "overwrite") await DeleteExamAsync(connection, transaction, targetId, ct);
        // Historical devices cannot authenticate to either system. Only identity/audit relationships survive.
        foreach (var node in tables["activation_devices"])
        {
            var row = node!.AsObject(); row["activation_status"] = "revoked"; row["protected_credential"] = "archive-history-no-credential";
            row["room_group_id"] = null; row["last_seen_at"] = null;
            row["client_number"] = await HistoricalClientNumberAsync(connection, transaction, row["client_number"]!.GetValue<string>(), ct);
            await InsertRowsAsync(connection, transaction, "activation_devices", new JsonArray(row.DeepClone()), ct);
        }
        foreach (var table in ExamArchiveTables.All)
        {
            if (table.Name is "actual_roster_versions" or "collection_pending_checks")
                await ExecuteAsync(connection, transaction, $"DELETE FROM exam.{table.Name} WHERE exam_id=@exam", ct, ("exam", targetId));
            await InsertRowsAsync(connection, transaction, table.Name, tables[table.Name], ct);
        }
        foreach (var resource in reboundResources) PostgresVersionedResourceStore.SaveInTransaction(connection, transaction, resource.Resource);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.exam_archive_origins(exam_id,origin_exam_id,archive_id,imported_at,imported_by)
            VALUES(@exam,@origin,@archive,@now,@actor)
            """, ct, ("exam", targetId), ("origin", document.Manifest.OriginExamId), ("archive", document.Manifest.ArchiveId), ("now", time.GetUtcNow()), ("actor", actor));
        var result = new ExamArchiveImportResult(targetId, name, true, false);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO exam.exam_archive_imports(request_id,archive_id,mode,target_exam_id,actor_id,result,imported_at)
            VALUES(@request,@archive,@mode,@target,@actor,@result::jsonb,@now)
            """, ct, ("request", request.RequestId), ("archive", document.Manifest.ArchiveId), ("mode", request.Mode),
            ("target", request.ExpectedTargetExamId ?? ""), ("actor", actor), ("result", JsonSerializer.Serialize(result, Json)), ("now", time.GetUtcNow()));
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<IReadOnlyList<ExamArchiveConflict>> ConflictsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string origin, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT c.exam_id,c.exam_name,c.version+COALESCE(v.stream_revision,0)
            FROM exam.exam_catalog c LEFT JOIN exam.exam_archive_origins o USING(exam_id)
            LEFT JOIN exam.version_streams v USING(exam_id)
            WHERE COALESCE(o.origin_exam_id,c.exam_id)=@origin ORDER BY c.created_at,c.exam_id
            """, ("origin", origin));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ExamArchiveConflict>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    private static async Task<ExamArchiveImportResult?> ReplayAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ExamArchiveDocument document,
        ExamArchiveImportRequest request, string actor, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT archive_id,mode,target_exam_id,actor_id,result::text FROM exam.exam_archive_imports WHERE request_id=@request", ("request", request.RequestId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetString(0) != document.Manifest.ArchiveId || reader.GetString(1) != request.Mode ||
            reader.GetString(2) != (request.ExpectedTargetExamId ?? "") || reader.GetString(3) != actor)
            throw Conflict("该导入请求标识已用于其他归档或导入选择。");
        return JsonSerializer.Deserialize<ExamArchiveImportResult>(reader.GetString(4), Json)!;
    }

    private static async Task DeleteExamAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string examId, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM exam.exam_archive_origins WHERE exam_id=@exam; DELETE FROM exam.exam_catalog_hiding WHERE exam_id=@exam; DELETE FROM exam.versioned_resources WHERE exam_id=@exam; DELETE FROM exam.device_presence WHERE exam_id=@exam", ct, ("exam", examId));
        foreach (var table in ExamArchiveTables.All.Reverse())
        {
            if (table.Name is "student_sessions" or "actual_roster_versions" or "collection_pending_checks") continue;
            await ExecuteAsync(connection, transaction, $"DELETE FROM exam.{table.Name} t WHERE {table.Predicate}", ct, ("exam", examId));
        }
        // A login can contain multiple exams. Remove only this exam from those sessions.
        await ExecuteAsync(connection, transaction, """
            DELETE FROM exam.student_sessions WHERE exam_ids=ARRAY[@exam]::text[];
            UPDATE exam.student_sessions SET exam_ids=array_remove(exam_ids,@exam), candidate_ids_by_exam=candidate_ids_by_exam-@exam WHERE @exam=ANY(exam_ids);
            DELETE FROM exam.actual_roster_versions WHERE exam_id=@exam;
            DELETE FROM exam.collection_pending_checks WHERE exam_id=@exam;
            """, ct, ("exam", examId));
    }

    private static async Task<string> UniqueNameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string original, string targetId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10000; attempt++)
        {
            var suffix = attempt == 0 ? "" : $"（导入副本 {attempt + 1}）";
            var name = original[..Math.Min(original.Length, 128 - suffix.Length)] + suffix;
            await using var command = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM exam.exam_catalog WHERE name_key=@name AND exam_id<>@exam)", ("name", ExamSettingsRules.NameKey(name)), ("exam", targetId));
            if (await command.ExecuteScalarAsync(ct) is false) return name;
        }
        throw Conflict("相同考试名称过多，请先调整已有考试名称。");
    }

    private static async Task<string> HistoricalClientNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string original, CancellationToken ct)
    {
        var number = original;
        while (true)
        {
            await using var command = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM exam.activation_devices WHERE client_number=@number)", ("number", number));
            if (await command.ExecuteScalarAsync(ct) is false) return number;
            number = (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()) % 1_000_000_000_000UL).ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static PaperMaterialsRow ReadMaterials(JsonObject row, ExamArchiveDocument document) => new(
        row["exam_id"]!.GetValue<string>(), row["paper_version"]!.GetValue<long>(), document.Contents[row["archive_entry"]!.GetValue<string>()],
        row["archive_sha256"]!.GetValue<string>(), row["package_content_sha256"]!.GetValue<string>(), row["format_version"]!.GetValue<int>(),
        row["file_count"]!.GetValue<int>(), row["question_digests"]!.Deserialize<Dictionary<string, string>>(Json)!,
        row["created_at"]!.GetValue<DateTimeOffset>(), row["created_by"]!.GetValue<string>());

    private static IEnumerable<string> DeviceIds(Dictionary<string, JsonArray> tables) => tables.Values.SelectMany(rows => rows)
        .OfType<JsonObject>().SelectMany(row => row.Where(item => item.Key != "source_device_id" && item.Key.EndsWith("device_id", StringComparison.Ordinal)))
        .Select(item => item.Value?.GetValue<string>()).OfType<string>();

    private static void ReplaceSessionIdentifiers(JsonNode? node, IReadOnlyDictionary<string, string> aliases)
    {
        if (node is JsonArray array) { foreach (var child in array) ReplaceSessionIdentifiers(child, aliases); return; }
        if (node is not JsonObject obj) return;
        foreach (var (key, value) in obj.ToArray())
        {
            if (key.Replace("_", "", StringComparison.Ordinal).Equals("SessionId", StringComparison.OrdinalIgnoreCase) && value is JsonValue scalar && scalar.TryGetValue<string>(out var session) && aliases.TryGetValue(session, out var alias)) obj[key] = alias;
            else if (key is not ("answers" or "frozen_answers" or "settings")) ReplaceSessionIdentifiers(value, aliases);
        }
    }

    private static void Validate(ExamArchiveDocument document)
    {
        try { ValidateCore(document); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or NullReferenceException)
        { throw new InvalidDataException("考试归档中的记录格式无效。", error); }
    }

    private static void ValidateCore(ExamArchiveDocument document)
    {
        var manifest = document.Manifest;
        if (manifest.FormatVersion != ExamArchiveFormat.Version || manifest.Tables.Count != ExamArchiveTables.Names.Count ||
            manifest.Tables.Keys.Any(key => !ExamArchiveTables.Names.Contains(key)) ||
            manifest.Tables["exam_catalog"].Count != 1 || manifest.Tables["exam_entry_access"].Count != 1)
            throw new InvalidDataException("考试归档不完整或包含不支持的数据表。");
        var catalog = manifest.Tables["exam_catalog"][0]!;
        if (catalog["exam_name"]!.GetValue<string>() != manifest.ExamName ||
            ExamSettingsRules.Validate(manifest.ExamName, catalog["settings"]!.Deserialize<ExamSettings>(Json)).Count > 0)
            throw new InvalidDataException("考试归档中的名称或考试设置无效。");
        foreach (var (table, rows) in manifest.Tables)
        foreach (var node in rows)
        {
            if (node is not JsonObject row) throw new InvalidDataException("考试归档记录无效。");
            if (row.TryGetPropertyValue("exam_id", out var exam) && exam?.GetValue<string>() != manifest.SourceExamId)
                throw new InvalidDataException("考试归档混入其他考试的数据。");
            if (table == "activation_devices" && row.ContainsKey("protected_credential")) throw new InvalidDataException("考试归档不能包含登录凭据。");
            if (table == "student_sessions" && (row["exam_ids"] is not JsonArray exams || exams.Count != 1 || exams[0]?.GetValue<string>() != manifest.SourceExamId ||
                row["candidate_ids_by_exam"] is not JsonObject candidates || candidates.Count != 1 || !candidates.ContainsKey(manifest.SourceExamId)))
                throw new InvalidDataException("考试归档包含其他考试的登录会话。");
        }
        foreach (var resource in manifest.Resources)
            if (resource.Version < 1 || resource.ResourceKind != "paper-package" || string.IsNullOrWhiteSpace(resource.ResourceId) || !document.Contents.ContainsKey(resource.ContentEntry))
                throw new InvalidDataException("考试归档缺少试题资源。");
        if (manifest.Resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != manifest.Resources.Count ||
            manifest.Resources.Select(resource => (resource.ResourceKind, resource.Version)).Distinct().Count() != manifest.Resources.Count)
            throw new InvalidDataException("考试归档包含重复的试题资源或试卷版本。");
        var published = manifest.Resources.ToDictionary(resource => resource.ResourceId, StringComparer.Ordinal);
        foreach (var row in manifest.Tables["version_events"])
        {
            if (row!["operation_type"]!.GetValue<string>() != "paper.publish") continue;
            var payload = row["payload"]!;
            var id = payload["artifactId"]!.GetValue<string>();
            if (!published.TryGetValue(id, out var resource) || resource.Version != payload["expectedVersion"]!.GetValue<long>() + 1)
                throw new InvalidDataException("考试归档缺少历史试卷版本引用的资源。");
        }
        var access = manifest.Tables["exam_entry_access"][0]!;
        if (access["package_ready"]!.GetValue<bool>() && !manifest.Resources.Any(resource => resource.Version == access["paper_version"]!.GetValue<long>()))
            throw new InvalidDataException("考试归档缺少当前试卷资源。");
        foreach (var row in manifest.Tables["paper_materials"])
            if (!document.Contents.ContainsKey(row!["archive_entry"]!.GetValue<string>())) throw new InvalidDataException("考试归档缺少原始题目材料。");
        var devices = manifest.Tables["activation_devices"].Select(row => row!["device_id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (DeviceIds(manifest.Tables).Any(device => !devices.Contains(device))) throw new InvalidDataException("考试归档缺少设备历史记录。");
        var participants = manifest.Tables["participations"].Select(row => row!["participation_id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var row in manifest.Tables.Values.SelectMany(rows => rows).OfType<JsonObject>())
            if (row["participation_id"] is JsonValue id && !participants.Contains(id.GetValue<string>()))
                throw new InvalidDataException("考试归档的参加记录引用无效。");
    }

    private static async Task InsertRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, JsonArray rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        if (!ExamArchiveTables.Names.Contains(table)) throw new InvalidOperationException("Unsupported archive table.");
        await ExecuteAsync(connection, transaction, $"INSERT INTO exam.{table} SELECT * FROM jsonb_populate_recordset(NULL::exam.{table},@rows::jsonb)", ct, ("rows", rows.ToJsonString(Json)));
    }
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return command;
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] values)
    {
        await using var command = Command(connection, transaction, sql, values); await command.ExecuteNonQueryAsync(ct);
    }
    private static ParticipationRuleException Conflict(string message) => new(ParticipationErrorCodes.VersionConflict, message);
}
