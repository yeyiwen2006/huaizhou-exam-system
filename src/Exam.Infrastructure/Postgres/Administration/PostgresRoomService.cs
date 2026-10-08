using System.Text;
using Exam.Contracts.Administration;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Administration;

public sealed class PostgresRoomService(NpgsqlDataSource source,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider _time=timeProvider??TimeProvider.System;
    public async Task<IReadOnlyList<RoomGroup>> ListAsync(CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var command=AdministrationSql.Command(c,null,"""
            SELECT g.group_id,g.name,g.version,count(d.device_id)::integer
              FROM exam.room_groups g LEFT JOIN exam.activation_devices d ON d.room_group_id=g.group_id
             GROUP BY g.group_id,g.name,g.version ORDER BY g.name_key,g.group_id;
            """);
        var rows=new List<RoomGroup>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),reader.GetInt32(3)));return rows;
    }
    public Task<RoomGroup> CreateAsync(CreateRoomGroupRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);var name=Name(request.Name);var now=_time.GetUtcNow();
        return AdministrationSql.MutateAsync(source,"room.create","rooms",request.RequestId,actor,request,now,async(c,t,token)=>
        {
            var id=Guid.NewGuid().ToString("N");
            await using var command=AdministrationSql.Command(c,t,"INSERT INTO exam.room_groups(group_id,name,name_key,version,created_at,updated_at,updated_by) VALUES($1,$2,$3,1,$4,$4,$5);",id,name,Key(name),now,actor);
            await command.ExecuteNonQueryAsync(token);return new RoomGroup(id,name,1,0);
        },ct);
    }
    public Task<RoomGroup> RenameAsync(string id,RenameRoomGroupRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);var name=Name(request.Name);var now=_time.GetUtcNow();
        return AdministrationSql.MutateAsync(source,"room.rename","rooms",request.RequestId,actor,new{id,request},now,async(c,t,token)=>
        {
            var prior=await LockGroupAsync(c,t,id,token);if(prior.Version!=request.ExpectedVersion)throw AdministrationSql.Conflict("试场已被修改，请刷新后重试。");
            if(prior.Name==name)return prior;
            await using var command=AdministrationSql.Command(c,t,"UPDATE exam.room_groups SET name=$2,name_key=$3,version=version+1,updated_at=$4,updated_by=$5 WHERE group_id=$1;",id,name,Key(name),now,actor);
            await command.ExecuteNonQueryAsync(token);return prior with{Name=name,Version=prior.Version+1};
        },ct);
    }
    public Task<DeletedRoomGroup> DeleteAsync(string id,DeleteRoomGroupRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AdministrationSql.MutateAsync(source,"room.delete","rooms",request.RequestId,actor,new{id,request},_time.GetUtcNow(),async(c,t,token)=>
        {
            var prior=await LockGroupAsync(c,t,id,token);if(prior.Version!=request.ExpectedVersion)throw AdministrationSql.Conflict("试场已被修改，请刷新后重试。");
            if(prior.DeviceCount!=0)throw AdministrationSql.Conflict("该试场仍有设备，只有空试场可以删除。请先完成转组。");
            await using var command=AdministrationSql.Command(c,t,"DELETE FROM exam.room_groups WHERE group_id=$1;",id);await command.ExecuteNonQueryAsync(token);return new DeletedRoomGroup(id,true);
        },ct);
    }
    public Task<MoveDevicesResult> MoveAsync(MoveDevicesRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);AdministrationSql.Require(request.Reason,"转组原因",500);
        if(request.Devices is null || request.Devices.Count is <1 or >2000 || request.Devices.Any(x=>x is null||x.ExpectedVersion<1||x.ClientNumber?.Length is not (11 or 12)||!x.ClientNumber.All(char.IsAsciiDigit)) ||
           request.Devices.Select(x=>x.ClientNumber).Distinct(StringComparer.Ordinal).Count()!=request.Devices.Count)throw AdministrationSql.Invalid("请选择1至2000台不同的设备，并提供已加载的版本。");
        var now=_time.GetUtcNow();var target=string.IsNullOrWhiteSpace(request.RoomGroupId)?null:request.RoomGroupId;
        return AdministrationSql.MutateAsync(source,"room.move-devices","rooms",request.RequestId,actor,request,now,async(c,t,token)=>
        {
            var group=target is null?null:await LockGroupAsync(c,t,target,token,exclusive:false);
            var devices=new List<(string Id,string Number,string? GroupId,string GroupName,long Version)>();
            await using(var command=AdministrationSql.Command(c,t,"""
                SELECT d.device_id,d.client_number,d.room_group_id,coalesce(g.name,'未分组'),d.version
                  FROM exam.activation_devices d LEFT JOIN exam.room_groups g ON g.group_id=d.room_group_id
                 WHERE d.client_number=ANY($1) ORDER BY d.client_number FOR UPDATE OF d;
                """,(object)request.Devices.Select(x=>x.ClientNumber).ToArray()))
            {await using var reader=await command.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))devices.Add((reader.GetString(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetInt64(4)));}
            if(devices.Count!=request.Devices.Count)throw AdministrationSql.Invalid("部分设备编号不存在，整批未转组。");
            var expected=request.Devices.ToDictionary(x=>x.ClientNumber,x=>x.ExpectedVersion,StringComparer.Ordinal);
            if(devices.Any(x=>x.Version!=expected[x.Number]))throw AdministrationSql.Conflict("部分设备已被其他操作修改，整批未转组；请刷新后重新选择。");
            var moved=0;
            foreach(var device in devices.Where(x=>x.GroupId!=target))
            {
                await using(var update=AdministrationSql.Command(c,t,"UPDATE exam.activation_devices SET room_group_id=$2,version=version+1 WHERE device_id=$1;",device.Id,target))await update.ExecuteNonQueryAsync(token);
                await using var audit=AdministrationSql.Command(c,t,"""
                    INSERT INTO exam.device_room_history(request_id,device_id,client_number,from_group_id,from_group_name,to_group_id,to_group_name,actor_id,reason,changed_at)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10);
                    """,request.RequestId,device.Id,device.Number,device.GroupId,device.GroupName,target,group?.Name??"未分组",actor,request.Reason.Trim(),now);
                await audit.ExecuteNonQueryAsync(token);moved++;
            }
            return new MoveDevicesResult(moved,devices.Select(x=>x.Number).ToArray());
        },ct);
    }
    public async Task<IReadOnlyList<DeviceRoomHistory>> HistoryAsync(string clientNumber,CancellationToken ct=default)
    {
        AdministrationSql.Require(clientNumber,"学生端编号",12);
        await using var c=await source.OpenConnectionAsync(ct);
        // Current numbers resolve the stable device identity, including history before a number change.
        // The response identifies the requested number; stored audit rows keep their original numbers.
        await using var command=AdministrationSql.Command(c,null,"""
            SELECT $1::text,from_group_id,from_group_name,to_group_id,to_group_name,actor_id,reason,changed_at
              FROM exam.device_room_history
             WHERE device_id=(SELECT device_id FROM exam.activation_devices WHERE client_number=$1)
                OR client_number=$1
             ORDER BY changed_at DESC,request_id DESC LIMIT 2000;
            """,clientNumber);
        var rows=new List<DeviceRoomHistory>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1),reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetFieldValue<DateTimeOffset>(7)));return rows;
    }
    private static string Name(string? name){AdministrationSql.Require(name,"试场名称",80);var normalized=name!.Trim().Normalize(NormalizationForm.FormKC);AdministrationSql.Require(normalized,"试场名称",80);return normalized;}
    private static string Key(string name)=>name.ToUpperInvariant();
    private static async Task<RoomGroup> LockGroupAsync(NpgsqlConnection c,NpgsqlTransaction t,string id,CancellationToken ct,bool exclusive=true)
    {
        AdministrationSql.Require(id,"试场标识");
        string name;long version;
        await using(var command=AdministrationSql.Command(c,t,"SELECT name,version FROM exam.room_groups WHERE group_id=$1 "+(exclusive?"FOR UPDATE;":"FOR KEY SHARE;"),id))
        {await using var reader=await command.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw AdministrationSql.Invalid("该试场不存在，请刷新试场列表。");name=reader.GetString(0);version=reader.GetInt64(1);}
        await using var count=AdministrationSql.Command(c,t,"SELECT count(*)::integer FROM exam.activation_devices WHERE room_group_id=$1;",id);
        return new(id,name,version,(int)(await count.ExecuteScalarAsync(ct))!);
    }
}
