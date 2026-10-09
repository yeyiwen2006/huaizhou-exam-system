using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Server.Api;
using Exam.Server.Diagnostics;
using Exam.Server.Realtime;
using Exam.Server.Runtime;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Separate development executable. Exam.Server keeps its production deployment requirements.
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
return await LocalEntry.RunAsync(args);

internal static class LocalEntry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Nodes = ["local-node-a", "local-node-b"];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--management", var managementConfiguration, "--postgres-bin", var postgresBin])
            return await LocalManagementHost.RunAsync(managementConfiguration, postgresBin);
        if (args is ["--prepare", var prepare])
        {
            var (directory, settings) = ReadSettings(prepare);
            for (var index = 0; index < Nodes.Length; index++)
            {
                EnsureCertificate(directory, settings, index);
                using var publicCertificate = LoadCertificate(directory, settings, index);
                File.WriteAllText(Path.Combine(directory, Nodes[index] + ".cer"), publicCertificate.ExportCertificatePem());
            }
            using var certificate = LoadCertificate(directory, settings, 0);
            File.WriteAllText(Path.Combine(directory, "student-server.json"), JsonSerializer.Serialize(new
            {
                formatVersion = 1, host = "127.0.0.1", port = settings.HttpsPort,
                certificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()
            }, Json));
            return 0;
        }
        if (args is not ["--config", var configuration, "--node", var node] || node is not ("a" or "b"))
        {
            Console.Error.WriteLine("使用 start-local.cmd 启动本机调试环境。");
            return 2;
        }
        var (root, local) = ReadSettings(configuration);
        var indexOfNode = node == "a" ? 0 : 1;
        using var first = LoadCertificate(root, local, 0);
        using var second = LoadCertificate(root, local, 1);
        var certificates = new Dictionary<string, X509Certificate2>
        {
            [Nodes[0]] = first, [Nodes[1]] = second
        };
        var certificateForNode = certificates[Nodes[indexOfNode]];
        var identity = new ServerIdentityOptions
        {
            ServerId = local.ServerId, NodeId = Nodes[indexOfNode], ServerEpoch = 1
        };
        var values = new Dictionary<string, string?>
        {
            ["ExamServer:ServerId"] = identity.ServerId,
            ["ExamServer:NodeId"] = identity.NodeId,
            ["ExamServer:ServerEpoch"] = "1",
            ["ConnectionStrings:Exam"] = ConnectionString(local),
            ["Database:MaximumPoolSize"] = "16", ["Database:MinimumPoolSize"] = "0",
            ["Database:MaxConcurrentWrites"] = "8",
            ["Logging:LogLevel:Default"] = "Warning"
        };
        for (var index = 0; index < Nodes.Length; index++)
        {
            values[$"HighAvailability:RequiredNodeIds:{index}"] = Nodes[index];
            values[$"StudentFailover:Nodes:{index}:Host"] = $"127.0.0.{index + 1}";
            values[$"StudentFailover:Nodes:{index}:Port"] = local.HttpsPort.ToString();
            values[$"StudentFailover:Nodes:{index}:NodeId"] = Nodes[index];
            values[$"StudentFailover:Nodes:{index}:CertificateSha256"] = certificates[Nodes[index]]
                .GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = "Development", ContentRootPath = root
        });
        // Do not inherit real deployment configuration or connection strings from the shell.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.ConfigureKestrel(options =>
        {
            var address = IPAddress.Parse($"127.0.0.{indexOfNode + 1}");
            options.Listen(address, local.HttpsPort, endpoint => endpoint.UseHttps(certificateForNode));
            if (indexOfNode == 0) options.Listen(IPAddress.Loopback, local.AdminPort);
        });
        builder.Services.Configure<ServerIdentityOptions>(builder.Configuration.GetSection(ServerIdentityOptions.SectionName));
        builder.Services.AddExamServerTransport();
        builder.Services.AddPostgresExamRuntime(builder.Configuration, identity, allowUninitializedFailoverState: true);
        builder.Services.RemoveAll<IPackageKeyUnwrapper>();
        builder.Services.AddSingleton<IPackageKeyUnwrapper>(new LocalUnwrapper(identity.NodeId, certificateForNode));
        builder.Services.RemoveAll<IPublicationCertificateProvider>();
        builder.Services.AddSingleton<IPublicationCertificateProvider>(new LocalCertificates(certificates));
        await using var app = builder.Build();
        // The launcher starts a before b, avoiding concurrent schema migrations.
        if (indexOfNode == 0)
        {
            try
            {
                await app.Services.GetRequiredService<PostgresMigrationRunner>().MigrateAsync();
            }
            catch (PostgresMigrationBlockedException exception)
            {
                Console.Error.WriteLine(exception.Message);
                // Reserved startup status consumed by eng/development/start_local.py.
                return 3;
            }
        }
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.UseMiddleware<BusinessConfirmationMiddleware>();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", localDebug = true }));
        app.MapGet("/health/ready", async (PostgresReadinessProbe probe, BusinessRecoveryGuard recovery,
            CancellationToken cancellation) =>
        {
            var readiness = await probe.CheckAsync(cancellation);
            return readiness.Ready && recovery.Snapshot.Ready
                ? Results.Ok(new { status = "ready", localDebug = true, identity.ServerId, identity.NodeId, identity.ServerEpoch })
                : Results.Json(new { status = "recovering", localDebug = true, reason = readiness.Reason,
                    checks = recovery.Snapshot.Checks }, statusCode: 503);
        });
        app.MapExamApi();
        app.MapExamDiagnostics();
        app.MapHub<ExamEventHub>("/hubs/exam-events");
        await app.RunAsync();
        return 0;
    }

    internal static (string Directory, LocalSettings Settings) ReadSettings(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ".owner.json")));
        if (marker.RootElement.GetProperty("kind").GetString() != "exam-local-debug" ||
            !Path.GetFileName(path).Equals("local-settings.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本机调试配置目录无效。");
        var settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException("缺少本机调试配置。");
        if (settings.DatabasePort is < 1024 or > 65535 || settings.HttpsPort is < 1024 or > 65535 ||
            settings.AdminPort is < 1024 or > 65535 ||
            new[] { settings.DatabasePort, settings.HttpsPort, settings.AdminPort }.Distinct().Count() != 3 ||
            settings.DatabasePassword.Length < 16 || settings.CertificatePassword.Length < 16 ||
            !settings.ServerId.StartsWith("local-debug-", StringComparison.Ordinal))
            throw new InvalidOperationException("本机调试端口或身份配置无效。");
        var servicePath = Path.Combine(directory, "management", "service.json");
        if (File.Exists(servicePath))
        {
            var service = JsonSerializer.Deserialize<Exam.Contracts.ServiceManagement.ServiceConfiguration>(File.ReadAllText(servicePath), Json)!;
            settings = settings with { HttpsPort = service.HttpsPort };
        }
        var selection = Path.Combine(directory, "management", "active-database.json");
        if (File.Exists(selection))
        {
            using var selected = JsonDocument.Parse(File.ReadAllText(selection));
            var name = selected.RootElement.GetProperty("databaseName").GetString()!;
            if (name != "exam_local_debug" && !(name.StartsWith("exam_local_restore_", StringComparison.Ordinal) &&
                name.Length <= 63 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new InvalidDataException("本机恢复数据库标识无效。");
            settings = settings with { DatabaseName = name };
        }
        return (directory, settings);
    }

    internal static string ConnectionString(LocalSettings settings, string? database = null) =>
        new Npgsql.NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = settings.DatabasePort,
            Database = database ?? settings.DatabaseName, Username = "postgres", Password = settings.DatabasePassword,
            SslMode = Npgsql.SslMode.Disable, Timeout = 5 }.ConnectionString;

    private static void EnsureCertificate(string root, LocalSettings local, int index)
    {
        var file = Path.Combine(root, Nodes[index] + ".pfx");
        if (File.Exists(file)) return;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Exam Local Debug {Nodes[index]}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse($"127.0.0.{index + 1}"));
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        File.WriteAllBytes(file, certificate.Export(X509ContentType.Pfx, local.CertificatePassword));
    }

    internal static X509Certificate2 LoadCertificate(string root, LocalSettings local, int index) =>
        X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root, Nodes[index] + ".pfx"),
            // Windows Schannel needs a user key container for the HTTPS server credential.
            // No certificate is installed in a trust store; the PFX remains in the local data directory.
            local.CertificatePassword, X509KeyStorageFlags.UserKeySet);

    private sealed class LocalCertificates(IReadOnlyDictionary<string, X509Certificate2> certificates) : IPublicationCertificateProvider
    {
        public PublicationCertificateLease Acquire() => new(certificates.Select(pair =>
            new NodeKeyRecipient(pair.Key, X509CertificateLoader.LoadCertificate(pair.Value.RawData))).ToArray());
    }

    private sealed class LocalUnwrapper(string node, X509Certificate2 certificate) : IPackageKeyUnwrapper
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, node, certificate);
    }

    internal sealed record LocalSettings(string ServerId, int DatabasePort, string DatabasePassword,
        int HttpsPort, int AdminPort, string CertificatePassword, int ManagementPort = 0,
        string DatabaseName = "exam_local_debug");
}
