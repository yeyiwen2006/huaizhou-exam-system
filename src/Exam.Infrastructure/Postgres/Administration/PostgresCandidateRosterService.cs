using System.Data;
using Exam.Contracts.Administration;
using Exam.Domain.Administration;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Administration;

public sealed class PostgresCandidateRosterService(NpgsqlDataSource source,TimeProvider? timeProvider=null)
{
    private readonly TimeProvider _time=timeProvider??TimeProvider.System;
    public async Task<CandidateRosterSnapshot> GetAsync(string examId,CancellationToken ct=default)
    {
        await using var c=await source.OpenConnectionAsync(ct);
        await using var t=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await AdministrationSql.RequireExamAsync(c,t,examId,ct);
        var result=await ReadAsync(c,t,examId,ct);await t.CommitAsync(ct);return result;
    }
    public Task<RosterImportResult> ImportAsync(string examId,ImportCandidateRosterRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AdministrationSql.MutateAsync(source,"roster.import","roster:"+examId,request.RequestId,actor,request,_time.GetUtcNow(),async(c,t,token)=>
        {
            var current=await LockAndReadAsync(c,t,examId,request.ExpectedVersion,token);
            var plan=RosterRules.Plan(current.Entries,request.Rows);
            if(plan.Issues.Count>0)return new RosterImportResult(false,0,plan.Duplicates,current,plan.Issues);
            foreach(var row in plan.NewRows)await InsertAsync(c,t,examId,row,token);
            if(plan.NewRows.Count>0)await IncrementAsync(c,t,examId,token);
            return new RosterImportResult(true,plan.NewRows.Count,plan.Duplicates,await ReadAsync(c,t,examId,token),[]);
        },ct);
    }
    public Task<CandidateRosterSnapshot> UpdateAsync(string examId,string candidateId,UpdateCandidateRosterRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);AdministrationSql.Require(candidateId,"考生标识");
        return AdministrationSql.MutateAsync(source,"roster.update","roster:"+examId,request.RequestId,actor,new{candidateId,request},_time.GetUtcNow(),async(c,t,token)=>
        {
            var current=await LockAndReadAsync(c,t,examId,request.ExpectedVersion,token);
            var prior=current.Entries.SingleOrDefault(e=>e.CandidateId==candidateId)??throw AdministrationSql.Invalid("该考生已不在当前考试名单中。");
            var errors=RosterRules.Validate(request.Entry);
            if(errors.Count>0)throw AdministrationSql.Invalid(string.Join(" ",errors.Select(x=>x.Message)));
            var row=request.Entry with{Name=request.Entry.Name.Trim(),ClassText=request.Entry.ClassText.Trim(),AdmissionNo=request.Entry.AdmissionNo.Trim()};
            if(prior.Name==row.Name&&prior.ClassText==row.ClassText&&prior.AdmissionNo==row.AdmissionNo)return current;
            if(current.Entries.Any(e=>e.CandidateId!=candidateId&&e.AdmissionNo==row.AdmissionNo))throw AdministrationSql.Conflict("准考证号与已有考生冲突，未修改。");
            await RequireUnusedAsync(c,t,examId,candidateId,token);
            await using(var delete=AdministrationSql.Command(c,t,"DELETE FROM exam.candidate_roster_entries WHERE exam_id=$1 AND candidate_id=$2;",examId,candidateId))await delete.ExecuteNonQueryAsync(token);
            // 替换身份标识，使并发登录得到的旧身份不能在名单更正后继续申请进入。
            await InsertAsync(c,t,examId,row,token);await IncrementAsync(c,t,examId,token);
            return await ReadAsync(c,t,examId,token);
        },ct);
    }
    public Task<CandidateRosterSnapshot> DeleteAsync(string examId,string candidateId,DeleteCandidateRosterRequest request,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(request);AdministrationSql.Require(candidateId,"考生标识");
        return AdministrationSql.MutateAsync(source,"roster.delete","roster:"+examId,request.RequestId,actor,new{candidateId,request},_time.GetUtcNow(),async(c,t,token)=>
        {
            var current=await LockAndReadAsync(c,t,examId,request.ExpectedVersion,token);
            if(!current.Entries.Any(e=>e.CandidateId==candidateId))throw AdministrationSql.Invalid("该考生已不在当前考试名单中。");
            await RequireUnusedAsync(c,t,examId,candidateId,token);
            await using var delete=AdministrationSql.Command(c,t,"DELETE FROM exam.candidate_roster_entries WHERE exam_id=$1 AND candidate_id=$2;",examId,candidateId);
            await delete.ExecuteNonQueryAsync(token);await IncrementAsync(c,t,examId,token);return await ReadAsync(c,t,examId,token);
        },ct);
    }
    private static async Task<CandidateRosterSnapshot> LockAndReadAsync(NpgsqlConnection c,NpgsqlTransaction t,string examId,long expected,CancellationToken ct)
    {
        if(expected<0)throw AdministrationSql.Invalid("名单版本无效。");
        await AdministrationSql.RequireExamAsync(c,t,examId,ct,lockRow:true);
        await using(var insert=AdministrationSql.Command(c,t,"""
            INSERT INTO exam.roster_heads(exam_id,version)
            VALUES($1,CASE WHEN EXISTS(SELECT 1 FROM exam.candidate_roster_entries WHERE exam_id=$1) THEN 1 ELSE 0 END)
            ON CONFLICT(exam_id) DO NOTHING;
            """,examId))await insert.ExecuteNonQueryAsync(ct);
        await using(var head=AdministrationSql.Command(c,t,"SELECT version FROM exam.roster_heads WHERE exam_id=$1 FOR UPDATE;",examId))
            if((long)(await head.ExecuteScalarAsync(ct))! != expected)throw AdministrationSql.Conflict("名单已被其他考务端修改；当前预览失效，请刷新后重新确认。");
        return await ReadAsync(c,t,examId,ct);
    }
    private static async Task RequireUnusedAsync(NpgsqlConnection c,NpgsqlTransaction t,string examId,string candidateId,CancellationToken ct)
    {
        await using var query=AdministrationSql.Command(c,t,"""
            SELECT EXISTS(SELECT 1 FROM exam.participations WHERE exam_id=$1 AND candidate_id=$2)
                OR EXISTS(SELECT 1 FROM exam.entry_preparations WHERE exam_id=$1 AND candidate_id=$2);
            """,examId,candidateId);
        if((bool)(await query.ExecuteScalarAsync(ct))!)throw AdministrationSql.Conflict("该考生已有入场或参加考试记录，不能修改身份或删除；原答卷和历史记录已保留。");
    }
    private static async Task InsertAsync(NpgsqlConnection c,NpgsqlTransaction t,string examId,RosterImportRow row,CancellationToken ct)
    {
        await using var command=AdministrationSql.Command(c,t,"""
            INSERT INTO exam.candidate_roster_entries(exam_id,candidate_id,candidate_name,class_text,admission_no)
            VALUES($1,$2,$3,$4,$5);
            """,examId,Guid.NewGuid().ToString("N"),row.Name,row.ClassText,row.AdmissionNo);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task IncrementAsync(NpgsqlConnection c,NpgsqlTransaction t,string examId,CancellationToken ct)
    { await using var command=AdministrationSql.Command(c,t,"UPDATE exam.roster_heads SET version=version+1 WHERE exam_id=$1;",examId);await command.ExecuteNonQueryAsync(ct); }
    private static async Task<CandidateRosterSnapshot> ReadAsync(NpgsqlConnection c,NpgsqlTransaction t,string examId,CancellationToken ct)
    {
        var entries=new List<CandidateRosterEntry>();
        await using(var query=AdministrationSql.Command(c,t,"SELECT candidate_id,candidate_name,class_text,admission_no FROM exam.candidate_roster_entries WHERE exam_id=$1 ORDER BY admission_no,candidate_id;",examId))
        {await using var reader=await query.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))entries.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)));}
        await using var head=AdministrationSql.Command(c,t,"SELECT version FROM exam.roster_heads WHERE exam_id=$1;",examId);
        var version=await head.ExecuteScalarAsync(ct);return new(examId,version is long v?v:entries.Count>0?1:0,entries);
    }
}
