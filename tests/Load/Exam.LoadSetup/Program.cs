using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Contracts.Common;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Versioning;
using Npgsql;

// Deliberately separate provisioning from measured traffic. Never seed an existing database.
if (args is ["--validate-fixture", var validationDirectory])
{
    LocalFixtureValidation.Validate(validationDirectory);
    await LocalFixtureValidation.RequireFreshDatabaseAsync(validationDirectory);
    Console.WriteLine("Isolated local fixture configuration validated.");
    return 0;
}
if (args is ["--audit", var fixtureDirectory, var reportPath])
    return await LocalCapacityAudit.RunAsync(fixtureDirectory, reportPath);
if (args is ["--cleanup-owned", var cleanupDirectory])
    return await FixtureOwnership.CleanupAsync(cleanupDirectory);
if (args.Length != 3 || args[0] != "--create-isolated" ||
    !int.TryParse(args[1], out var count) || count is < 1 or > 300)
{
    Console.Error.WriteLine("Usage: Exam.LoadSetup --create-isolated <1..300> <new-output-directory>. Requires EXAM_LOAD_POSTGRES; creates a NEW exam_load_* database only. Synthetic data only; this does not measure activation/login.");
    return 2;
}

var source = Environment.GetEnvironmentVariable("EXAM_LOAD_POSTGRES");
if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("EXAM_LOAD_POSTGRES is required.");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("Output must be a new directory.");
var adminOptions = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
if (adminOptions.Host is not ("127.0.0.1" or "localhost" or "::1"))
    throw new InvalidOperationException("Local provisioning only: PostgreSQL host must be loopback.");
var database = $"exam_load_{Guid.NewGuid():N}";
await using (var admin = new NpgsqlConnection(adminOptions.ConnectionString))
{
    await admin.OpenAsync();
    await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\" TEMPLATE template0 ENCODING 'UTF8'", admin);
    await create.ExecuteNonQueryAsync();
}
try
{
await FixtureOwnership.MarkAsync(output, adminOptions.ConnectionString, database);
var connectionOptions = new NpgsqlConnectionStringBuilder(source)
{
    Database = database, Pooling = true, MinPoolSize = 0, MaxPoolSize = 16,
    IncludeErrorDetail = false, LogParameters = false
};
await using var dataSource = new PostgresConnectionOptions(connectionOptions.ConnectionString).CreateDataSource();
await new PostgresMigrationRunner(dataSource).MigrateAsync();
var serverId = "local-capacity-server-" + database[10..];
var nodeId = "local-capacity-node-" + database[10..];
// 合成考务账号：隔离夹具数据库中的第一个账号即管理员；口令随机生成且不写出，只保存登录会话令牌。
var operatorAccounts = new Exam.Infrastructure.Postgres.Operators.PostgresOperatorAccounts(dataSource);
var operatorPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
await operatorAccounts.RegisterAsync(new Exam.Contracts.Operators.RegisterOperatorRequest(
    "capacity-test-operator", "合成容量测试考务", operatorPassword));
var operatorToken = (await operatorAccounts.LoginAsync(new Exam.Contracts.Operators.LoginOperatorRequest(
    "capacity-test-operator", operatorPassword))).Token;
var now = DateTimeOffset.UtcNow;
var versioning = new PostgresExamVersioningService(dataSource);
// A real catalog entry is required by the business CLI; a bare version stream is not an exam listing.
var catalog = new Exam.Infrastructure.Postgres.Exams.PostgresExamCatalog(
    dataSource, versioning, new FixtureTimeProvider(now.AddMinutes(-2)));
var detail = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), "合成负载考试",
    new(now.AddMinutes(-1), now.AddHours(6)),
    new([new("subject", "合成科目", "120",
        [new("group", "合成题组", "single", 1, 120, "120", false, new("1", "0", "0"), null)])])),
    "capacity-test-operator");
var examId = detail.Summary.ExamId;
var resourceId = $"synthetic-package-{Guid.NewGuid():N}";
using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=local-capacity-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
var certificatePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
await File.WriteAllBytesAsync(Path.Combine(output, "synthetic-node.pfx"), certificate.Export(X509ContentType.Pkcs12, certificatePassword));
await File.WriteAllTextAsync(Path.Combine(output, "synthetic-node.password"), certificatePassword);
var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>([nodeId], StringComparer.Ordinal));
var encryption = new PackageEncryptionService(65536, policy);
await using var ciphertext = new MemoryStream();
var paper = SyntheticFixturePaper.Build(examId);
var envelope = await encryption.EncryptAsync(new MemoryStream(paper.Bytes),
    ciphertext, resourceId, 1, [new(nodeId, certificate)]);
await new PostgresVersionedResourceStore(dataSource, policy).SaveAsync(new VersionedEncryptedResource(
    resourceId, examId, "paper-package", 1, ciphertext.ToArray(), envelope, DateTimeOffset.UtcNow, "capacity-test-operator"));
