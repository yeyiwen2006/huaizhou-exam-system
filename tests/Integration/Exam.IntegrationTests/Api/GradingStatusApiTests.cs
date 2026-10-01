using System.Net;
using Exam.Application.Scoring;
using Exam.Contracts.Common;
using Exam.Domain.Scoring;
using Exam.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Api;

/// <summary>
/// D05 计分状态端点：路由、返回形状与禁缓存。
/// 用最小测试宿主加内存计分输入，验证的正是生产端点读取同一服务的结果，
/// 不代替报表门与重算后台各自的模块用例。
/// </summary>
public sealed class GradingStatusApiTests
{
    private const string Manifest = """
        {"schemaVersion":1,"examId":"exam-api","examName":"接口合成考试",
         "subjects":[{"subjectId":"s","name":"科目","maxScore":"1"}],
         "groups":[{"groupId":"g","subjectId":"s","name":"单选","type":"single","order":1,
            "questionCount":1,"maxScore":"1","scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
         "questions":[{"questionId":"q","questionNo":1,"subjectId":"s","groupId":"g",
             "source":"questions/接口合成考试_1.tex","options":["A","B"]}],"resourceRoots":[]}
        """;

    private const string AnswerKey = """
        {"schemaVersion":1,"examId":"exam-api","answers":[{"questionId":"q","questionNo":1,"correctOptionIds":["A"]}]}
        """;

    [Fact]
    public async Task StatusEndpoint_ReportsVersionScopeAndReadinessWithoutCaching()
    {
        var rules = ImportedGradingRules.Create(Manifest, AnswerKey);
        var scope = new InMemoryGradingScope();
        var inputs = new InMemoryGradingInputs();
        inputs.SetRules(rules.ExamId, new PublishedGradingRules(rules, 3));
        scope.SetScope(rules.ExamId, [new("s-1", 3), new("late", null)]);

        await using var application = await CreateApplicationAsync(scope, inputs);
        using var client = application.GetTestClient();

        var response = await client.GetAsync($"/api/v1/grading/{rules.ExamId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"currentGradingVersion\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"finalSubmissionCount\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"gradedAtCurrentVersion\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"recalculationReady\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"neverGraded\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusEndpoint_WithoutPublishedRules_ReportsNoVersion()
    {
        var rules = ImportedGradingRules.Create(Manifest, AnswerKey);
        var scope = new InMemoryGradingScope();
        scope.SetScope(rules.ExamId, [new("s-1", null)]);

        await using var application = await CreateApplicationAsync(scope, new InMemoryGradingInputs());
        using var client = application.GetTestClient();

        var response = await client.GetAsync($"/api/v1/grading/{rules.ExamId}/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"currentGradingVersion\":null", body, StringComparison.Ordinal);
        Assert.Contains("\"readiness\":null", body, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> CreateApplicationAsync(
        IGradingScope scope,
        IGradingInputs inputs)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExamServer:ServerId"] = "server-test",
            ["ExamServer:NodeId"] = "node-test",
            ["ExamServer:ServerEpoch"] = "1"
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            var defaults = JsonDefaults.Create();
            options.SerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = defaults.PropertyNameCaseInsensitive;
            foreach (var converter in defaults.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });
        builder.Services.AddSingleton<IGradingScope>(scope);
        builder.Services.AddSingleton<IGradingInputs>(inputs);
        builder.Services.AddSingleton(new GradingStatusService(inputs, scope));

        var application = builder.Build();
        // 只映射本组：整套 MapExamApi 还会带上报表与版本等端点，它们的依赖不属本用例范围。
        application.MapGroup("/api/v1").MapGradingApi();
        await application.StartAsync();
        return application;
    }
}
