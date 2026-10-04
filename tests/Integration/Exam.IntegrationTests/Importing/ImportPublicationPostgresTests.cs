using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Application.Scoring;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Exam.IntegrationTests.Importing;

[Collection(PostgresCollection.Name)]
public sealed class ImportPublicationPostgresTests(PostgresDatabaseFixture database)
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task LocalPreview_OfficialUpload_AtomicVersions_RulesAndStudentPayload_WithLostReceiptReplay()
    {
        using var source = new SyntheticImportFixture();
        await using var server = await ServerFixture.Create(database.DataSource, source);
        var pipeline = new ImportPipeline(server.Api) { CompileOverride = SyntheticImportFixture.CompileFixtureAsync };
        using var preview = await pipeline.PrepareAsync(source.Source, server.ExamId);
        Assert.Equal(0, server.Versions.GetExam(server.ExamId).Value!.Versions.PaperVersion);
        Assert.Equal(0L, await server.ResourceCount());
        Assert.Equal("source-exam", JsonSerializer.Deserialize<ExamManifest>(File.ReadAllText(source.ManifestPath), ImportJson.Create())!.ExamId);
        server.Boundary.DropReceiptOnce = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.PublishAsync(preview));
        Assert.Equal(1, server.Versions.GetExam(server.ExamId).Value!.Versions.PaperVersion);
        var replay = await pipeline.PublishAsync(preview);
        Assert.True(preview.MatchesPublication(replay));
        Assert.Equal(2, server.Uploads);
        await server.AssertPublishedAndPrivate(replay);
    }

    [Theory]
    [Trait("Category", "Postgres")]
    [InlineData("settings")]
    [InlineData("start")]
    public async Task ServerTransactionRejectsChangesAfterTheLastClientCheck_WithoutPartialPublication(string change)
    {
        using var source = new SyntheticImportFixture();
        await using var server = await ServerFixture.Create(database.DataSource, source);
        var pipeline = new ImportPipeline(server.Api) { CompileOverride = SyntheticImportFixture.CompileFixtureAsync };
        using var preview = await pipeline.PrepareAsync(source.Source, server.ExamId);
        server.Boundary.BeforeUpload = async () =>
        {
            if (change == "settings")
            {
                var current = await server.Catalog.GetAsync(server.ExamId);
                var subject = current.Settings.Subjects.Single();
                var group = subject.Groups.Single();
                await server.Catalog.UpdateSettingsAsync(server.ExamId,
                    new(Guid.NewGuid().ToString("N"), current.Summary.SettingsVersion, current.Summary.Name,
                        new([subject with { Groups = [group with { AllowAbstain = false }] }])), "synthetic-operator");
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                var startsAt = now.AddSeconds(2);
                var schedule = server.Versions.UpdateSchedule(new(server.ExamId, 1,
                    new(startsAt, now.AddHours(1)), now, Guid.NewGuid().ToString("N")));
                Assert.True(schedule.Succeeded, schedule.Error);
                // Simulate the clock crossing the start while the upload is in flight, before the
                // scheduler has reconciled its projection. No production readiness guard is bypassed.
                await Task.Delay(startsAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100));
            }
        };
        var failure = await Assert.ThrowsAsync<ExamPublicationApiException>(() => pipeline.PublishAsync(preview));
        Assert.Equal(change == "start" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal(change == "start" ? "PUBLICATION_NOT_READY" : "VERSION_CONFLICT", failure.Code);
        Assert.Equal(0L, await server.ResourceCount());
        var actual = server.Versions.GetExam(server.ExamId).Value!;
        Assert.Equal(0, actual.Versions.PaperVersion);
        Assert.Equal(0, actual.Versions.GradingVersion);
        Assert.Null(await new PostgresGradingInputs(database.DataSource).LoadRulesAsync(server.ExamId));
        Assert.Equal(false, await server.Scalar("SELECT package_ready FROM exam.exam_entry_access WHERE exam_id=$1", server.ExamId));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    [Trait("Category", "Latex")]
    public async Task RealCli_ZipPreview_ExplicitConfirmation_AtomicUpload_AndCrossProcessReplay()
    {
        using var source = new SyntheticImportFixture();
        await using var server = await ServerFixture.Create(database.DataSource, source, network: true);
        var zip = Path.Combine(source.Root, "questions.zip");
        ZipFile.CreateFromDirectory(source.Source, zip);
        var output = Path.Combine(source.Root, "reviewed");
        var preview = await RunCli(server.Address, ["import", "preview", zip, "--exam", source.Name, "--output", output]);
        Assert.True(preview.Exit == 0, preview.Output + preview.Error);
        Assert.Contains("尚未发布", preview.Output);
        Assert.Equal(0, server.Uploads);
        Assert.Equal(0L, await server.ResourceCount());
        Assert.True(File.Exists(Path.Combine(output, "preview.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(output, "compiled"), "*.pdf", SearchOption.AllDirectories));
        var unconfirmed = await RunCli(server.Address, ["--error-format", "json", "import", "publish", output]);
        Assert.Equal(2, unconfirmed.Exit);
        using var rejection = JsonDocument.Parse(unconfirmed.Error);
        Assert.Equal("INVALID_INPUT", rejection.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, server.Uploads);
        var published = await RunCli(server.Address, ["import", "publish", output, "--confirm"]);
        Assert.True(published.Exit == 0, published.Output + published.Error);
        Assert.Contains("正式发布成功", published.Output);
        var replay = await RunCli(server.Address, ["--error-format", "json", "import", "publish", output, "--confirm"]);
        Assert.True(replay.Exit == 0, replay.Output + replay.Error);
        Assert.Equal(2, server.Uploads);
        await server.AssertPublishedAndPrivate(server.Versions.GetExam(server.ExamId).Value!);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    [Trait("Category", "Latex")]
    public async Task RealCli_RetrieveEditCompilePublishRunningRevision_AndReplayWithoutAnotherVersion()
    {
        using var source = new SyntheticImportFixture();
        await using var server = await ServerFixture.Create(database.DataSource, source, network: true);
        var initial = Path.Combine(source.Root, "initial-preview");
        var preview = await RunCli(server.Address, ["import", "preview", source.Source, "--exam", source.Name, "--output", initial]);
        Assert.True(preview.Exit == 0, preview.Output + preview.Error);
        var first = await RunCli(server.Address, ["import", "publish", initial, "--confirm"]);
        Assert.True(first.Exit == 0, first.Output + first.Error);
        var original = await new PostgresPaperMaterialStore(database.DataSource).FindAsync(server.ExamId, 1);
        Assert.NotNull(original);

        await server.Scalar("UPDATE exam.exam_entry_access SET instructions_ready=true WHERE exam_id=$1", server.ExamId);
        var now = DateTimeOffset.UtcNow;
        var starts = now.AddSeconds(1);
        Assert.True(server.Versions.UpdateSchedule(new(server.ExamId, 1, new(starts, now.AddHours(2)),
            now, Guid.NewGuid().ToString("N"))).Succeeded);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var started = server.Versions.ExecuteScheduleTrigger(new(server.ExamId, 2, ScheduleTriggerKind.Start,
            DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));
        Assert.True(started.Succeeded, started.Error);
        Assert.Equal(ExamLifecycle.Running, server.Versions.GetExam(server.ExamId).Value!.LifecycleState);

        var material = Path.Combine(source.Root, "retrieved-materials");
        var retrieved = await RunCli(server.Address, ["import", "materials", source.Name, "--output", material]);
        Assert.True(retrieved.Exit == 0, retrieved.Output + retrieved.Error);
        // Revise answers and all scoring branches without any TeX compilation or paper publication.
        var manifestPath = Path.Combine(material, "manifest.json");
        var answerPath = Path.Combine(material, "server-only", "answer-key.json");
        var manifestJson = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifestJson["subjects"]![0]!["maxScore"] = "3";
        manifestJson["groups"]![0]!["maxScore"] = "3";
        manifestJson["groups"]![0]!["scoring"]!["oneCorrect"]!["correct"] = "3";
        await File.WriteAllTextAsync(manifestPath, manifestJson.ToJsonString());
        var answerJson = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(answerPath))!;
        answerJson["answers"]![0]!["correctOptionIds"] = new System.Text.Json.Nodes.JsonArray("B");
        await File.WriteAllTextAsync(answerPath, answerJson.ToJsonString());
        var rulePreview = Path.Combine(source.Root, "rule-preview");
        var competing = Path.Combine(source.Root, "competing-rule-preview");
        foreach (var output in new[] { rulePreview, competing })
        {
            var checkedRules = await RunCli(server.Address, ["grading", "revise", source.Name, "--package", material,
                "--reason", "合成答案及分值纠错", "--output", output]);
            Assert.True(checkedRules.Exit == 0, checkedRules.Output + checkedRules.Error);
            Assert.DoesNotContain("correctOptionIds", checkedRules.Output);
            Assert.True(File.Exists(Path.Combine(output, "preview.md")));
        }
        Assert.Equal(1, server.Versions.GetExam(server.ExamId).Value!.Versions.GradingVersion);
        var unconfirmedRules = await RunCli(server.Address, ["grading", "publish", rulePreview]);
        Assert.Equal(2, unconfirmedRules.Exit);
        foreach (var attempt in Enumerable.Range(0, 2))
        {
            var publishedRules = await RunCli(server.Address, ["grading", "publish", rulePreview, "--confirm"]);
            Assert.True(publishedRules.Exit == 0, publishedRules.Output + publishedRules.Error);
            Assert.Contains("规则发布成功：规则版本 2", publishedRules.Error);
            Assert.Contains("结果就绪", publishedRules.Error);
        }
        var conflict = await RunCli(server.Address, ["grading", "publish", competing, "--confirm"]);
        Assert.Equal(4, conflict.Exit);
        var afterRules = server.Versions.GetExam(server.ExamId).Value!;
        Assert.Equal(1, afterRules.Versions.PaperVersion);
        Assert.Equal(2, afterRules.Versions.GradingVersion);
        Assert.Equal(1L, await server.ResourceCount());
        var status = await RunCli(server.Address, ["grading", "status", source.Name]);
        Assert.True(status.Exit == 0, status.Output + status.Error);
        Assert.Contains("结果就绪", status.Error);

        // A later paper revision must use the current answer/score working copy, not restore the old archive's rules.
        material = Path.Combine(source.Root, "materials-with-current-rules");
        retrieved = await RunCli(server.Address, ["import", "materials", source.Name, "--output", material]);
        Assert.True(retrieved.Exit == 0, retrieved.Output + retrieved.Error);
        var latestSource = await new PostgresGradingInputs(database.DataSource).LoadSourceAsync(server.ExamId);
        Assert.NotNull(latestSource);
        Assert.Equal(2, latestSource.GradingVersion);
        Assert.True(PaperRevisionMaterials.EquivalentGrading(await File.ReadAllTextAsync(Path.Combine(material, "manifest.json")),
            await File.ReadAllTextAsync(Path.Combine(material, "server-only", "answer-key.json")), latestSource.ManifestJson, latestSource.AnswerKeyJson));
        var question = Directory.EnumerateFiles(Path.Combine(material, "questions"), "*.tex").Single();
        await File.AppendAllTextAsync(question, "\n% synthetic revised source, same question and option identities\n");
        var revised = Path.Combine(source.Root, "revision-preview");
        var checkedRevision = await RunCli(server.Address, ["import", "preview", material, "--exam", source.Name,
            "--output", revised, "--reason", "合成开考后纠错"]);
        Assert.True(checkedRevision.Exit == 0, checkedRevision.Output + checkedRevision.Error);
        foreach (var attempt in Enumerable.Range(0, 2))
        {
            var published = await RunCli(server.Address, ["import", "publish", revised, "--confirm"]);
            Assert.True(published.Exit == 0, published.Output + published.Error);
        }
        var current = server.Versions.GetExam(server.ExamId).Value!;
        Assert.Equal(2, current.Versions.PaperVersion);
        Assert.Equal(3, current.Versions.GradingVersion);
        var finalRules = await new PostgresGradingInputs(database.DataSource).LoadSourceAsync(server.ExamId);
        Assert.True(PaperRevisionMaterials.EquivalentGrading(latestSource.ManifestJson, latestSource.AnswerKeyJson,
            finalRules!.ManifestJson, finalRules.AnswerKeyJson));
        Assert.Equal(["q1"], current.Paper!.ChangedQuestionIds);
        Assert.Equal(2L, await server.ResourceCount());
        Assert.Equal(2L, await server.Scalar("SELECT count(*) FROM exam.paper_materials WHERE exam_id=$1", server.ExamId));
        var old = await new PostgresPaperMaterialStore(database.DataSource).FindAsync(server.ExamId, 1);
        Assert.Equal(original.Archive, old!.Archive);
    }

    private static async Task<(int Exit, string Output, string Error)> RunCli(string address, string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var assembly = Path.Combine(root!.FullName, "src", "Exam.Cli", "bin", "Release", "net10.0", "Exam.Cli.dll");
        Assert.True(File.Exists(assembly), "Build the current Release CLI before this test.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assembly);
        foreach (var value in arguments) start.ArgumentList.Add(value);
        start.Environment["EXAM_SERVER_URL"] = address;
        start.Environment["EXAM_OPERATOR_TOKEN"] = "synthetic-session";
        start.Environment["EXAM_ALLOW_INSECURE_LOOPBACK"] = "true";
        start.Environment.Remove("EXAM_DEVICE_ID");
        start.Environment.Remove("EXAM_DEVICE_CREDENTIAL");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly X509Certificate2 _certificate;
        private readonly WebApplication _app;
        private readonly HttpClient _http;
        private readonly ManagedNodeEncryptionPolicy _policy = new(new HashSet<string>(["node-a"], StringComparer.Ordinal));
        private int _uploads;
        public int Uploads => _uploads;
        public string Address { get; }
        public string ExamId { get; }
        public PostgresExamCatalog Catalog { get; }
        public PostgresExamVersioningService Versions { get; }
        public HttpExamPublicationClient Api { get; }
        public UploadBoundary Boundary { get; }

        private ServerFixture(NpgsqlDataSource source, X509Certificate2 certificate, WebApplication app,
            HttpClient http, string address, string examId, PostgresExamCatalog catalog,
            PostgresExamVersioningService versions, UploadBoundary boundary)
        {
            _source = source; _certificate = certificate; _app = app; _http = http;
            Address = address; ExamId = examId; Catalog = catalog; Versions = versions; Boundary = boundary;
            Api = new HttpExamPublicationClient(http);
        }

        public static async Task<ServerFixture> Create(NpgsqlDataSource source, SyntheticImportFixture material, bool network = false)
        {
            var versions = new PostgresExamVersioningService(source);
            var catalog = new PostgresExamCatalog(source, versions, TimeProvider.System);
            var now = DateTimeOffset.UtcNow;
            var detail = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), material.Name,
                new(now.AddHours(2), now.AddHours(3)), material.Settings), "synthetic-operator");
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=synthetic-import-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
            var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>(["node-a"], StringComparer.Ordinal));
            var publication = new PostgresBundlePublicationService(versions, new(source, policy), new Unwrapper(certificate), policy,
                new PaperMaterialsVerifier(new Unwrapper(certificate), policy, new PostgresPaperMaterialStore(source)),
                new Certificates(certificate));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ServerIdentityOptions.SectionName}:ServerId"] = "synthetic-server",
                [$"{ServerIdentityOptions.SectionName}:NodeId"] = "node-a",
                [$"{ServerIdentityOptions.SectionName}:ServerEpoch"] = "1"
            });
            if (network) builder.WebHost.UseUrls("http://127.0.0.1:0"); else builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(publication);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IExamVersioningService>(versions);
            builder.Services.AddSingleton<Exam.Domain.Scoring.IGradingInputs>(new PostgresGradingInputs(source));
            builder.Services.AddSingleton(new GradingStatusService(new PostgresGradingInputs(source), new PostgresGradingScope(source)));
            builder.Services.AddSingleton(new PostgresPaperMaterialStore(source));
            builder.Services.AddSingleton<PublicationUploadGate>();
            // 发布端点提交后会及时安排一轮重算：夹具按生产装配方式提供同一实现。
            builder.Services.AddSingleton(new GradingRecalculationTrigger(new PostgresGradingInputs(source),
                new RecalculationScheduler(new PostgresGradingScope(source), new PostgresRecalculationQueue(source)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GradingRecalculationTrigger>.Instance));
            var app = builder.Build();
            ServerFixture? fixture = null;
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.Authorization != "Bearer synthetic-session")
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new ApiEnvelope<object>("synthetic-server", "node-a", 1,
                        "synthetic", null, new("UNAUTHORIZED", "Synthetic session required.")));
                    return;
                }
                context.Items["Exam.AuthenticatedOperatorId"] = "synthetic-operator";
                if (context.Request.Path.Value!.EndsWith("/publications/upload", StringComparison.Ordinal))
                    Interlocked.Increment(ref fixture!._uploads);
                await next(context);
            });
            static IResult Envelope<T>(T result) => Results.Json(new ApiEnvelope<T>("synthetic-server", "node-a", 1,
                "synthetic-read", result, null), JsonDefaults.Create());
            var group = app.MapGroup("/api/v1");
            group.MapGet("/exams", async () => Envelope(await catalog.ListAsync()));
            group.MapGet("/exams/{examId}", async (string examId) => Envelope(await catalog.GetAsync(examId)));
            group.MapVersioningApi();
            group.MapGradingApi();
            group.MapGet("/operators/me", () => Envelope(new Exam.Contracts.Operators.OperatorAccount(
                "synthetic-operator", "synthetic", Exam.Contracts.Operators.OperatorStatus.Active, false, now, null)));
            group.MapPublicationUploadApi();
            await app.StartAsync();
            var address = network ? app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()
                : "http://localhost";
            var boundary = new UploadBoundary { InnerHandler = network ? new HttpClientHandler { AllowAutoRedirect = false } : app.GetTestServer().CreateHandler() };
            var http = new HttpClient(boundary) { BaseAddress = new Uri(address + "/"), Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-session");
            fixture = new(source, certificate, app, http, address, detail.Summary.ExamId, catalog, versions, boundary);
            return fixture;
        }

        public Task<object?> Scalar(string sql, params object[] values) => ScalarCore(sql, values);
        private async Task<object?> ScalarCore(string sql, object[] values)
        {
            await using var command = _source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteScalarAsync();
        }
        public async Task<long> ResourceCount() => (long)(await Scalar(
            "SELECT count(*) FROM exam.versioned_resources WHERE exam_id=$1", ExamId))!;

        public async Task AssertPublishedAndPrivate(ExamVersionSnapshot result)
        {
            Assert.Equal(ExamId, result.ExamId);
            Assert.Equal(1, result.Versions.PaperVersion);
            Assert.Equal(1, result.Versions.GradingVersion);
            Assert.Equal(1L, await ResourceCount());
            Assert.Equal(3L, await Scalar("SELECT count(*) FROM exam.version_events WHERE exam_id=$1", ExamId));
            Assert.Equal(true, await Scalar("SELECT package_ready FROM exam.exam_entry_access WHERE exam_id=$1", ExamId));
            var grading = await new PostgresGradingInputs(_source).LoadRulesAsync(ExamId);
            Assert.NotNull(grading);
            Assert.Equal(1, grading.GradingVersion);
            var stored = await new PostgresVersionedResourceStore(_source, _policy).FindByIdAsync(result.Paper!.ArtifactId);
            Assert.NotNull(stored);
            Assert.Equal("paper-package", stored.ResourceKind);
            var key = PackageEncryptionService.UnwrapDataKey(stored.Envelope, "node-a", _certificate);
            using var encrypted = new MemoryStream(stored.EncryptedContent);
            using var plain = new MemoryStream();
            try
            {
                await PackageCiphertext.DecryptAsync(encrypted, plain, new(stored.ResourceId, 1, stored.Envelope.ChunkSize,
                    stored.Envelope.NoncePrefix, key));
                var bytes = plain.ToArray();
                var manifest = StudentPaperPackage.ReadManifest(bytes, ExamId, 1);
                Assert.Equal("subject-current", manifest.Questions.Single().Structure.SubjectId);
                Assert.Equal("group-current", manifest.Questions.Single().Structure.GroupId);
                var json = JsonSerializer.Serialize(manifest, ImportJson.Create());
                Assert.DoesNotContain("correctOptionIds", json);
                Assert.DoesNotContain("scoring", json);
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                Assert.Contains(archive.Entries, e => e.FullName == OptionRegionArtifact.FileName);
                Assert.DoesNotContain(archive.Entries, e => e.FullName.EndsWith(".tex") || e.FullName.EndsWith(".txt") ||
                    e.FullName.Contains("answer", StringComparison.OrdinalIgnoreCase));
                CryptographicOperations.ZeroMemory(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _certificate.Dispose();
        }
    }

    private sealed class UploadBoundary : DelegatingHandler
    {
        public bool DropReceiptOnce { get; set; }
        public Func<Task>? BeforeUpload { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var upload = request.RequestUri!.AbsolutePath.EndsWith("/publications/upload", StringComparison.Ordinal);
            if (upload && BeforeUpload is { } before) { BeforeUpload = null; await before(); }
            var result = await base.SendAsync(request, cancellationToken);
            if (upload && DropReceiptOnce && result.IsSuccessStatusCode)
            { DropReceiptOnce = false; result.Dispose(); throw new HttpRequestException("Synthetic lost official receipt."); }
            return result;
        }
    }
    private sealed class Unwrapper(X509Certificate2 certificate) : IPackageKeyUnwrapper
    { public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, "node-a", certificate); }
    private sealed class Certificates(X509Certificate2 certificate) : IPublicationCertificateProvider
    { public PublicationCertificateLease Acquire() => new([new("node-a", X509CertificateLoader.LoadCertificate(certificate.RawData))]); }
}
