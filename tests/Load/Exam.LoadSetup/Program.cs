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
var connectionOptions = new NpgsqlConnectionStringBuilder(source)
{
    Database = database, Pooling = true, MinPoolSize = 0, MaxPoolSize = 16,
    IncludeErrorDetail = false, LogParameters = false
};
await using var dataSource = new PostgresConnectionOptions(connectionOptions.ConnectionString).CreateDataSource();
await new PostgresMigrationRunner(dataSource).MigrateAsync();
var examId = $"synthetic-{Guid.NewGuid():N}";
var serverId = "local-capacity-server-" + database[10..];
var nodeId = "local-capacity-node-" + database[10..];
// 合成考务账号：隔离夹具数据库中的第一个账号即管理员；口令随机生成且不写出，只保存登录会话令牌。
var operatorAccounts = new Exam.Infrastructure.Postgres.Operators.PostgresOperatorAccounts(dataSource);
var operatorPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
await operatorAccounts.RegisterAsync(new Exam.Contracts.Operators.RegisterOperatorRequest(
    "capacity-test-operator", "合成容量测试考务", operatorPassword));
var operatorToken = (await operatorAccounts.LoginAsync(new Exam.Contracts.Operators.LoginOperatorRequest(
    "capacity-test-operator", operatorPassword))).Token;
var resourceId = $"synthetic-package-{Guid.NewGuid():N}";
using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=local-capacity-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
var policy = new ManagedNodeEncryptionPolicy(new HashSet<string>([nodeId], StringComparer.Ordinal));
var encryption = new PackageEncryptionService(65536, policy);
await using var ciphertext = new MemoryStream();
var envelope = await encryption.EncryptAsync(new MemoryStream(RandomNumberGenerator.GetBytes(1024 * 1024)),
    ciphertext, resourceId, 1, [new(nodeId, certificate)]);
await new PostgresVersionedResourceStore(dataSource, policy).SaveAsync(new VersionedEncryptedResource(
    resourceId, examId, "paper-package", 1, ciphertext.ToArray(), envelope, DateTimeOffset.UtcNow, "capacity-test-operator"));
var protector = new Pbkdf2DeviceCredentialProtector();
var store = new PostgresSubmissionStore(dataSource);
var sessions = new List<object>();
await using var connection = await dataSource.OpenConnectionAsync();
await using (var seedExam = new NpgsqlCommand("""
    INSERT INTO exam.exam_entry_access
      (exam_id, scheduled_start, scheduled_end, lifecycle, package_ready, instructions_ready)
    VALUES (@exam, clock_timestamp() - interval '1 minute', clock_timestamp() + interval '6 hours', 'running', false, false);
    """, connection))
{
    seedExam.Parameters.AddWithValue("exam", examId);
    await seedExam.ExecuteNonQueryAsync();
}
for (var index = 1; index <= count; index++)
{
    var deviceId = $"load-device-{index:D3}";
    var candidateId = $"load-candidate-{index:D3}";
    var participationId = $"load-participation-{index:D3}";
    var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    await using (var seed = new NpgsqlCommand("""
        INSERT INTO exam.activation_devices
          (device_id, client_number, activation_date, activation_status, protected_credential,
           credential_version, created_at, version)
        VALUES (@device, @number, CURRENT_DATE, 'active', @verifier, 1, clock_timestamp(), 1);
        INSERT INTO exam.candidate_roster_entries
          (exam_id, candidate_id, candidate_name, class_text, admission_no)
        VALUES (@exam, @candidate, '合成容量考生', '01', @admission);
        """, connection))
    {
        seed.Parameters.AddWithValue("device", deviceId);
        seed.Parameters.AddWithValue("number", $"20260922{index:D3}");
        seed.Parameters.AddWithValue("verifier", protector.Protect(credential));
        seed.Parameters.AddWithValue("exam", examId);
        seed.Parameters.AddWithValue("candidate", candidateId);
        seed.Parameters.AddWithValue("admission", $"{index:D6}");
        await seed.ExecuteNonQueryAsync();
    }
    await store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
        examId, candidateId, participationId, 1, deviceId, 1,
        ParticipationBusinessState.Answering, 1, 1));
    sessions.Add(new { examId, candidateId, participationId, entryVersion = 1,
        deviceId, authorizationVersion = 1, paperVersion = 1, credential, initialAnswerVersion = 0,
        resourcePath = $"/api/v1/resources/{resourceId}?participationId={participationId}&entryVersion=1&deviceId={deviceId}&authorizationVersion=1",
        expectedResourceBytes = ciphertext.Length });
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
        "Resource is 1 MiB encrypted synthetic bytes, not a compiled paper or maximum actual package; instructions/decryption are not covered.",
        "Credentials and local-server.json must not be committed or uploaded." }
}, json));
Console.WriteLine($"Created {count} synthetic identities in a NEW isolated database. Private configuration: {output}");
return 0;
