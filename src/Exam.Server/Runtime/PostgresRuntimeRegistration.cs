using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Application.Participation;
using Exam.Application.ServiceManagement;
using Exam.Application.Submissions;
using Exam.Application.Versioning;
using Exam.Contracts.Commands;
using Exam.Contracts.Versioning;
using Exam.Domain.Commands;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Activation;
using Exam.Infrastructure.Postgres.Commands;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.Infrastructure.Postgres.Participation;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;
using Exam.Server.Realtime;
using Exam.Server.Capacity;
using Npgsql;

namespace Exam.Server.Runtime;

public static class PostgresRuntimeRegistration
{
    public static NpgsqlDataSource AddPostgresExamRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        ServerIdentityOptions identity,
        bool allowUninitializedFailoverState)
    {
        var connectionString = configuration.GetConnectionString("Exam");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "缺少 ConnectionStrings:Exam。生产服务不会退回内存存储。请通过受保护的环境配置提供 PostgreSQL 连接。");
        }

        if (!identity.IsValid())
        {
            throw new InvalidOperationException("ExamServer 的 serverId、nodeId 和 serverEpoch 必须有效。");
        }

        var operatorToken = configuration[$"{ExamApiSecurityOptions.SectionName}:OperatorToken"];
        var operatorId = configuration[$"{ExamApiSecurityOptions.SectionName}:OperatorId"];
        if (string.IsNullOrWhiteSpace(operatorToken) || operatorToken.Length < 32)
        {
            throw new InvalidOperationException(
                "ExamSecurity:OperatorToken 必须通过受保护配置提供，且至少 32 个字符；不会使用仓库默认口令。");
        }

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            throw new InvalidOperationException(
                "ExamSecurity:OperatorId 必须通过受保护配置提供，用于不可伪造的审计身份。");
        }

        services.AddSingleton(new ExamApiSecurityOptions(operatorToken, operatorId));

        var timeout = configuration.GetValue("Database:CommandTimeoutSeconds", 30);
        var connectionOptions = new PostgresConnectionOptions(connectionString, timeout,
            MaximumPoolSize: configuration.GetValue<int?>("Database:MaximumPoolSize"),
            MinimumPoolSize: configuration.GetValue<int?>("Database:MinimumPoolSize"),
            ConnectionTimeoutSeconds: configuration.GetValue("Database:ConnectionTimeoutSeconds", 15));
        var capacity = DatabaseCapacityOptions.Read(configuration, connectionOptions.BuildConnectionSettings().MaxPoolSize,
            production: !allowUninitializedFailoverState);
        services.AddExamCapacity(configuration, capacity);
        var dataSource = connectionOptions.CreateDataSource();
        services.AddSingleton(dataSource);
        services.AddSingleton<PostgresMigrationRunner>();
        services.AddSingleton<PostgresReadinessProbe>();
        services.AddSingleton(new ActiveNodeGuard(dataSource, identity, allowUninitializedFailoverState));
        services.AddSingleton<PostgresFailoverStateStore>();

        services.AddSingleton<IActivationPersistence, PostgresActivationPersistence>();
        services.AddSingleton<IDeviceCredentialProtector, Pbkdf2DeviceCredentialProtector>();
        services.AddSingleton<ActivationService>();
        services.AddSingleton<IDeviceCredentialValidator>(provider =>
            provider.GetRequiredService<ActivationService>());

        services.AddSingleton<IParticipationPersistence, PostgresParticipationPersistence>();
        services.AddSingleton<ICandidateRosterDirectory, PostgresCandidateRosterDirectory>();
        services.AddSingleton<IExamEntryDirectory, PostgresExamEntryDirectory>();
        services.AddSingleton<ParticipationService>();

        services.AddSingleton(new ServerNodeEnvelope(
            identity.ServerId,
            identity.NodeId,
            identity.ServerEpoch));
        services.AddSingleton<ICommandEventStore, PostgresCommandEventStore>();
        services.AddSingleton<IParticipationAccessReader, PostgresParticipationAccessReader>();
        services.AddSingleton<ReliableCommandService>();

        services.AddSingleton<ISubmissionStore, PostgresSubmissionStore>();
        services.AddSingleton<IGradingTaskQueue, PostgresGradingTaskQueue>();
        services.AddSingleton<PostgresCollectionCompletionStore>();
        services.AddSingleton<ICollectionCompletionStore>(provider => provider.GetRequiredService<PostgresCollectionCompletionStore>());
        services.AddSingleton<IAuthoritativeCollectionCompletionStore>(provider => provider.GetRequiredService<PostgresCollectionCompletionStore>());
        var maxConcurrentWrites = configuration.GetValue("Database:MaxConcurrentWrites", 32);
        if (maxConcurrentWrites is < 1 or > 1024)
            throw new InvalidOperationException("Database:MaxConcurrentWrites 必须在 1 至 1024 之间。");
        services.AddSingleton(provider => new SubmissionService(
            provider.GetRequiredService<ISubmissionStore>(), provider.GetRequiredService<IGradingTaskQueue>(),
            maxConcurrentWrites, provider.GetRequiredService<TimeProvider>(),
            accessReader: provider.GetRequiredService<IParticipationAccessReader>()));
        services.AddSingleton<CollectionCompletionService>();
        services.AddHostedService<CollectionCompletionWorker>();
        services.AddSingleton<IExamVersioningService, PostgresExamVersioningService>();
        services.AddSingleton<PostgresScheduleProcessor>();
        services.AddHostedService<PostgresScheduleWorker>();
        var requiredNodeIds = configuration.GetSection("HighAvailability:RequiredNodeIds").Get<string[]>()
            ?? [identity.NodeId];
        if (requiredNodeIds.Any(string.IsNullOrWhiteSpace) ||
            requiredNodeIds.Distinct(StringComparer.Ordinal).Count() != requiredNodeIds.Length ||
            (!allowUninitializedFailoverState && requiredNodeIds.Length < 2))
        {
            throw new InvalidOperationException(
                "HighAvailability:RequiredNodeIds 必须是不重复的节点列表，生产环境至少配置两个节点。");
        }

        var encryptionPolicy = new ManagedNodeEncryptionPolicy(
            requiredNodeIds.ToHashSet(StringComparer.Ordinal));
        services.AddSingleton(encryptionPolicy);
        services.AddSingleton<PackageEncryptionService>();
        services.AddSingleton<PostgresVersionedResourceStore>();
        services.AddSingleton(new PackageUnlockIdentity(identity.ServerId, identity.NodeId, identity.ServerEpoch));
        services.AddSingleton<IPackageKeyUnwrapper>(new CertificateStorePackageKeyUnwrapper(
            identity.NodeId,
            configuration["PackageSecurity:CertificateThumbprint"] ?? string.Empty,
            System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine));
        services.AddSingleton<PostgresPackageUnlockService>();
        services.AddSingleton<Exam.Infrastructure.Postgres.Student.PostgresStudentDirectory>();
        services.AddSingleton<Exam.Infrastructure.Postgres.Student.PostgresStudentExamMetadataService>();

        if (configuration.GetValue("Backup:Enabled", false))
        {
            var backupOptions = new PostgresBackupOptions(
                Required(configuration, "Backup:PostgresBinDirectory"),
                connectionString,
                Required(configuration, "Backup:AllowedBackupRoot"),
                Required(configuration, "Backup:RestoreStagingRoot"),
                Required(configuration, "Backup:VersionedResourcesDirectory"),
                Required(configuration, "Backup:ProtectedConfigurationDirectory"),
                TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60)));
            services.AddSingleton(backupOptions);
            services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
            services.AddSingleton<IBackupCoordinator>(provider => new PostgresBackupCoordinator(
                provider.GetRequiredService<PostgresBackupOptions>(),
                provider.GetRequiredService<IExternalProcessRunner>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<NpgsqlDataSource>()));
        }

        var maxConnections = configuration.GetValue("Realtime:MaxConnections", 1024);
        var maxConcurrentDispatches = configuration.GetValue("Realtime:MaxConcurrentDispatches", 32);
        if (maxConnections < 300 || maxConcurrentDispatches is < 1 or > 256)
        {
            throw new InvalidOperationException(
                "Realtime:MaxConnections 至少为 300，MaxConcurrentDispatches 必须在 1 至 256 之间。");
        }

        services.AddSingleton(new RealtimeDispatchOptions(maxConnections, maxConcurrentDispatches));
        services.AddSingleton<RealtimeDeviceAuthorizer>();
        services.AddSingleton<RealtimeConnectionRegistry>();
        services.AddHostedService<ReliableCommandDispatcher>();
        return dataSource;
    }

    private static string Required(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"启用备份时必须配置 {key}。")
            : configuration[key]!;
}

public sealed class PostgresReadinessProbe(
    NpgsqlDataSource dataSource,
    PostgresMigrationRunner migrations,
    ActiveNodeGuard activeNode,
    DatabaseCapacityProbe? capacity = null)
{
    public async Task<(bool Ready, string? Reason)> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (capacity is not null && await capacity.CheckAsync(cancellationToken) is { } capacityFailure)
                return (false, capacityFailure);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT migration_name FROM exam.schema_migrations;",
                connection);
            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied.Add(reader.GetString(0));
                }
            }

            var missing = migrations.GetRequiredMigrationNames().Where(name => !applied.Contains(name)).ToArray();
            if (missing.Length != 0)
            {
                return (false, $"数据库迁移未完成：{string.Join(", ", missing)}");
            }

            var active = await activeNode.CheckAsync(cancellationToken);
            return active.Allowed ? (true, null) : (false, active.Reason);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return (false, "PostgreSQL 不可用或连接等待超时，请检查数据库服务和受保护配置。");
        }
    }
}
