using Exam.IntegrationTests.Postgres;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Exam.Application.Activation;
using Exam.Application.Reporting;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Reporting;
using Exam.Contracts.Versioning;
using Exam.Domain.Common;
using Exam.Domain.Reporting;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.Infrastructure.Postgres.Reporting;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Reporting;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Submissions;

// Reuses the existing synthetic A05/D04 fixture to test the actual G wiring.
public sealed partial class PostgresSubmissionStoreTests
{
    [Theory]
    [InlineData("single", "A")]
    [InlineData("multiple", "BD")]
    [InlineData("indeterminate", "BD")]
    public async Task Reporting_RealApiRequiresOperatorAndExportsTheSameDurableSnapshot(string type, string expectedAnswer)
    {
        var (context, versions) = await SeedReportExamAsync(type);
        var source = ReportSource();
        var reports = new ReportPublicationService(source);
        Assert.Equal(ReportState.ExamNotEnded, (await reports.StatusAsync(context.ExamId)).State);
        FinishReportExam(context.ExamId, versions);
        var status = await reports.StatusAsync(context.ExamId);
        Assert.Equal(ReportState.Ready, status.State);
        Assert.Equal(1, status.GradedCount);
        var first = await reports.ReadAsync(context.ExamId);
        var restarted = await new ReportPublicationService(ReportSource()).ReadAsync(context.ExamId);
        Assert.Equal(first.Status.SnapshotId, restarted.Status.SnapshotId);
        Assert.Equal(first.Result.GetRawText(), restarted.Result.GetRawText());

        await using var app = await ReportAppAsync(reports);
        using var client = app.GetTestClient();
        var path = "/api/v1/reports/" + context.ExamId;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Id", context.DeviceId);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Credential", "synthetic-device-only");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path+"/xlsx?snapshotId="+status.SnapshotId)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReportTestToken);
        var envelope = await client.GetFromJsonAsync<ApiEnvelope<PublishedReport>>(path, JsonDefaults.Create());
        Assert.Equal(first.Status.SnapshotId, envelope!.Result!.Status.SnapshotId);
        var calculation = ReportJson.Read(envelope.Result.Result);
        Assert.Equal("3", calculation.StudentTotals[0].TotalScore.TryFormatFiniteDecimal(out var exact) ? exact : "invalid");
        using var binary = await client.GetAsync(path+"/xlsx?snapshotId="+status.SnapshotId);
        var downloaded = await ReportWorkbookDownload.ReadAsync(binary, status.SnapshotId!);
        using var workbook = new XLWorkbook(new MemoryStream(downloaded.Bytes));
        Assert.Equal(5, workbook.Worksheets.Count);
        Assert.Equal(calculation.StudentTotals[0].Participant.ExamNumber, workbook.Worksheet(1).Cell(2, 2).GetString());
        Assert.Equal(3d, workbook.Worksheet(1).Cell(2, 5).GetDouble());
        Assert.Equal(expectedAnswer, workbook.Worksheet(3).Cell(2, 6).GetString());
        Assert.Equal(XLColor.Black, workbook.Worksheet(3).Cell(2, 6).Style.Font.FontColor);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path+"/xlsx?snapshotId=stale")).StatusCode);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reports.ReadAsync(context.ExamId)));
        Assert.All(concurrent, result => Assert.Equal(first.Status.SnapshotId, result.Status.SnapshotId));
    }

    [Fact]
    public async Task Reporting_MembershipSupplementAndRuleChangesImmediatelyRejectOldResults()
    {
        var (context, versions) = await SeedReportExamAsync();
        FinishReportExam(context.ExamId, versions);
        var reports = new ReportPublicationService(ReportSource());
        var initial = await reports.ReadAsync(context.ExamId);
        var second = await SeedParticipationAsync(examId: context.ExamId);
        var collecting = await reports.StatusAsync(context.ExamId);
        Assert.Equal(ReportState.Collecting, collecting.State);
        Assert.Equal(2, collecting.ParticipantCount);
        Assert.Equal(second.CandidateId, Assert.Single(collecting.MissingParticipants).CandidateId);
        Assert.Equal(ErrorCodes.ReportNotReady, (await Assert.ThrowsAsync<DomainRuleException>(() =>
            reports.ReadAsync(context.ExamId, initial.Status.SnapshotId))).Code);
        var saved = await new PostgresSubmissionStore(fixture.DataSource).SaveSubmissionAsync(
            second.Submission(Id("g-submit"), Id("g-submission"), Answer("B")), DateTimeOffset.UtcNow, Id("g-receipt"));
        Assert.Equal(ReportState.Grading, (await reports.StatusAsync(context.ExamId)).State);
        await GradeReportSubmissionAsync(saved.Submission!.SubmissionId, context.ExamId);
        var collected = await reports.ReadAsync(context.ExamId);
        Assert.NotEqual(initial.Status.SnapshotId, collected.Status.SnapshotId);
        var rules = (await new PostgresGradingInputs(fixture.DataSource).LoadRulesAsync(context.ExamId))!;
        await using var command = fixture.DataSource.CreateCommand("""
            SELECT payload::text FROM exam.version_events WHERE exam_id=$1 AND operation_type='grading.publish'
            ORDER BY stream_revision DESC LIMIT 1;
            """);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value=context.ExamId });
        var publication = JsonSerializer.Deserialize<PublishGradingRulesRequest>((string)(await command.ExecuteScalarAsync())!, JsonDefaults.Create())!;
        var key = JsonSerializer.Serialize(new AnswerKeyFile(1, context.ExamId, [new("q-1", 1, ["B"])]), ImportJson.Create());
        Assert.True(versions.PublishGradingRules(publication with { ExpectedVersion = rules.GradingVersion,
            AnswerKeyJson = key, RequestId = Id("g-rules"), RuleSetId = "synthetic-new-rules", Now = DateTimeOffset.UtcNow }).Succeeded);
        Assert.Equal(ReportState.Grading, (await reports.StatusAsync(context.ExamId)).State);
        await Assert.ThrowsAsync<DomainRuleException>(() => reports.ReadAsync(context.ExamId, collected.Status.SnapshotId));
        await using var ids = fixture.DataSource.CreateCommand("SELECT submission_id FROM exam.final_submissions WHERE exam_id=$1;");
        ids.Parameters.Add(new Npgsql.NpgsqlParameter { Value=context.ExamId });
        var submissions = new List<string>();
        await using (var reader = await ids.ExecuteReaderAsync()) while (await reader.ReadAsync()) submissions.Add(reader.GetString(0));
        foreach (var id in submissions) await GradeReportSubmissionAsync(id, context.ExamId);
        var recalculated = await reports.ReadAsync(context.ExamId);
        Assert.Equal(2, recalculated.Status.GradingVersion);
        Assert.NotEqual(collected.Status.SnapshotId, recalculated.Status.SnapshotId);
        Assert.Equal(1, ReportJson.Read(recalculated.Result).CommonSampleCount);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("raw")]
    [InlineData("clamp")]
    public async Task Reporting_CorruptGradeAndUninitializedProductionNodeCannotPublish(string corruption)
    {
        var (context, versions) = await SeedReportExamAsync();
        FinishReportExam(context.ExamId, versions);
        var sql=corruption switch
        {
            "raw" => "UPDATE exam.grading_results SET subject_scores=jsonb_set(subject_scores,'{0,exactRawScore}','\"999\"') WHERE exam_id=$1;",
            "clamp" => "UPDATE exam.grading_results SET subject_scores=jsonb_set(subject_scores,'{0,wasClampedToZero}','true') WHERE exam_id=$1;",
            _ => "UPDATE exam.grading_results SET total_score='999' WHERE exam_id=$1;"
        };
        await using var command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value=context.ExamId }); await command.ExecuteNonQueryAsync();
        Assert.Equal(ReportState.InvalidSource, (await new ReportPublicationService(ReportSource()).StatusAsync(context.ExamId)).State);
        var strict = new PostgresReportSource(fixture.DataSource, new PostgresActiveNodeWriteGuard(new(Id("strict"), "node-a", 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.ReadAsync(context.ExamId).AsTask());
    }

    [Fact]
    public async Task Reporting_ConcurrentExportsDoNotHoldSubmissionLocksAndRestartRebuildsNewSnapshot()
    {
        var (context,versions)=await SeedReportExamAsync(); FinishReportExam(context.ExamId,versions);
        var reports=new ReportPublicationService(ReportSource()); var initial=await reports.ReadAsync(context.ExamId);
        using var exporter=new ReportExportService(reports);
        var exports=Enumerable.Range(0,8).Select(async _=>
        {
            try { Assert.NotEmpty(await exporter.ExportAsync(context.ExamId,initial.Status.SnapshotId!)); }
            catch(DomainRuleException error) { Assert.Contains(error.Code,new[] { ErrorCodes.ReportNotReady,ErrorCodes.VersionConflict }); }
        }).ToArray();
        var second=await SeedParticipationAsync(examId:context.ExamId);
        var saved=await new PostgresSubmissionStore(fixture.DataSource).SaveSubmissionAsync(
            second.Submission(Id("g-concurrent-submit"),Id("g-concurrent-submission"),Answer("B")),DateTimeOffset.UtcNow,Id("g-concurrent-receipt"))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(saved.Submission); await Task.WhenAll(exports).WaitAsync(TimeSpan.FromSeconds(30));
        await GradeReportSubmissionAsync(saved.Submission!.SubmissionId,context.ExamId);
        var current=await new ReportPublicationService(ReportSource()).ReadAsync(context.ExamId);
        Assert.NotEqual(initial.Status.SnapshotId,current.Status.SnapshotId);
        Assert.Equal(2,current.Status.CollectedCount); Assert.Equal(2,ReportJson.Read(current.Result).StudentTotals.Count);
    }

    // 合成考务账号的会话令牌；建立测试服务时登录取得（每个测试一个新实例）。
    private string ReportTestToken = string.Empty;
    private PostgresReportSource ReportSource() => new(fixture.DataSource,
        new PostgresActiveNodeWriteGuard(new("synthetic-report-server", "node-a", 1), allowUninitializedState: true));
    private async Task<(ParticipationContext Context, PostgresExamVersioningService Versions)> SeedReportExamAsync(string type = "single")
    {
        var context = await SeedParticipationAsync();
        var now = DateTimeOffset.UtcNow;
        string[] selected = type == "single" ? ["A"] : ["B", "D"];
        var scoring = new ManifestGroupScoring(type == "multiple" ? null : new("3", "0", "-0.75"),
            type == "single" ? null : new("3", "1", "0", "-0.75"));
        var manifest = new ExamManifest(1, context.ExamId, "合成报表联调", [new("subject", "Synthetic subject", "3")],
            [new("group", "subject", "Synthetic group", type, 1, 1, false, "3", scoring)],
            [new("q-1", 1, "subject", "group", "questions/合成报表联调_1.tex", ["A", "B", "C", "D"], null)], []);
        var manifestJson = JsonSerializer.Serialize(manifest, ImportJson.Create());
        var keyJson = JsonSerializer.Serialize(new AnswerKeyFile(1, context.ExamId, [new("q-1", 1, selected)]), ImportJson.Create());
        var versions = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(versions.CreateExam(new(context.ExamId, new(now.AddHours(1), now.AddHours(2)), now, Id("g-create"))).Succeeded);
        var resource = Id("g-paper"); await StoreGradingPipelinePaperAsync(context.ExamId, resource, now);
        var structure = Exam.Domain.Scoring.ImportedGradingRules.Create(manifestJson, keyJson).Structure;
        Assert.True(versions.PublishPaper(new(context.ExamId, 0, resource, structure, [], new(true,true,true,true),
            "synthetic-review", "合成报表", now, Id("g-paper-request"))).Succeeded);
        Assert.True(versions.PublishGradingRules(new(context.ExamId, 0, "synthetic-rules", new(true,true,true),
            "synthetic-review", "合成报表", now, Id("g-rules-request"), manifestJson, keyJson)).Succeeded);
        var saved = await new PostgresSubmissionStore(fixture.DataSource).SaveSubmissionAsync(
            context.Submission(Id("g-submit"), Id("g-submission"), new Dictionary<string, Exam.Contracts.Submissions.AnswerValue>
                { ["q-1"] = Exam.Contracts.Submissions.AnswerValue.Selected(selected) }), now, Id("g-receipt"));
        await GradeReportSubmissionAsync(saved.Submission!.SubmissionId, context.ExamId);
        return (context, versions);
    }
    private static void FinishReportExam(string examId, PostgresExamVersioningService versions)
    {
        var now = DateTimeOffset.UtcNow.AddHours(3);
        Assert.True(versions.ExecuteScheduleTrigger(new(examId, 1, ScheduleTriggerKind.Start, now, Id("g-start"))).Succeeded);
        Assert.True(versions.ExecuteScheduleTrigger(new(examId, 1, ScheduleTriggerKind.End, now, Id("g-end"))).Succeeded);
    }
    private async Task GradeReportSubmissionAsync(string submissionId, string examId)
    {
        var inputs = new PostgresGradingInputs(fixture.DataSource);
        var rules = (await inputs.LoadRulesAsync(examId))!;
        var submission = (await inputs.LoadSubmissionAsync(submissionId))!;
        var grade = rules.Rules.Grade(submission, rules.GradingVersion);
        Assert.True(grade.Succeeded, grade.FailureReason);
        await new PostgresGradingResultStore(fixture.DataSource).SaveAsync(new(grade.Grade!, DateTimeOffset.UtcNow));
    }
    private async Task<WebApplication> ReportAppAsync(ReportPublicationService reports, bool realHttp = false)
    {
        var builder = WebApplication.CreateBuilder();
        if (realHttp) builder.WebHost.UseUrls("http://127.0.0.1:0"); else builder.WebHost.UseTestServer();
        builder.Configuration["ExamServer:ServerId"] = "synthetic-report-server";
        builder.Configuration["ExamServer:NodeId"] = "node-a"; builder.Configuration["ExamServer:ServerEpoch"] = "1";
        builder.Services.ConfigureHttpJsonOptions(options =>
        { foreach (var converter in JsonDefaults.Create().Converters) options.SerializerOptions.Converters.Add(converter); });
        builder.Services.AddSingleton(reports); builder.Services.AddSingleton<ReportExportService>();
        builder.Services.AddSingleton(new ServerNodeEnvelope("synthetic-report-server", "node-a", 1));
        ReportTestToken = await SyntheticOperators.LoginAsync(fixture.DataSource, "synthetic-operator");
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddSingleton<IDeviceCredentialValidator, CollectionTestDeviceValidator>();
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource,
            new() { ServerId="synthetic-report-server", NodeId="node-a", ServerEpoch=1 }, true));
        var app = builder.Build(); app.UseMiddleware<ApiExceptionMiddleware>(); app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGroup("/api/v1").MapReportApi(); await app.StartAsync(); return app;
    }
}
