using System.Net;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;
using Exam.Management;

internal static class LocalManagementHost
{
    public static async Task<int> RunAsync(string configuration, string postgresBin)
    {
        var context = new LocalServiceContext(configuration, postgresBin);
        await context.InitializeAsync();
        var settings = context.Settings;
        if (settings.ManagementPort is < 1024 or > 65535 ||
            new[] { settings.AdminPort, settings.HttpsPort, settings.DatabasePort }.Contains(settings.ManagementPort))
            throw new InvalidOperationException("本机测试管理端口无效。");
        using var certificate = LocalEntry.LoadCertificate(context.Root, settings, 0);
        await using var business = new LocalBusinessControl(context);
        var backups = new LocalBackupCoordinator(context);
        var journal = new FileManagementOperationStore(Path.Combine(context.ManagementDirectory, "operations"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = [], EnvironmentName = "Development", ContentRootPath = context.Root });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 65536;
            options.Listen(IPAddress.Loopback, settings.ManagementPort, endpoint => endpoint.UseHttps(certificate));
        });
        builder.Services.AddSingleton(new ManagementOperatorSessions(() => context.Connection(),
            Path.Combine(context.ManagementDirectory, "operators.cache")));
        builder.Services.AddSingleton(new ManagementRuntime(settings.ServerId, "local-node-a", context, business,
            _ => business, business, () => backups, journal, new SystemClock(), settings.ManagementPort,
            observeBusinessReady: (_, token) => business.ReadyAsync(token),
            prepareRecoveryWorkingCopy: backups.PrepareAsync, verifyRecoveryTarget: backups.VerifyAsync,
            recoveryWorkingDirectory: () => Path.Combine(context.Root, "restores"),
            activateRecovery: (operation, token) => backups.ActivateAsync(operation, business, journal, token),
            observeRecoveryActivation: (operation, token) => backups.ObserveAsync(operation, business, token),
            readAudit: journal.ReadAuditAsync,
            readRuntimeLogs: (_, _) => Task.FromResult<IReadOnlyList<ProtectedRuntimeLogEntry>>([]),
            checkReadinessOnStatus: true, requireCurrentRecoveryState: false));
        await using var app = builder.Build();
        app.UseWhen(request => request.Request.Path == "/health/ready", branch => branch.Run(async request =>
            await request.Response.WriteAsJsonAsync(new { status = "ready", localDebug = true })));
        app.MapManagementApi();
        try { await business.StartAsync(CancellationToken.None); }
        catch (LocalStartupException error) { return error.ExitCode == 3 ? 3 : 1; }
        await app.RunAsync();
        return 0;
    }
}
