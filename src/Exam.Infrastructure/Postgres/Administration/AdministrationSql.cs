using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Administration;

internal static class AdministrationSql
{
    internal static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    internal static ParticipationRuleException Invalid(string message)=>new(ParticipationErrorCodes.InvalidRequest,message);
    internal static ParticipationRuleException Conflict(string message)=>new(ParticipationErrorCodes.VersionConflict,message);
    internal static void Require(string? text,string label,int max=256)
    { if(string.IsNullOrWhiteSpace(text)||text.Length>max||text.Any(char.IsControl))throw Invalid(label+"不能为空、超长或包含控制字符。"); }
    internal static NpgsqlCommand Command(NpgsqlConnection connection,NpgsqlTransaction? transaction,string sql,params object?[] values)
    {
        var command=new NpgsqlCommand(sql,connection,transaction);
        foreach(var value in values)
            command.Parameters.Add(value is null
                ? new NpgsqlParameter {NpgsqlDbType=NpgsqlDbType.Text,Value=DBNull.Value}
                : new NpgsqlParameter {Value=value});
        return command;
    }
    internal static async Task LockAsync(NpgsqlConnection c,NpgsqlTransaction t,string key,CancellationToken ct)
    { await using var command=Command(c,t,"SELECT pg_advisory_xact_lock(hashtextextended($1,0));",key); await command.ExecuteNonQueryAsync(ct); }
    internal static async Task<T> MutateAsync<T>(NpgsqlDataSource source,string operation,string scope,
        string requestId,string actor,object payload,DateTimeOffset now,
        Func<NpgsqlConnection,NpgsqlTransaction,CancellationToken,Task<T>> action,CancellationToken ct)
    {
        Require(requestId,"请求标识",128);Require(actor,"操作者",256);Require(scope,"操作范围",512);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{operation,scope,actor,payload},Json))));
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        await LockAsync(connection,transaction,"administration-request:"+requestId,ct);
        await using(var existing=Command(connection,transaction,"SELECT payload_hash,response_json::text FROM exam.administration_requests WHERE request_id=$1;",requestId))
        {
            await using var reader=await existing.ExecuteReaderAsync(ct);
            if(await reader.ReadAsync(ct))
            {
                if(reader.GetString(0)!=hash)throw Conflict("请求标识已用于其他内容；未重复执行操作。");
                return JsonSerializer.Deserialize<T>(reader.GetString(1),Json) ?? throw new InvalidDataException("持久化操作回执无法读取。");
            }
        }
        await LockAsync(connection,transaction,"administration-scope:"+scope,ct);
        try
        {
            var result=await action(connection,transaction,ct);
            await using var save=Command(connection,transaction,"""
                INSERT INTO exam.administration_requests(request_id,operation,scope,payload_hash,response_json,actor_id,created_at)
                VALUES($1,$2,$3,$4,$5::jsonb,$6,$7);
                """,requestId,operation,scope,hash,JsonSerializer.Serialize(result,Json),actor,now.ToUniversalTime());
            await save.ExecuteNonQueryAsync(ct);await transaction.CommitAsync(ct);return result;
        }
        catch(PostgresException e) when(e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
        { throw Conflict("名称、编号或关联记录已发生变化，未完成本次写入；请刷新后重试。"); }
    }
    internal static async Task RequireExamAsync(NpgsqlConnection c,NpgsqlTransaction? t,string examId,CancellationToken ct,bool lockRow=false)
    {
        Require(examId,"考试标识");
        await using var command=Command(c,t,"SELECT exam_id FROM exam.exam_entry_access WHERE exam_id=$1"+(lockRow?" FOR SHARE;":";"),examId);
        if(await command.ExecuteScalarAsync(ct) is null)throw new ParticipationRuleException(ParticipationErrorCodes.ExamNotFound,"当前考试不存在，请刷新考试列表。");
    }
}
