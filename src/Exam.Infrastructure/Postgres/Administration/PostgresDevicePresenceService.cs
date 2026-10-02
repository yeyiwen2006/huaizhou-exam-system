using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Administration;
using Exam.Contracts.Participation;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Administration;

public sealed class PostgresDevicePresenceService(NpgsqlDataSource source,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider _time=timeProvider??TimeProvider.System;
    private static readonly HashSet<string> Pages = ["登录","选择考试","等待考试","正在答题","交卷确认","回收中","交卷反馈","已退出"];
    private static readonly HashSet<string> Downloads = ["未下载","下载中","已下载","更新中","已更新","失败"];
    public Task<DevicePresenceLease> OpenAsync(string deviceId,OpenDevicePresenceRequest request,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);AdministrationSql.Require(request.ConnectionId,"连接标识",128);
        var now=_time.GetUtcNow();
        return AdministrationSql.MutateAsync(source,"presence.open","presence:"+deviceId,request.RequestId,deviceId,request,now,async(c,t,token)=>
        {
            await RequireActiveAsync(c,t,deviceId,token);
            await using var command=AdministrationSql.Command(c,t,"""
                INSERT INTO exam.device_presence(device_id,connection_id,generation,sequence,page,last_seen_at,updated_at)
                 VALUES($1,$2,1,0,'登录',$3,$3)
                ON CONFLICT(device_id) DO UPDATE SET connection_id=$2,generation=exam.device_presence.generation+1,
                 sequence=0,payload_hash=NULL,session_id=NULL,exam_id=NULL,page='登录',current_question_id=NULL,
                 installed_paper_version=0,download_state='未下载',recovering=false,last_seen_at=$3,
                 disconnected_at=NULL,hub_connection_id=NULL,updated_at=$3 RETURNING generation;
                """,deviceId,request.ConnectionId,now);
            return new DevicePresenceLease(request.ConnectionId,(long)(await command.ExecuteScalarAsync(token))!,now);
        },ct);
    }
    public async Task<DevicePresenceReceipt> UpdateAsync(string deviceId,UpdateDevicePresenceRequest request,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);AdministrationSql.Require(request.ConnectionId,"连接标识",128);
        if(request.Generation<1||request.Sequence<1||request.InstalledPaperVersion<0||
           !Pages.Contains(request.Page)||!Downloads.Contains(request.DownloadState)||
           request.CurrentQuestionId?.Length>256||request.ExamId?.Length>256||request.SessionId?.Length>256)
            throw AdministrationSql.Invalid("设备状态字段或版本无效，未更新连接状态。");
        if(request.ExamId is not null&&request.SessionId is null)throw AdministrationSql.Invalid("未登录设备不能报告考试状态。");
        if(request.Page=="登录"&&(request.SessionId is not null||request.ExamId is not null))throw AdministrationSql.Invalid("登录页不能保留上一名考生的信息。");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request,AdministrationSql.Json))));
        var now=_time.GetUtcNow();
        await using var c=await source.OpenConnectionAsync(ct);
        await using var t=await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        await RequireActiveAsync(c,t,deviceId,ct);
        await using(var lockState=AdministrationSql.Command(c,t,"SELECT connection_id,generation,sequence,payload_hash,last_seen_at,page FROM exam.device_presence WHERE device_id=$1 FOR UPDATE;",deviceId))
        {
            await using var reader=await lockState.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct)||reader.GetString(0)!=request.ConnectionId||reader.GetInt64(1)!=request.Generation)
                throw AdministrationSql.Conflict("此设备已建立新的运行连接，请重新连接服务器。");
            var sequence=reader.GetInt64(2);
            if(request.Sequence<sequence)throw AdministrationSql.Conflict("较新的设备状态已保存，未接受迟到的旧状态。");
            if(request.Sequence==sequence)
            {
                if(reader.IsDBNull(3)||reader.GetString(3)!=hash)throw AdministrationSql.Conflict("同一状态序号不能提交不同内容。");
                return new(request.Generation,sequence,reader.GetFieldValue<DateTimeOffset>(4));
            }
            if(reader.GetString(5)=="已退出")
                throw AdministrationSql.Conflict("该运行连接已退出，请重新建立连接。");
        }
        if(request.SessionId is not null)
        {
            await using var session=AdministrationSql.Command(c,t,"SELECT exam_ids FROM exam.student_sessions WHERE session_id=$1 AND device_id=$2;",request.SessionId,deviceId);
            var exams=await session.ExecuteScalarAsync(ct) as string[];
            if(exams is null||request.ExamId is not null&&!exams.Contains(request.ExamId,StringComparer.Ordinal))
                throw new ParticipationRuleException(ParticipationErrorCodes.SessionNotFound,"设备、考生会话与考试不匹配，未报告其他考生状态。");
        }
        await using var update=AdministrationSql.Command(c,t,"""
            UPDATE exam.device_presence SET sequence=$2,payload_hash=$3,session_id=$4,exam_id=$5,page=$6,
             current_question_id=$7,installed_paper_version=$8,download_state=$9,recovering=$10,last_seen_at=$11,
             disconnected_at=CASE WHEN $12 THEN $11::timestamptz ELSE NULL END,
             hub_connection_id=CASE WHEN $12 THEN NULL ELSE hub_connection_id END,updated_at=$11
             WHERE device_id=$1;
            """,deviceId,request.Sequence,hash,request.Disconnect?null:request.SessionId,request.Disconnect?null:request.ExamId,
            request.Disconnect?"已退出":request.Page,request.Disconnect?null:request.CurrentQuestionId,request.InstalledPaperVersion,
            request.DownloadState,request.Recovering,now,request.Disconnect);
        await update.ExecuteNonQueryAsync(ct);await t.CommitAsync(ct);return new(request.Generation,request.Sequence,now);
    }
    public async Task HubConnectedAsync(string deviceId,string hubConnectionId,CancellationToken ct=default)
    {
        await using var command=source.CreateCommand("""
            UPDATE exam.device_presence SET hub_connection_id=$2,disconnected_at=NULL,last_seen_at=$3,
                recovering=false,updated_at=$3 WHERE device_id=$1 AND page<>'已退出';
            """);
        command.Parameters.AddWithValue(deviceId);command.Parameters.AddWithValue(hubConnectionId);command.Parameters.AddWithValue(_time.GetUtcNow());
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task HubDisconnectedAsync(string deviceId,string hubConnectionId,CancellationToken ct=default)
    {
        await using var command=source.CreateCommand("""
            UPDATE exam.device_presence SET disconnected_at=$3,recovering=true,updated_at=$3,
                hub_connection_id=NULL WHERE device_id=$1 AND hub_connection_id=$2;
            """);
        command.Parameters.AddWithValue(deviceId);command.Parameters.AddWithValue(hubConnectionId);command.Parameters.AddWithValue(_time.GetUtcNow());
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task RequireActiveAsync(NpgsqlConnection c,NpgsqlTransaction t,string id,CancellationToken ct)
    {
        await using var command=AdministrationSql.Command(c,t,"SELECT activation_status FROM exam.activation_devices WHERE device_id=$1 FOR SHARE;",id);
        if(await command.ExecuteScalarAsync(ct) is not string status||status!="active")
            throw new ParticipationRuleException(ParticipationErrorCodes.NotActivated,"设备未激活，不能更新连接状态。");
    }
}
