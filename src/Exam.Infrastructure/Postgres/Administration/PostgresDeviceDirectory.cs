using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Domain.Administration;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Administration;

public sealed class PostgresDeviceDirectory(NpgsqlDataSource source,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider _time=timeProvider??TimeProvider.System;
    public async Task<DeviceDirectoryPage> ListAsync(string? roomGroupId=null,string? query=null,
        ActivationStatus? status=null,int offset=0,int limit=500,CancellationToken ct=default)
    {
        if(offset<0||limit is <1 or >2000||query?.Length>128||roomGroupId?.Length>256)
            throw AdministrationSql.Invalid("设备筛选或分页参数无效。");
        var now=_time.GetUtcNow();
        await using var command=source.CreateCommand("""
            SELECT d.device_id,d.client_number,d.room_group_id,coalesce(g.name,'未分组') AS group_name,
                   d.activation_status,d.version,p.page,p.current_question_id,p.last_seen_at,p.disconnected_at,
                   p.recovering,s.candidate_name,s.class_text,s.admission_no,p.exam_id,
                   coalesce(c.exam_name,m.display_name,p.exam_id) AS exam_name
              FROM exam.activation_devices d
              LEFT JOIN exam.room_groups g ON g.group_id=d.room_group_id
              LEFT JOIN exam.device_presence p ON p.device_id=d.device_id
              LEFT JOIN exam.student_sessions s ON s.session_id=p.session_id AND s.device_id=d.device_id
              LEFT JOIN exam.exam_catalog c ON c.exam_id=p.exam_id
              LEFT JOIN exam.student_exam_metadata m ON m.exam_id=p.exam_id
             WHERE ($1::text IS NULL OR ($1='' AND d.room_group_id IS NULL) OR d.room_group_id=$1)
               AND ($2::text IS NULL OR strpos(d.client_number,$2)>0 OR strpos(coalesce(s.candidate_name,''),$2)>0
                    OR strpos(coalesce(s.class_text,''),$2)>0 OR strpos(coalesce(s.admission_no,''),$2)>0)
               AND ($3::text IS NULL OR d.activation_status=$3)
             ORDER BY d.client_number OFFSET $4 LIMIT $5;
            """);
        command.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)roomGroupId??DBNull.Value});
        command.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)query?.Trim()??DBNull.Value});
        command.Parameters.Add(new(){NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Text,Value=(object?)status?.ToString().ToLowerInvariant()??DBNull.Value});
        command.Parameters.AddWithValue(offset);command.Parameters.AddWithValue(limit+1);
        var rows=new List<DeviceDirectoryEntry>();await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            var seen=Time(reader,"last_seen_at");var connection=ConnectionStatusPolicy.Evaluate(now,seen,Time(reader,"disconnected_at"),Bool(reader,"recovering"));
            rows.Add(new(Text(reader,"device_id")!,Text(reader,"client_number")!,Text(reader,"room_group_id"),Text(reader,"group_name")!,
                Enum.Parse<ActivationStatus>(Text(reader,"activation_status")!,true),connection,
                Text(reader,"candidate_name"),Text(reader,"class_text"),Text(reader,"admission_no"),Text(reader,"exam_id"),Text(reader,"exam_name"),
                Text(reader,"page")??"未报告",Text(reader,"current_question_id"),seen,reader.GetInt64(reader.GetOrdinal("version"))));
        }
        var more=rows.Count>limit;if(more)rows.RemoveAt(rows.Count-1);return new(rows,more?offset+limit:null,now);
    }
    internal static string? Text(NpgsqlDataReader r,string name){var i=r.GetOrdinal(name);return r.IsDBNull(i)?null:r.GetString(i);}
    internal static DateTimeOffset? Time(NpgsqlDataReader r,string name){var i=r.GetOrdinal(name);return r.IsDBNull(i)?null:r.GetFieldValue<DateTimeOffset>(i);}
    internal static bool Bool(NpgsqlDataReader r,string name){var i=r.GetOrdinal(name);return !r.IsDBNull(i)&&r.GetBoolean(i);}
}
