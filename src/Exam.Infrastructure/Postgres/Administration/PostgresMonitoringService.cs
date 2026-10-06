using System.Data;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Administration;
using Npgsql;
using static Exam.Infrastructure.Postgres.Administration.PostgresDeviceDirectory;

namespace Exam.Infrastructure.Postgres.Administration;

/// <summary>一次快照的名单、答案、回收、版本和指令全部来自同一 PostgreSQL MVCC 快照。</summary>
public sealed class PostgresMonitoringService(NpgsqlDataSource source,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider _time=timeProvider??TimeProvider.System;
    public async Task<MonitoringSnapshot> GetAsync(string examId,string? roomGroupId=null,string? query=null,CancellationToken ct=default)
    {
        if(query?.Length>128||roomGroupId?.Length>256)throw AdministrationSql.Invalid("监控筛选参数过长。");
        var now=_time.GetUtcNow();
        await using var c=await source.OpenConnectionAsync(ct);
        await using var t=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await AdministrationSql.RequireExamAsync(c,t,examId,ct);
        ExamVersionSnapshot? version=null;
        await using(var snapshot=AdministrationSql.Command(c,t,"SELECT snapshot::text FROM exam.version_state WHERE exam_id=$1;",examId))
        {
            if(await snapshot.ExecuteScalarAsync(ct) is string json)
                version=JsonSerializer.Deserialize<ExamVersionSnapshot>(json,AdministrationSql.Json);
        }
        var rows=new List<MonitoringParticipant>();
        await using(var command=AdministrationSql.Command(c,t,"""
            WITH focus AS (
              SELECT participation_id,count(*)::integer AS count,coalesce(sum(duration_ms),0)::bigint AS duration
                FROM exam.focus_loss_events WHERE exam_id=$1 GROUP BY participation_id)
            SELECT p.participation_id,p.candidate_id,r.candidate_name,r.class_text,r.admission_no,
                   p.authorized_device_id,d.client_number,d.room_group_id,coalesce(g.name,'未分组') AS group_name,
                   p.business_state,p.entry_version,p.authorization_version,p.version,p.current_paper_version,
                   coalesce(progress.answer_version,0) AS answer_version,
                   coalesce(progress.paper_version,presence.installed_paper_version,0) AS installed_paper_version,
                   coalesce(final.frozen_answers,progress.answers,'{}'::jsonb)::text AS answers,
                   coalesce(presence.current_question_id,progress.current_question_id) AS current_question_id,
                   presence.page,presence.download_state,presence.last_seen_at,presence.disconnected_at,presence.recovering,
                   final.receipt_id,final.received_at,coalesce(focus.count,0) AS focus_count,
                   coalesce(focus.duration,0)::bigint AS focus_duration
              FROM exam.participations p
              JOIN exam.candidate_roster_entries r ON r.exam_id=p.exam_id AND r.candidate_id=p.candidate_id
              JOIN exam.activation_devices d ON d.device_id=p.authorized_device_id
              LEFT JOIN exam.room_groups g ON g.group_id=d.room_group_id
              LEFT JOIN exam.participation_progress progress ON progress.participation_id=p.participation_id
                  AND progress.entry_version=p.entry_version
              LEFT JOIN exam.device_presence presence ON presence.device_id=d.device_id AND presence.exam_id=p.exam_id
                  AND EXISTS (SELECT 1 FROM exam.student_sessions s WHERE s.session_id=presence.session_id
                      AND s.device_id=d.device_id AND s.candidate_ids_by_exam->>p.exam_id=p.candidate_id)
              LEFT JOIN exam.final_submissions final ON final.participation_id=p.participation_id
              LEFT JOIN focus ON focus.participation_id=p.participation_id
             WHERE p.exam_id=$1 AND p.business_state IN ('waiting','answering','submitted')
             ORDER BY r.class_text,r.admission_no,p.participation_id;
            """,examId))
        {
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var id=Text(reader,"participation_id")!;var seen=Time(reader,"last_seen_at");
                var state=Enum.Parse<ParticipationBusinessState>(Text(reader,"business_state")!,true);
                var receipt=Text(reader,"receipt_id");var page=Text(reader,"page")??"未报告";
                var answers=JsonSerializer.Deserialize<Dictionary<string,AnswerValue>>(Text(reader,"answers")!,AdministrationSql.Json)
                    ??throw new InvalidDataException("已保存进度的答案结构无效。");
                var revision=version is not null&&version.Participants.TryGetValue(id,out var found)?found:null;
                rows.Add(new(id,Text(reader,"candidate_id")!,Text(reader,"candidate_name")!,Text(reader,"class_text")!,
                    Text(reader,"admission_no")!,Text(reader,"authorized_device_id")!,Text(reader,"client_number")!,Text(reader,"room_group_id"),Text(reader,"group_name")!,
                    state,ConnectionStatusPolicy.Evaluate(now,seen,Time(reader,"disconnected_at"),Bool(reader,"recovering")),page,Text(reader,"current_question_id"),seen,
                    Long(reader,"entry_version"),Long(reader,"authorization_version"),Long(reader,"version"),Long(reader,"answer_version"),
                    Long(reader,"installed_paper_version"),Long(reader,"current_paper_version"),Text(reader,"download_state")??"未报告",answers,
                    receipt is not null?"已回收":page=="回收中"?"回收中":"未回收",receipt,Time(reader,"received_at"),
                    reader.GetInt32(reader.GetOrdinal("focus_count")),Long(reader,"focus_duration"),revision,[],[]));
            }
        }
        var byId=rows.ToDictionary(x=>x.ParticipationId,StringComparer.Ordinal);
        var commands=new Dictionary<string,List<MonitoringCommand>>(StringComparer.Ordinal);
        await using(var command=AdministrationSql.Command(c,t,"""
            SELECT DISTINCT ON (e.participation_id,e.event_type) e.participation_id,e.event_id,e.event_type,
                   e.state_version,e.acknowledgement_result,e.created_at,e.acknowledged_at
              FROM exam.command_events e JOIN exam.participations p ON p.participation_id=e.participation_id
             WHERE e.exam_id=$1 AND e.entry_version=p.entry_version AND e.target_device_id=p.authorized_device_id
               AND e.target_authorization_version=p.authorization_version
             ORDER BY e.participation_id,e.event_type,e.state_version DESC,e.created_at DESC,e.event_id DESC;
            """,examId))
        {
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var id=Text(reader,"participation_id")!;if(!byId.TryGetValue(id,out var participant))continue;
                var eventType=Enum.Parse<CommandEventType>(Text(reader,"event_type")!);
                var ack=Text(reader,"acknowledgement_result");
                var canResend=(eventType is CommandEventType.Start or CommandEventType.End)&&
                    ack!="RejectedAsStale"&&(ack!="Applied"||eventType==CommandEventType.End)&&participant.ReceiptId is null&&
                    (eventType==CommandEventType.End?version?.LifecycleState==ExamLifecycle.Ended:version?.LifecycleState==ExamLifecycle.Running);
                if(!commands.TryGetValue(id,out var list))commands[id]=list=[];
                list.Add(new(Text(reader,"event_id")!,eventType,Long(reader,"state_version"),
                    ack switch {"Applied"=>"已执行","Received"=>"已收到","ResourceUpdateFailed"=>"资源更新失败","RejectedAsStale"=>"已拒绝过期指令",_=>"待确认"},
                    Time(reader,"created_at")!.Value,Time(reader,"acknowledged_at"),canResend));
            }
        }
        var conflicts=new Dictionary<string,List<ConflictDevice>>(StringComparer.Ordinal);
        await using(var command=AdministrationSql.Command(c,t,"""
            SELECT x.participation_id,d.device_id,d.client_number,coalesce(g.name,'未分组') AS group_name,
                   d.device_id=p.authorized_device_id AS authorized,presence.last_seen_at,presence.disconnected_at,
                   presence.recovering,presence.page,
                   CASE WHEN progress.device_id=d.device_id THEN progress.answer_version ELSE 0 END AS answer_version,
                   CASE WHEN progress.device_id=d.device_id THEN progress.last_synced_at ELSE NULL END AS last_synced_at,
                   CASE WHEN progress.device_id=d.device_id THEN
                     (SELECT count(*)::integer FROM jsonb_each(progress.answers) answer
                       WHERE answer.value->>'kind' IN ('selected','abstain')) ELSE 0 END AS saved_answer_count
              FROM exam.participation_device_conflicts x
              JOIN exam.participations p ON p.participation_id=x.participation_id
              JOIN exam.activation_devices d ON d.device_id IN (x.original_device_id,x.pending_device_id)
              LEFT JOIN exam.room_groups g ON g.group_id=d.room_group_id
              LEFT JOIN exam.device_presence presence ON presence.device_id=d.device_id AND presence.exam_id=p.exam_id
                  AND EXISTS (SELECT 1 FROM exam.student_sessions s WHERE s.session_id=presence.session_id
                      AND s.device_id=d.device_id AND s.candidate_ids_by_exam->>p.exam_id=p.candidate_id)
              LEFT JOIN exam.participation_progress progress ON progress.participation_id=p.participation_id
                  AND progress.entry_version=p.entry_version
             WHERE p.exam_id=$1 AND p.business_state IN ('waiting','answering')
             ORDER BY x.participation_id,authorized DESC,d.client_number;
            """,examId))
        {
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                var id=Text(reader,"participation_id")!;if(!conflicts.TryGetValue(id,out var list))conflicts[id]=list=[];
                var seen=Time(reader,"last_seen_at");
                list.Add(new(Text(reader,"device_id")!,Text(reader,"client_number")!,Text(reader,"group_name")!,
                    ConnectionStatusPolicy.Evaluate(now,seen,Time(reader,"disconnected_at"),Bool(reader,"recovering")),
                    seen,Bool(reader,"authorized"),Long(reader,"answer_version"),Text(reader,"page")??"未报告")
                { LastSyncedAt=Time(reader,"last_synced_at"),SavedAnswerCount=reader.GetInt32(reader.GetOrdinal("saved_answer_count")) });
            }
        }
        var questions=version?.Paper?.Structure.Questions.ToDictionary(q=>q.QuestionId,StringComparer.Ordinal);
        rows=rows.Select(row=>row with
        {
            Commands=commands.GetValueOrDefault(row.ParticipationId)??[],
            ConflictDevices=conflicts.GetValueOrDefault(row.ParticipationId)??[],
            CurrentQuestionNo=row.CurrentQuestionId is {} questionId&&questions?.TryGetValue(questionId,out var question)==true?question.QuestionNo:null,
            CurrentOptionIds=row.CurrentQuestionId is {} currentId&&questions?.TryGetValue(currentId,out var current)==true?current.OptionIds:null
        }).ToList();
        long rosterVersion=0;
        await using(var head=AdministrationSql.Command(c,t,"SELECT roster_version FROM exam.actual_roster_versions WHERE exam_id=$1;",examId))
            if(await head.ExecuteScalarAsync(ct) is long v)rosterVersion=v;
        var missing=rows.Where(x=>x.ReceiptId is null).ToArray();
        var filtered=rows.Where(x=>(roomGroupId is null||(roomGroupId==""?x.RoomGroupId is null:x.RoomGroupId==roomGroupId))&&
            (string.IsNullOrWhiteSpace(query)||new[]{x.Name,x.ClassText,x.AdmissionNo,x.ClientNumber}.Any(s=>s.Contains(query.Trim(),StringComparison.OrdinalIgnoreCase)))).ToArray();
        await t.CommitAsync(ct);
        return new(examId,now,version?.Versions??new(0,0,0),version?.LifecycleState??ExamLifecycle.Scheduled,
            rosterVersion,rows.Count,rows.Count-missing.Length,filtered,missing);
    }
    private static long Long(NpgsqlDataReader r,string name){var i=r.GetOrdinal(name);return r.IsDBNull(i)?0:r.GetInt64(i);}
}
