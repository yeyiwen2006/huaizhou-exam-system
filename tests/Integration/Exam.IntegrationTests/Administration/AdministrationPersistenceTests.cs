using CandidateRosterEntry = Exam.Contracts.Administration.CandidateRosterEntry;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Participation;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.IntegrationTests.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Exam.IntegrationTests.Administration;

[Collection(PostgresCollection.Name)]
[Trait("Category","Postgres")]
public sealed class AdministrationPersistenceTests(PostgresDatabaseFixture fixture)
{
    private const string Actor="synthetic-audit-operator";
    private static string Id()=>Guid.NewGuid().ToString("N");
    private PostgresCandidateRosterService Roster=>new(fixture.DataSource);
    private PostgresRoomService Rooms=>new(fixture.DataSource);

    [Fact]
    public async Task Roster_AtomicValidationLeadingZerosDuplicateReplayAndIsolation()
    {
        var a=CreateExam();var b=CreateExam();
        var request=new ImportCandidateRosterRequest(Id(),0,[new(1,"合成考生甲","001","000001")]);
        var result=await Roster.ImportAsync(a,request,Actor);
        Assert.True(result.Applied);Assert.Equal(1,result.Added);Assert.Equal(1,result.Snapshot.Version);
        var replay=await Roster.ImportAsync(a,request,Actor);
        Assert.Equal(result.Snapshot.Entries[0].CandidateId,replay.Snapshot.Entries[0].CandidateId);
        var failed=await Roster.ImportAsync(a,new(Id(),1,[new(1,"新增考生","002","000002"),new(2,"不同姓名","001","000001")]),Actor);
        Assert.False(failed.Applied);Assert.Single(failed.Issues);
        var saved=await new PostgresCandidateRosterService(fixture.DataSource).GetAsync(a);
        var person=Assert.Single(saved.Entries);Assert.Equal("001",person.ClassText);Assert.Equal("000001",person.AdmissionNo);Assert.Equal(1,saved.Version);
        Assert.Empty((await Roster.GetAsync(b)).Entries);
        var duplicates=await Roster.ImportAsync(a,new(Id(),1,[new(1,"合成考生甲","001","000001")]),Actor);
        Assert.True(duplicates.Applied);Assert.Equal(0,duplicates.Added);Assert.Equal(1,duplicates.Duplicates);Assert.Equal(1,duplicates.Snapshot.Version);
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Roster.ImportAsync(a,request with{Rows=[new(1,"篡改重试","001","000003")]},Actor));
        Assert.Single((await Roster.GetAsync(a)).Entries);
    }
    [Fact]
    public async Task ConcurrentRosterImports_OneVersionWinsWithoutPartialOrDuplicateWrites()
    {
        var exam=CreateExam();
        var attempts=await Task.WhenAll(Enumerable.Range(1,8).Select(async index=>
        {
            try{return (await Roster.ImportAsync(exam,new(Id(),0,[new(1,"合成考生"+index,"001",index.ToString("D6"))]),Actor)).Applied;}
            catch(ParticipationRuleException){return false;}
        }));
        Assert.Single(attempts,x=>x);var snapshot=await Roster.GetAsync(exam);
        Assert.Equal(1,snapshot.Version);Assert.Single(snapshot.Entries);
    }
    [Fact]
    public async Task RosterCorrection_InvalidatesCachedIdentity_AndCannotChangeParticipatingPeople()
    {
        var exam=CreateExam();var first=await Roster.ImportAsync(exam,new(Id(),0,[new(1,"原姓名","001","000001")]),Actor);
        var old=Assert.Single(first.Snapshot.Entries);
        var changed=await Roster.UpdateAsync(exam,old.CandidateId,new(Id(),1,new(1,"更正姓名","001","000001")),Actor);
        var person=Assert.Single(changed.Entries);Assert.NotEqual(old.CandidateId,person.CandidateId);
        Assert.False(await new PostgresCandidateRosterDirectory(fixture.DataSource).IsRegisteredAsync(exam,old.CandidateId));
        var device=await SeedDevice();await SeedParticipation(exam,person,device);
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Roster.UpdateAsync(exam,person.CandidateId,new(Id(),changed.Version,new(1,"禁止更改","001","000001")),Actor));
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Roster.DeleteAsync(exam,person.CandidateId,new(Id(),changed.Version),Actor));
        Assert.Equal(person,(await Roster.GetAsync(exam)).Entries[0]);
    }
    [Fact]
    public async Task RosterDelete_RequiresCorrectExamAndVersion()
    {
        var a=CreateExam();var b=CreateExam();var added=await Roster.ImportAsync(a,new(Id(),0,[new(1,"合成考生","001","001")]),Actor);
        var person=added.Snapshot.Entries[0];
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Roster.DeleteAsync(b,person.CandidateId,new(Id(),0),Actor));
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Roster.DeleteAsync(a,person.CandidateId,new(Id(),0),Actor));
        var deleted=await Roster.DeleteAsync(a,person.CandidateId,new(Id(),1),Actor);Assert.Empty(deleted.Entries);Assert.Equal(2,deleted.Version);
    }
    [Theory]
    [InlineData("20261008001", "20261008002")]
    [InlineData("202610080001", "202610080002")]
    public async Task Rooms_BatchMoveIsVersionedAtomicAuditedAndDoesNotChangeCredentials(string firstNumber, string secondNumber)
    {
        var first=await Rooms.CreateAsync(new(Id(),"合成试场甲-"+Id()),Actor);
        var second=await Rooms.CreateAsync(new(Id(),"合成试场乙-"+Id()),Actor);
        var a=await SeedDevice(firstNumber);var b=await SeedDevice(secondNumber);
        var invalid=new MoveDevicesRequest(Id(),first.GroupId,[new(a.Number,1),new(b.Number,9)],"合成冲突测试");
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Rooms.MoveAsync(invalid,Actor));
        var directory=new PostgresDeviceDirectory(fixture.DataSource);
        Assert.Null(Assert.Single((await directory.ListAsync(query:a.Number)).Items).RoomGroupId);
        var request=new MoveDevicesRequest(Id(),first.GroupId,[new(a.Number,1),new(b.Number,1)],"合成批量转组");
        Assert.Equal(2,(await Rooms.MoveAsync(request,Actor)).Moved);
        Assert.Equal(2,(await Rooms.MoveAsync(request,Actor)).Moved);
        Assert.Single(await Rooms.HistoryAsync(a.Number));
        var device=Assert.Single((await directory.ListAsync(query:a.Number)).Items);
        Assert.Equal(first.GroupId,device.RoomGroupId);Assert.Equal(2,device.Version);
        Assert.Equal("synthetic-verifier-not-a-secret",await Scalar("SELECT protected_credential FROM exam.activation_devices WHERE device_id=$1",a.Id));
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Rooms.DeleteAsync(first.GroupId,new(Id(),first.Version),Actor));
        Assert.Equal(2,(await Rooms.MoveAsync(new(Id(),second.GroupId,[new(a.Number,2),new(b.Number,2)],"合成第二次转组"),Actor)).Moved);
        Assert.True((await Rooms.DeleteAsync(first.GroupId,new(Id(),first.Version),Actor)).Deleted);
        var history=await Rooms.HistoryAsync(a.Number);Assert.Equal(2,history.Count);Assert.Contains(history,x=>x.FromGroupName==first.Name&&x.ToGroupName==second.Name);
        Assert.Equal(a.Number,Assert.Single((await directory.ListAsync(query:a.Number)).Items).ClientNumber);
    }
    [Fact]
    public async Task RoomRenameAndDeletion_EnforceUniquenessVersionsAndEmptyMembership()
    {
        var name="合成唯一试场-"+Id();var first=await Rooms.CreateAsync(new(Id(),name),Actor);
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Rooms.CreateAsync(new(Id()," "+name+" "),Actor));
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>Rooms.RenameAsync(first.GroupId,new(Id(),99,"无效版本"),Actor));
        var renamed=await Rooms.RenameAsync(first.GroupId,new(Id(),1,name+"-新"),Actor);Assert.Equal(2,renamed.Version);
        var request=new DeleteRoomGroupRequest(Id(),2);Assert.True((await Rooms.DeleteAsync(first.GroupId,request,Actor)).Deleted);
        Assert.True((await Rooms.DeleteAsync(first.GroupId,request,Actor)).Deleted);
        Assert.DoesNotContain(await Rooms.ListAsync(),x=>x.GroupId==first.GroupId);
    }

    [Theory]
    [InlineData("20261007011", "202610090011")]
    [InlineData("202610070011", "202610100011")]
    public async Task RoomHistorySurvivesLegacyUpgradeAndDailyNumberChange(string oldNumber, string currentNumber)
    {
        var room = await Rooms.CreateAsync(new(Id(), "合成历史试场-" + Id()), Actor);
        var device = await SeedDevice(oldNumber);
        await Rooms.MoveAsync(new(Id(), room.GroupId, [new(device.Number, 1)], "合成首次转组"), Actor);
        await Sql("UPDATE exam.activation_devices SET client_number=$2 WHERE device_id=$1", device.Id, currentNumber);
        await Rooms.MoveAsync(new(Id(), null, [new(currentNumber, 2)], "合成取消分组"), Actor);

        var history = await Rooms.HistoryAsync(currentNumber);
        Assert.Equal(2, history.Count);
        Assert.All(history, entry => Assert.Equal(currentNumber, entry.ClientNumber));
        Assert.Contains(history, entry => entry.FromGroupName == "未分组" && entry.ToGroupName == room.Name);
        Assert.Contains(history, entry => entry.FromGroupName == room.Name && entry.ToGroupName == "未分组");
        Assert.Single(await Rooms.HistoryAsync(oldNumber));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM exam.device_room_history WHERE device_id=$1 AND client_number=$2", device.Id, oldNumber));
    }
    [Fact]
    public async Task Presence_RejectsLateProcessesAndChangedReplays_AndLogoutClearsCandidate()
    {
        var device=await SeedDevice();var exam=CreateExam();var roster=await Roster.ImportAsync(exam,new(Id(),0,[new(1,"合成在线考生","001","000001")]),Actor);
        var person=roster.Snapshot.Entries[0];var session=await SeedSession(exam,person,device);
        var clock=new MutableClock();var service=new PostgresDevicePresenceService(fixture.DataSource,clock);
        var lease=await service.OpenAsync(device.Id,new(Id(),Id()));
        var online=new UpdateDevicePresenceRequest(lease.ConnectionId,lease.Generation,1,session,exam,"等待考试",null,1,"已下载",false);
        await service.UpdateAsync(device.Id,online);await service.UpdateAsync(device.Id,online);
        var directory=new PostgresDeviceDirectory(fixture.DataSource,clock);var row=Assert.Single((await directory.ListAsync(query:device.Number)).Items);
        Assert.Equal(person.Name,row.CandidateName);Assert.Equal(DeviceConnectionState.Online,row.ConnectionState);
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>service.UpdateAsync(device.Id,online with{Page="正在答题"}));
        var exit=online with{Sequence=2,Disconnect=true,Page="已退出"};await service.UpdateAsync(device.Id,exit);
        row=Assert.Single((await directory.ListAsync(query:device.Number)).Items);Assert.Null(row.CandidateName);Assert.Null(row.ExamId);Assert.Equal(DeviceConnectionState.Offline,row.ConnectionState);
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>service.UpdateAsync(device.Id,online with{Sequence=3}));
        var restarted=await service.OpenAsync(device.Id,new(Id(),Id()));Assert.True(restarted.Generation>lease.Generation);
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>service.UpdateAsync(device.Id,online with{Sequence=100}));
        await service.UpdateAsync(device.Id,new(restarted.ConnectionId,restarted.Generation,1,null,null,"登录",null,0,"未下载",false));
        Assert.Null(Assert.Single((await directory.ListAsync(query:device.Number)).Items).CandidateName);
        clock.Advance(TimeSpan.FromSeconds(135));Assert.Equal(DeviceConnectionState.Offline,Assert.Single((await directory.ListAsync(query:device.Number)).Items).ConnectionState);
    }
    [Fact]
    public async Task Presence_RejectsOtherDeviceSessions_AndOldHubDisconnectCannotInvalidateNewHub()
    {
        var a=await SeedDevice();var b=await SeedDevice();var exam=CreateExam();
        var roster=await Roster.ImportAsync(exam,new(Id(),0,[new(1,"合成身份","001","000001")]),Actor);var session=await SeedSession(exam,roster.Snapshot.Entries[0],b);
        var clock=new MutableClock();var service=new PostgresDevicePresenceService(fixture.DataSource,clock);
        var lease=await service.OpenAsync(a.Id,new(Id(),Id()));
        await Assert.ThrowsAsync<ParticipationRuleException>(()=>service.UpdateAsync(a.Id,new(lease.ConnectionId,lease.Generation,1,session,exam,"等待考试",null,1,"已下载",false)));
        await service.HubConnectedAsync(a.Id,"old-hub");await service.HubConnectedAsync(a.Id,"new-hub");await service.HubDisconnectedAsync(a.Id,"old-hub");
        var directory=new PostgresDeviceDirectory(fixture.DataSource,clock);
        Assert.Equal(DeviceConnectionState.Online,Assert.Single((await directory.ListAsync(query:a.Number)).Items).ConnectionState);
        await service.HubDisconnectedAsync(a.Id,"new-hub");Assert.Equal(DeviceConnectionState.Offline,Assert.Single((await directory.ListAsync(query:a.Number)).Items).ConnectionState);
    }
    [Fact]
    public async Task Monitoring_IsHumanReadable_SeparatesOfflineFromBusinessState_AndCountsOnlyDurableReceipts()
    {
        var exam=CreateExam();var roster=await Roster.ImportAsync(exam,new(Id(),0,[new(1,"合成参加考试考生","001","000001"),new(2,"仅在名册中","002","000002")]),Actor);
        var person=roster.Snapshot.Entries[0];var device=await SeedDevice();var part=await SeedParticipation(exam,person,device);
        var session=await SeedSession(exam,person,device);var clock=new MutableClock();var presence=new PostgresDevicePresenceService(fixture.DataSource,clock);
        var paper = new PaperPublicationInfo(1,"synthetic-paper",new([new("q1",7,"subject","group","single",["A","B"])]),[],clock.GetUtcNow(),Actor,"合成监測测试");
        await Sql("UPDATE exam.version_state SET snapshot=jsonb_set(snapshot,'{paper}',$2::jsonb) WHERE exam_id=$1;",
            exam,JsonSerializer.Serialize(paper,JsonDefaults.Create()));
        var lease=await presence.OpenAsync(device.Id,new(Id(),Id()));
        await presence.UpdateAsync(device.Id,new(lease.ConnectionId,lease.Generation,1,session,exam,"回收中","q1",1,"已下载",false));
        var answers=JsonSerializer.Serialize(new Dictionary<string,AnswerValue>{{"q1",AnswerValue.Selected("B")}},JsonDefaults.Create());
        await Sql("""
            INSERT INTO exam.participation_progress(participation_id,exam_id,candidate_id,entry_version,device_id,authorization_version,answer_version,paper_version,answers,current_question_id,last_synced_at)
            VALUES($1,$2,$3,1,$4,1,7,1,$5::jsonb,'q1',$6);
            """,part,exam,person.CandidateId,device.Id,answers,clock.GetUtcNow());
        var monitor=new PostgresMonitoringService(fixture.DataSource,clock);var snapshot=await monitor.GetAsync(exam);
        Assert.Equal(1,snapshot.ActualCount);Assert.Equal(0,snapshot.CollectedCount);Assert.Single(snapshot.Uncollected);
        var row=Assert.Single(snapshot.Participants);Assert.Equal(person.Name,row.Name);Assert.Equal("001",row.ClassText);Assert.Equal(device.Number,row.ClientNumber);
        Assert.Equal(7,row.AnswerVersion);Assert.Equal("q1",row.CurrentQuestionId);Assert.Equal("回收中",row.CollectionState);Assert.Equal("B",Assert.Single(row.Answers["q1"].OptionIds));
        Assert.Equal(7,row.CurrentQuestionNo);
        Assert.Equal(new[] { "A", "B" },row.CurrentOptionIds);
        var pendingDevice=await SeedDevice();
        await Sql("INSERT INTO exam.participation_device_conflicts(participation_id,original_device_id,pending_device_id,detected_at) VALUES($1,$2,$3,$4);",
            part,device.Id,pendingDevice.Id,clock.GetUtcNow());
        var savedAt=clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        await presence.UpdateAsync(device.Id,new(lease.ConnectionId,lease.Generation,2,session,exam,"正在答题（已切出）","q1",1,"已下载",false));
        Assert.Equal("正在答题（已切出）",Assert.Single((await monitor.GetAsync(exam)).Participants).Page);
        var conflictRow=Assert.Single((await monitor.GetAsync(exam)).Participants);
        var original=Assert.Single(conflictRow.ConflictDevices,item=>item.DeviceId==device.Id);
        var pending=Assert.Single(conflictRow.ConflictDevices,item=>item.DeviceId==pendingDevice.Id);
        Assert.Equal(savedAt,original.LastSyncedAt);Assert.Equal(1,original.SavedAnswerCount);
        Assert.Equal(clock.GetUtcNow(),original.LastCommunicationAt);
        Assert.Null(pending.LastSyncedAt);Assert.Equal(0,pending.SavedAnswerCount);
        // A previous entry's progress is not current evidence for either device.
        await Sql("UPDATE exam.participation_progress SET entry_version=2 WHERE participation_id=$1;",part);
        Assert.All(Assert.Single((await monitor.GetAsync(exam)).Participants).ConflictDevices,item=>Assert.Null(item.LastSyncedAt));
        await Sql("UPDATE exam.participation_progress SET entry_version=1 WHERE participation_id=$1;",part);
        clock.Advance(TimeSpan.FromSeconds(135));row=Assert.Single((await monitor.GetAsync(exam)).Participants);Assert.Equal(DeviceConnectionState.Offline,row.ConnectionState);Assert.Equal(ParticipationBusinessState.Waiting,row.BusinessState);
        await Sql("""
            INSERT INTO exam.final_submissions(submission_id,exam_id,candidate_id,participation_id,device_id,authorization_version,paper_version,frozen_answers,received_at,receipt_id,grading_version)
            VALUES($1,$2,$3,$4,$5,1,1,$6::jsonb,$7,$8,1);
            """,Id(),exam,person.CandidateId,part,device.Id,answers,clock.GetUtcNow(),Id());
        snapshot=await monitor.GetAsync(exam);Assert.Equal(1,snapshot.CollectedCount);Assert.Empty(snapshot.Uncollected);Assert.Equal("已回收",snapshot.Participants[0].CollectionState);
        Assert.Single((await monitor.GetAsync(exam,query:"000001")).Participants);
        var empty=await monitor.GetAsync(exam,query:"不匹配姓名");Assert.Empty(empty.Participants);Assert.Equal(1,empty.ActualCount);
    }
    [Fact]
    public async Task Monitoring_PhysicalConnectionDoesNotDependOnTheCurrentCandidateSession()
    {
        var exam=CreateExam();
        var roster=await Roster.ImportAsync(exam,new(Id(),0,
            [new(1,"合成原考生","001","000001"),new(2,"合成换人考生","001","000002")]),Actor);
        var person=roster.Snapshot.Entries.Single(x=>x.AdmissionNo=="000001");
        var other=roster.Snapshot.Entries.Single(x=>x.AdmissionNo=="000002");
        var original=await SeedDevice();var pending=await SeedDevice();
        var part=await SeedParticipation(exam,person,original);
        var clock=new MutableClock();var savedAt=clock.GetUtcNow();
        var answers=JsonSerializer.Serialize(new Dictionary<string,AnswerValue>
            { ["q1"]=AnswerValue.Selected("B"),["q2"]=AnswerValue.Abstain(),["q3"]=AnswerValue.Empty() },JsonDefaults.Create());
        await Sql("""
            INSERT INTO exam.participation_progress(participation_id,exam_id,candidate_id,entry_version,device_id,authorization_version,answer_version,paper_version,answers,current_question_id,last_synced_at)
            VALUES($1,$2,$3,1,$4,1,7,1,$5::jsonb,'q1',$6);
            """,part,exam,person.CandidateId,original.Id,answers,savedAt);
        await Sql("INSERT INTO exam.participation_device_conflicts(participation_id,original_device_id,pending_device_id,detected_at) VALUES($1,$2,$3,$4);",
            part,original.Id,pending.Id,savedAt);
        var presence=new PostgresDevicePresenceService(fixture.DataSource,clock);
        var originalLease=await presence.OpenAsync(original.Id,new(Id(),Id()));
        var pendingLease=await presence.OpenAsync(pending.Id,new(Id(),Id()));
        var otherSession=await SeedSession(exam,other,original);
        clock.Advance(TimeSpan.FromSeconds(1));
        await presence.UpdateAsync(original.Id,new(originalLease.ConnectionId,originalLease.Generation,1,
            otherSession,exam,"正在答题","other-question",9,"已下载",false));
        await presence.UpdateAsync(pending.Id,new(pendingLease.ConnectionId,pendingLease.Generation,1,
            null,null,"登录",null,0,"未下载",false));
        var monitor=new PostgresMonitoringService(fixture.DataSource,clock);
        var row=Assert.Single((await monitor.GetAsync(exam)).Participants);
        Assert.Equal(DeviceConnectionState.Online,row.ConnectionState);
        Assert.Equal(clock.GetUtcNow(),row.LastCommunicationAt);
        Assert.Equal("已切换考生或考试",row.Page);
        Assert.Equal("q1",row.CurrentQuestionId);Assert.Equal(1,row.InstalledPaperVersion);
        var originalRow=Assert.Single(row.ConflictDevices,x=>x.DeviceId==original.Id);
        var pendingRow=Assert.Single(row.ConflictDevices,x=>x.DeviceId==pending.Id);
        Assert.Equal(DeviceConnectionState.Online,originalRow.ConnectionState);
        Assert.Equal(clock.GetUtcNow(),originalRow.LastCommunicationAt);
        Assert.Equal("已切换考生或考试",originalRow.Page);
        Assert.Equal(savedAt,originalRow.LastSyncedAt);Assert.Equal(2,originalRow.SavedAnswerCount);
        Assert.Equal(DeviceConnectionState.Online,pendingRow.ConnectionState);
        Assert.Equal("未登录",pendingRow.Page);
        Assert.Null(pendingRow.LastSyncedAt);Assert.Equal(0,pendingRow.SavedAnswerCount);
        // An explicit disconnect changes connectivity without removing participation or saved answers.
        await presence.UpdateAsync(pending.Id,new(pendingLease.ConnectionId,pendingLease.Generation,2,
            null,null,"已退出",null,0,"未下载",false) { Disconnect=true });
        row=Assert.Single((await monitor.GetAsync(exam)).Participants);
        Assert.Equal(ParticipationBusinessState.Waiting,row.BusinessState);
        Assert.Equal(DeviceConnectionState.Offline,Assert.Single(row.ConflictDevices,x=>x.DeviceId==pending.Id).ConnectionState);
        Assert.Equal(2,Assert.Single(row.ConflictDevices,x=>x.DeviceId==original.Id).SavedAnswerCount);
    }

    [Fact]
    public async Task EmptyExamMonitoring_DoesNotInventParticipantsOrCompletion()
    {
        var snapshot=await new PostgresMonitoringService(fixture.DataSource).GetAsync(CreateExam());
        Assert.Empty(snapshot.Participants);Assert.Empty(snapshot.Uncollected);Assert.Equal(0,snapshot.ActualCount);Assert.Equal(0,snapshot.CollectedCount);
    }
    private string CreateExam()
    {
        var id="a-admin-"+Id();var now=DateTimeOffset.UtcNow.AddYears(80);
        Assert.True(new PostgresExamVersioningService(fixture.DataSource).CreateExam(new(id,new(now.AddHours(1),now.AddHours(2)),now,Id())).Succeeded);
        return id;
    }
    private async Task<(string Id,string Number)> SeedDevice(string? clientNumber=null)
    {
        var device=Id();var number=clientNumber??Random.Shared.NextInt64(10000000000,99999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Sql("""
            INSERT INTO exam.activation_devices(device_id,client_number,activation_date,activation_status,protected_credential,credential_version,created_at,version)
            VALUES($1,$2,DATE '2026-10-02','active','synthetic-verifier-not-a-secret',1,$3,1);
            """,device,number,DateTimeOffset.UtcNow);
        return(device,number);
    }
    private async Task<string> SeedParticipation(string exam,CandidateRosterEntry person,(string Id,string Number) device)
    {
        var part=Id();await Sql("""
            INSERT INTO exam.participations(participation_id,exam_id,candidate_id,entry_version,business_state,authorized_device_id,authorization_version,version)
            VALUES($1,$2,$3,1,'waiting',$4,1,1);
            """,part,exam,person.CandidateId,device.Id);return part;
    }
    private async Task<string> SeedSession(string exam,CandidateRosterEntry person,(string Id,string Number) device)
    {
        var session=Id();await Sql("""
            INSERT INTO exam.student_sessions(session_id,device_id,candidate_id,candidate_name,class_text,admission_no,exam_ids,authenticated_at,candidate_ids_by_exam)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,jsonb_build_object(($7::text[])[1],$3::text));
            """,session,device.Id,person.CandidateId,person.Name,person.ClassText,person.AdmissionNo,new[]{exam},DateTimeOffset.UtcNow);return session;
    }
    private async Task Sql(string sql,params object[] values)
    {await using var command=fixture.DataSource.CreateCommand(sql);foreach(var value in values)command.Parameters.AddWithValue(value);await command.ExecuteNonQueryAsync();}
    private async Task<object?> Scalar(string sql,params object[] values)
    {await using var command=fixture.DataSource.CreateCommand(sql);foreach(var value in values)command.Parameters.AddWithValue(value);return await command.ExecuteScalarAsync();}
    private sealed class MutableClock:TimeProvider
    {
        private DateTimeOffset _now=new(2026,10,2,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(TimeSpan duration)=>_now+=duration;
    }
}
