using System.Text.Json;
using Exam.IntegrationTests.Postgres;
using Exam.Application.Activation;
using Exam.Application.Scoring;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Submissions;

/// <summary>
/// 评分处理 计分状态命令的端到端用例：真实 HTTP 宿主 + 真实 CLI 进程。
/// 覆盖鉴权失败、正常读取与参数错误，验证命令输出与退出码约定。
/// </summary>
public sealed partial class PostgresSubmissionStoreTests
{
    private const string GradingCliManifest = """
        {"schemaVersion":1,"examId":"grading-cli-exam","examName":"计分命令合成考试",
         "subjects":[{"subjectId":"s","name":"科目","maxScore":"1"}],
         "groups":[{"groupId":"g","subjectId":"s","name":"单选","type":"single","order":1,
            "questionCount":1,"maxScore":"1","scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
         "questions":[{"questionId":"q","questionNo":1,"subjectId":"s","groupId":"g",
             "source":"questions/计分命令合成考试_1.tex","options":["A","B"]}],"resourceRoots":[]}
        """;

    private const string GradingCliAnswerKey = """
        {"schemaVersion":1,"examId":"grading-cli-exam","answers":[{"questionId":"q","questionNo":1,"correctOptionIds":["A"]}]}
        """;

    [Fact]
    public async Task Grading_ActualCliReportsVersionScopeAndReadinessOverRealHttp()
    {
        var rules = ImportedGradingRules.Create(GradingCliManifest, GradingCliAnswerKey);
        var scope = new InMemoryGradingScope();
        var inputs = new InMemoryGradingInputs();
        inputs.SetRules(rules.ExamId, new PublishedGradingRules(rules, 3));
        scope.SetScope(rules.ExamId, [new("graded", 3), new("pending", null)]);

        await using var app = await GradingAppAsync(scope, inputs);
        var server = app.Urls.Single();

        var denied = await RunGradingCliAsync(server, ["grading", "status", rules.ExamId], authenticated: false);
        Assert.Equal(3, denied.Exit);

        var status = await RunGradingCliAsync(server, ["grading", "status", rules.ExamId]);
        Assert.True(status.Exit == 0, status.Output);
        var envelope = JsonSerializer.Deserialize<ApiEnvelope<GradingStatusReport>>(status.Output, JsonDefaults.Create());
        Assert.NotNull(envelope?.Result);
        Assert.Equal(3, envelope!.Result!.CurrentGradingVersion);
        Assert.Equal(2, envelope.Result.FinalSubmissionCount);
        Assert.Equal(1, envelope.Result.GradedAtCurrentVersion);
        Assert.False(envelope.Result.Readiness!.RecalculationReady);
        Assert.Contains(envelope.Result.Readiness.Outstanding, item => item.Reason == RegradeReason.NeverGraded);

        var badAction = await RunGradingCliAsync(server, ["grading", "explode", rules.ExamId]);
        Assert.Equal(2, badAction.Exit);
    }

    /// <summary>与报表命令用例共用同一套 CLI 进程启动方式，避免两处实现不一致。</summary>
    private Task<(int Exit, string Output)> RunGradingCliAsync(string server, string[] args, bool authenticated = true) =>
        RunReportCliAsync(server, args, authenticated);

    private async Task<WebApplication> GradingAppAsync(IGradingScope scope, IGradingInputs inputs)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ExamServer:ServerId"] = "synthetic-grading-server";
        builder.Configuration["ExamServer:NodeId"] = "node-a";
        builder.Configuration["ExamServer:ServerEpoch"] = "1";
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            foreach (var converter in JsonDefaults.Create().Converters) options.SerializerOptions.Converters.Add(converter);
        });
        builder.Services.AddSingleton(scope);
        builder.Services.AddSingleton(inputs);
        builder.Services.AddSingleton(new GradingStatusService(inputs, scope));
        builder.Services.AddSingleton(new ServerNodeEnvelope("synthetic-grading-server", "node-a", 1));
        ReportTestToken = await SyntheticOperators.LoginAsync(fixture.DataSource, "synthetic-operator");
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddSingleton<IDeviceCredentialValidator, CollectionTestDeviceValidator>();
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource,
            new() { ServerId = "synthetic-grading-server", NodeId = "node-a", ServerEpoch = 1 }, true));
        var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGroup("/api/v1").MapGradingApi();
        app.MapGet("/api/v1/exams", () => Microsoft.AspNetCore.Http.Results.Json(
            new ApiEnvelope<Exam.Contracts.Exams.ExamSummary[]>("synthetic-grading-server", "node-a", 1, "synthetic-catalog",
                [new("grading-cli-exam", "计分命令合成考试", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1),
                    ExamLifecycle.Running, false, false, 2, 2, 1)], null), JsonDefaults.Create()));
        await app.StartAsync();
        return app;
    }
}
