using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Fact]
    public async Task A05SubmissionAndOutbox_A06Publication_D04LatestExactResult_PreserveFrozenAnswer()
    {
        var context = await SeedParticipationAsync();
        var now = DateTimeOffset.UtcNow;
        var manifest = new ExamManifest(1, context.ExamId, "合成计分联调",
            [new("subject", "科目", "3")],
            [new("group", "subject", "单选", "single", 1, 1, false, "3", new(new("3", "0", "-0.75"), null))],
            [new("q-1", 1, "subject", "group", "questions/合成计分联调_1.tex", ["A", "B"], null)], []);
        var key = new AnswerKeyFile(1, context.ExamId, [new("q-1", 1, ["A"])]);
        var manifestJson = JsonSerializer.Serialize(manifest, ImportJson.Create());
        var keyJson = JsonSerializer.Serialize(key, ImportJson.Create());
        var imported = ImportedGradingRules.Create(manifestJson, keyJson);
        var versions = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(versions.CreateExam(new(context.ExamId, new(now.AddHours(1), now.AddHours(2)), now, Id("create"))).Succeeded);
        var resourceId = Id("paper");
        await StoreGradingPipelinePaperAsync(context.ExamId, resourceId, now);
        var paper = versions.PublishPaper(new(context.ExamId, 0, resourceId, imported.Structure, [],
            new(true, true, true, true), "synthetic-review", "接口验证", now, Id("paper-request")));
        Assert.True(paper.Succeeded, paper.Error);
        var firstRules = versions.PublishGradingRules(new(context.ExamId, 0, "rules-1", new(true, true, true),
            "synthetic-review", "首次规则", now, Id("rules-1"), manifestJson, keyJson));
        Assert.True(firstRules.Succeeded, firstRules.Error);

        var submissions = new PostgresSubmissionStore(fixture.DataSource);
        var saved = await submissions.SaveSubmissionAsync(
            context.Submission(Id("submit-request"), Id("submission"), Answer("A")), now, Id("receipt"));
        Assert.Equal(SubmissionPersistenceStatus.Saved, saved.Status);
        var original = saved.Submission!;
        Assert.Equal(1, original.Receipt.GradingVersion);
        var inputs = new PostgresGradingInputs(fixture.DataSource);
        var initialRules = (await inputs.LoadRulesAsync(context.ExamId))!;
        var initialGrade = initialRules.Rules.Grade(original, initialRules.GradingVersion);
        Assert.True(initialGrade.Succeeded, initialGrade.FailureReason);
        var results = new PostgresGradingResultStore(fixture.DataSource);
        await results.SaveAsync(new(initialGrade.Grade!, now));
        Assert.Equal("3", (await results.FindAsync(original.SubmissionId))!.Grade.ExactTotalScore);

        var changedKey = new AnswerKeyFile(1, context.ExamId, [new("q-1", 1, ["B"])]);
        var secondRules = versions.PublishGradingRules(new(context.ExamId, 1, "rules-2", new(true, true, true),
            "synthetic-review", "修改参考答案", now.AddSeconds(1), Id("rules-2"), manifestJson,
            JsonSerializer.Serialize(changedKey, ImportJson.Create())));
        Assert.True(secondRules.Succeeded, secondRules.Error);
        var tasks = new PostgresGradingTaskSource(fixture.DataSource);
        var claimed = (await tasks.ClaimAsync(10000, now.AddMinutes(2), TimeSpan.FromMinutes(1)))
            .Single(task => task.SubmissionId == original.SubmissionId);
        Assert.Equal(1, claimed.GradingVersion);
        // 这里显式组合生产适配器验证接口，不把本用例冒充后台服务注册或调度。
        var latest = (await new PostgresGradingInputs(fixture.DataSource).LoadRulesAsync(context.ExamId))!;
        var frozen = (await inputs.LoadSubmissionAsync(claimed.SubmissionId))!;
        Assert.Equal(2, latest.GradingVersion);
        var outcome = latest.Rules.Grade(frozen, latest.GradingVersion);
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        await results.SaveAsync(new(outcome.Grade!, now.AddSeconds(2)));
        Assert.True(await tasks.CompleteAsync(claimed));
        var result = Assert.Single(await results.ListAsync(context.ExamId, 2));
        Assert.Equal("-0.75", Assert.Single(result.Grade.Subjects).ExactRawScore);
        Assert.Equal("0", result.Grade.ExactTotalScore);
        Assert.Empty(await results.ListAsync(context.ExamId, 1));
        Assert.Equal(original.Receipt, (await inputs.LoadSubmissionAsync(original.SubmissionId))!.Receipt);
        Assert.Equal("A", Assert.Single((await inputs.LoadSubmissionAsync(original.SubmissionId))!.FrozenAnswers["q-1"].OptionIds));
        await Assert.ThrowsAsync<InvalidOperationException>(() => results.SaveAsync(new(initialGrade.Grade!, now)).AsTask());
        var queue = new PostgresGradingTaskQueue(fixture.DataSource);
        Assert.True(await queue.EnqueueOnceAsync(original.SubmissionId, 2));
        Assert.False(await queue.EnqueueOnceAsync(original.SubmissionId, 2));
        var regrade = (await tasks.ClaimAsync(10000, now.AddMinutes(4), TimeSpan.FromMinutes(1)))
            .Single(task => task.SubmissionId == original.SubmissionId);
        await results.SaveAsync(new(outcome.Grade!, now.AddSeconds(3)));
        Assert.True(await tasks.CompleteAsync(regrade));
        Assert.Single(await results.ListAsync(context.ExamId, 2));
    }

    private async Task StoreGradingPipelinePaperAsync(string examId, string resourceId, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-review-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
        var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>(["synthetic-review-node"], StringComparer.Ordinal));
        var encryption = new PackageEncryptionService(4096, policy);
        await using var source = new MemoryStream(RandomNumberGenerator.GetBytes(1024));
        await using var encrypted = new MemoryStream();
        var envelope = await encryption.EncryptAsync(source, encrypted, resourceId, 1, [new("synthetic-review-node", certificate)]);
        await new PostgresVersionedResourceStore(fixture.DataSource, policy).SaveAsync(new VersionedEncryptedResource(
            resourceId, examId, "paper-package", 1, encrypted.ToArray(), envelope, now, "synthetic-review"));
    }
}
