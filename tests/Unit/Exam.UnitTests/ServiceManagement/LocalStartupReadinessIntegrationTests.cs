using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class LocalStartupReadinessIntegrationTests
{
    [Fact]
    public async Task PersistedConfigurationAndLocalProbes_BlockMissingKeyThenPermitStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "exam-f01-readiness-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        try
        {
            var configurationStore = new FileServiceConfigurationStore(Path.Combine(root, "service.json"));
            await configurationStore.SaveAsync(
                new ServiceConfiguration("synthetic-server", "synthetic-node", "127.0.0.1", 7443,
                    data, false, 0), expectedVersion: 0);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-f01-node", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var thumbprint = certificate.Thumbprint;

            var missingHost = new RecordingHost();
            var missingKey = await ServiceManagementService.LoadAsync(
                configurationStore, missingHost,
                new CompositeStartupReadinessProbe([
                    new LocalStorageStartupReadinessProbe(1),
                    new NodeCertificateStartupReadinessProbe(thumbprint, () => [])
                ]), new UnusedBackups(), new SystemClock());

            var blocked = await Assert.ThrowsAsync<DomainRuleException>(() => missingKey.StartAsync());
            Assert.Equal(ErrorCodes.ServiceRecovering, blocked.Code);
            Assert.Equal(0, missingHost.Starts);
            Assert.Equal(BusinessServiceStatus.Stopped, missingHost.Status);
            Assert.Contains(missingKey.GetStatus().Checks, check =>
                check.Name == "data-write" && check.Passed);
            Assert.Contains(missingKey.GetStatus().Checks, check =>
                check.Name == "node-certificate-private-key" && !check.Passed && check.BlocksWrites);

            // Recreate the manager as it would be after a restart; only a fresh key check can permit start.
            var readyHost = new RecordingHost();
            var withKey = await ServiceManagementService.LoadAsync(
                new FileServiceConfigurationStore(Path.Combine(root, "service.json")), readyHost,
                new CompositeStartupReadinessProbe([
                    new LocalStorageStartupReadinessProbe(1),
                    new NodeCertificateStartupReadinessProbe(thumbprint,
                        () => [X509CertificateLoader.LoadPkcs12(
                            certificate.Export(X509ContentType.Pkcs12), null)])
                ]), new UnusedBackups(), new SystemClock());

            var started = await withKey.StartAsync();
            Assert.Equal(BusinessServiceStatus.Running, started.Status);
            Assert.Equal(1, readyHost.Starts);
            Assert.Equal(1, started.Configuration.Version);
            Assert.All(started.Checks, check => Assert.True(check.Passed, check.Message));
            Assert.Empty(Directory.EnumerateFileSystemEntries(data));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingHost : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; private set; } = BusinessServiceStatus.Stopped;
        public int Starts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            Status = BusinessServiceStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Status = BusinessServiceStatus.Stopped;
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedBackups : IBackupCoordinator
    {
        public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