var protector = new Pbkdf2DeviceCredentialProtector();
var store = new PostgresSubmissionStore(dataSource);
var sessions = new List<object>();
await using var connection = await dataSource.OpenConnectionAsync();
Require(versioning.PublishPaper(new(examId, 0, resourceId, paper.Structure, [], new(true, true, true, true),
    "G", "预先构造的合成显示夹具，未执行真实编译与预览验收", now, "synthetic-paper")));
Require(versioning.PublishGradingRules(new(examId, 0, "synthetic-rules", new(true, true, true), "G", "合成计分夹具",
    now, "synthetic-grading", paper.ManifestJson, paper.AnswerKeyJson)));
await using (var seedExam = new NpgsqlCommand("""
    WITH prepared AS (UPDATE exam.exam_entry_access SET instructions_ready=true WHERE exam_id=$1 RETURNING exam_id)
    INSERT INTO exam.activation_daily_sequences VALUES (CURRENT_DATE, $2);
    """, connection))
{
    seedExam.Parameters.AddWithValue(examId);
    seedExam.Parameters.AddWithValue(count);
    await seedExam.ExecuteNonQueryAsync();
}
Require(versioning.ExecuteScheduleTrigger(new(examId, 1, ScheduleTriggerKind.Start, now, "synthetic-start")));
for (var index = 1; index <= count; index++)
{
    var deviceId = $"load-device-{index:D3}";
    var candidateId = $"load-candidate-{index:D3}";
    var participationId = $"load-participation-{index:D3}";
    var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    await using (var seed = new NpgsqlCommand("""
        WITH device AS (INSERT INTO exam.activation_devices
          (device_id, client_number, activation_date, activation_status, protected_credential,
           credential_version, created_at, version)
        VALUES ($1, to_char(CURRENT_DATE, 'YYYYMMDD') || $2, CURRENT_DATE, 'active', $3, 1, clock_timestamp(), 1) RETURNING device_id)
        INSERT INTO exam.candidate_roster_entries
          (exam_id, candidate_id, candidate_name, class_text, admission_no)
        VALUES ($4, $5, '合成容量考生', '01', $6);
        """, connection))
    {
        seed.Parameters.AddWithValue(deviceId);
        seed.Parameters.AddWithValue($"{index:D3}");
        seed.Parameters.AddWithValue(protector.Protect(credential));
        seed.Parameters.AddWithValue(examId);
        seed.Parameters.AddWithValue(candidateId);
        seed.Parameters.AddWithValue($"{index:D6}");
        await seed.ExecuteNonQueryAsync();
    }
    await store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
        examId, candidateId, participationId, 1, deviceId, 1,
        ParticipationBusinessState.Answering, 1, 1));
    sessions.Add(new { examId, candidateId, participationId, entryVersion = 1,
        deviceId, authorizationVersion = 1, paperVersion = 1, credential, initialAnswerVersion = 0,
        resourcePath = $"/api/v1/resources/{resourceId}?participationId={participationId}&entryVersion=1&deviceId={deviceId}&authorizationVersion=1",
        expectedResourceBytes = ciphertext.Length,
        expectedResourceSha256 = Convert.ToHexString(SHA256.HashData(ciphertext.ToArray())).ToLowerInvariant() });
}
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
await File.WriteAllTextAsync(Path.Combine(output, "sessions.json"), JsonSerializer.Serialize(new
{
    synthetic = true, serverId, serverEpoch = 1, questionCount = 120, operatorToken, sessions
}, json));
await FixtureConfiguration.WriteAsync(Path.Combine(output, "local-server.json"), new
{
    ConnectionStrings = new { Exam = connectionOptions.ConnectionString },
    ExamServer = new { ServerId = serverId, NodeId = nodeId, ServerEpoch = 1 },
    Database = new { ApplyMigrations = false },
    HighAvailability = new { RequiredNodeIds = new[] { nodeId } }
});
await File.WriteAllTextAsync(Path.Combine(output, "setup.json"), JsonSerializer.Serialize(new
{
    synthetic = true, database, examId, count, questionCount = 120,
    createdAt = DateTimeOffset.UtcNow,
    limitations = new[] { "Single local PostgreSQL; synchronous replication not enabled.",
        "Direct isolated fixture provisioning; activation/login are not measured.",
        "Resource is a valid encrypted 120-question synthetic display ZIP, not a compiled or maximum actual paper; real compilation and GUI preview are not covered.",
        "The test host provides an ephemeral synthetic certificate; production certificate-store deployment is not covered.",
        "Credentials and local-server.json must not be committed or uploaded." }
}, json));
Console.WriteLine($"Created {count} synthetic identities in a NEW isolated database. Private configuration: {output}");
return 0;
}
catch
{
    // The create statement above succeeded in this process; never infer ownership from a prefix.
    await FixtureOwnership.DropCreatedAsync(adminOptions.ConnectionString, database);
    throw;
}

static void Require<T>(VersioningResult<T> result)
{
    if (!result.Succeeded) throw new InvalidOperationException("Synthetic fixture publication failed: " + result.ErrorCode + ": " + result.Error);
}

internal sealed class FixtureTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
