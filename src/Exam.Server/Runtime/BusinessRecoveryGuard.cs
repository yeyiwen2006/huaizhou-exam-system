using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Server.Runtime;

public sealed record BusinessRecoverySnapshot(bool Ready, IReadOnlyList<StartupCheckResult> Checks, DateTimeOffset? VerifiedAt)
{
    public bool MaterialsReady { get; init; }
}

public sealed class BusinessRecoveryGuard
{
    private volatile BusinessRecoverySnapshot _snapshot = new(false, [], null);
    public BusinessRecoverySnapshot Snapshot => _snapshot;
    public void Observe(IReadOnlyList<StartupCheckResult> checks, DateTimeOffset verifiedAt)
    {
        var valid = checks.Count > 0 && checks.All(c => c is not null && !string.IsNullOrWhiteSpace(c.Name) && c.Message is not null) &&
            checks.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() == checks.Count;
        _snapshot = new(valid && checks.All(c => c.Passed || !c.BlocksWrites), Array.AsReadOnly(checks.ToArray()), verifiedAt)
        {
            MaterialsReady = valid && checks.Any(c => c.Name != "managed-protection") &&
                checks.All(c => c.Name == "managed-protection" || c.Passed || !c.BlocksWrites)
        };
    }
}

public sealed class BusinessRecoveryWorker(
    NpgsqlDataSource source, IPackageKeyUnwrapper unwrapper, BusinessRecoveryGuard guard,
    IConfiguration configuration, TimeProvider timeProvider, BusinessServiceConfiguration? managedConfiguration = null,
    NodeAuthorityGuard? authority = null, PostgresRecoveryReadinessProbe? sharedProbe = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var probe = sharedProbe ?? new PostgresRecoveryReadinessProbe(source, unwrapper);
        var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
        var local = new ServiceConfiguration(identity.ServerId, identity.NodeId, "127.0.0.1", 1, "unused-by-database-probe", false, 1);
        IReadOnlyList<StartupCheckResult>? verifiedChecks = null;
        object? verifiedSelection = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (authority is not null && !await authority.CheckAsync(stoppingToken))
                {
                    guard.Observe([new("active-node-authority", false, "旧节点已隔离，须从活动服务器重建。", true)], timeProvider.GetUtcNow());
                    return;
                }
                var selection = FileRecoveryDeploymentSelection.ReadConfigured(key => configuration[key],
                    identity.ServerId, identity.NodeId, identity.ServerEpoch);
                IReadOnlyList<StartupCheckResult> checks;
                if (verifiedChecks is not null && Equals(verifiedSelection, selection))
                    checks = verifiedChecks;
                else if (selection is { ConnectionReference: "ExamRecovery" })
                {
                    var working = new RecoveryWorkingCopy(selection.RequestId, selection.ActorId, selection.BackupId,
                        selection.WorkingDirectory, selection.WorkingDirectory, timeProvider.GetUtcNow());
                    // The registered business source itself must be the isolated target; do not verify a different connection.
                    var verified = (await new PostgresWorkingCopyReadinessProbe(configuration.GetConnectionString("Exam") ?? "",
                        configuration.GetConnectionString("ExamRecovery") ?? "", "", identity.ServerEpoch, probe, source)
                        .RunAsync(local, working, stoppingToken)).ToList();
                    var archiveOptions = PostgresArchiveRecoveryOptions.ReadConfigured(key => configuration[key]);
                    if (selection.ArchiveRecovery || archiveOptions is not null ||
                        File.Exists(Path.Combine(working.WorkingDirectory, "wal-recovery-plan.json")) ||
                        File.Exists(Path.Combine(working.WorkingDirectory, "wal-recovery.json")))
                    {
                        if (!selection.ArchiveRecovery || archiveOptions is null)
                            verified.Add(new("recovery-wal-replay", false, "归档恢复部署选择与配置不匹配，禁止开放业务。", true));
                        else
                            verified.Add(await new PostgresArchiveRecovery(archiveOptions).VerifyReplayedAsync(working,
                                new RestoreRequest(selection.RequestId, selection.BackupId, working.SourceDirectory, selection.ActorId),
                                source, stoppingToken));
                    }
                    checks = verified;
                    if (FileRecoveryDeploymentSelection.ReadConfigured(key => configuration[key], identity.ServerId,
                        identity.NodeId, identity.ServerEpoch) != selection) throw new IOException("恢复部署选择发生变化。");
                }
                else checks = await probe.RunAsync(local, stoppingToken);
                // A standby cannot open business writes. Recheck that small authority query,
                // without rescanning every examination and submission after startup succeeded.
                if (checks.Count > 0 && checks.All(c => c.Passed || !c.BlocksWrites))
                { verifiedChecks = checks; verifiedSelection = selection; }
                if (managedConfiguration is not null)
                    checks = checks.Append(await managedConfiguration.CheckProtectionAsync(source, stoppingToken)).ToArray();
                guard.Observe(checks, timeProvider.GetUtcNow());
                if (guard.Snapshot.Ready) return;
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                guard.Observe([new("recovery-snapshot", false, "启动恢复核对未完成。", true)], timeProvider.GetUtcNow());
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}

/// <summary>Background business mutations and notifications wait for the same startup evidence as HTTP.</summary>
public sealed class RecoveryGatedWorker<T>(T worker, BusinessRecoveryGuard guard) : BackgroundService where T : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!guard.Snapshot.Ready) await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();
            await worker.StartAsync(stoppingToken);
            try
            {
                if (worker.ExecuteTask is { } execution) await execution.WaitAsync(stoppingToken);
            }
            finally { await worker.StopAsync(CancellationToken.None); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
