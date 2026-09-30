using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class BusinessServiceConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "managed-business-" + Guid.NewGuid().ToString("N"));
    public BusinessServiceConfigurationTests() => Directory.CreateDirectory(_directory);
    private ServiceConfiguration Configuration(int port = 8443) => new("server", "node", "127.0.0.1", port, _directory, false, 1);

    [Fact]
    public async Task ExistingDeploymentWithoutManagedPathKeepsItsListenerConfiguration()
    {
        var builder = Builder();
        builder.Configuration["urls"] = "https://127.0.0.1:8443";
        Assert.Null(await BusinessServiceConfiguration.LoadAndApplyAsync(builder));
        Assert.Equal("https://127.0.0.1:8443", builder.Configuration["urls"]);
    }

    [Theory]
    [InlineData("urls", "http://127.0.0.1:5000")]
    [InlineData("http_ports", "5000")]
    [InlineData("https_ports", "5001")]
    [InlineData("Kestrel:Endpoints:Other:Url", "http://127.0.0.1:5000")]
    [InlineData("preferHostingUrls", "true")]
    public async Task ManagedListenerRejectsCompetingDeploymentSources(string key, string value)
    {
        var builder = await ConfiguredBuilderAsync(Configuration());
        builder.Configuration[key] = value;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BusinessServiceConfiguration.LoadAndApplyAsync(builder));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("missing-directory")]
    [InlineData("space")]
    [InlineData("invalid-address")]
    public async Task InvalidManagedDeploymentNeverStartsOrCreatesMissingStorage(string failure)
    {
        var missing = Path.Combine(_directory, "missing");
        var configuration = Configuration();
        if (failure == "missing-directory") configuration = configuration with { DataDirectory = missing };
        if (failure == "invalid-address") configuration = configuration with { BindAddress = "arbitrary.invalid" };
        var builder = await ConfiguredBuilderAsync(configuration);
        if (failure == "identity") builder.Configuration["ExamServer:NodeId"] = "other";
        if (failure == "space") builder.Configuration["Management:MinimumAvailableBytes"] = long.MaxValue.ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BusinessServiceConfiguration.LoadAndApplyAsync(builder));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task SavedConfigurationControlsRealHttpsListenerAndReadinessEvidenceAfterRestart()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel cannot use CreateSelfSigned's ephemeral key. Import this synthetic PFX
        // without PersistKeySet so the temporary key is released with the certificate; no store is changed.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        var first = Configuration(AvailablePort());
        var path = Path.Combine(_directory, "service.json");
        var store = new FileServiceConfigurationStore(path);
        await store.SaveAsync(first, 0);
        await ObserveAsync(first);
        var saved = await store.SaveAsync(first with { HttpsPort = AvailablePort() }, first.Version);
        await ObserveAsync(saved);
        Assert.Equal(2, saved.Version);

        async Task ObserveAsync(ServiceConfiguration expected)
        {
            var builder = Builder(path);
            builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(tls => tls.ServerCertificate = certificate));
            var applied = await BusinessServiceConfiguration.LoadAndApplyAsync(builder);
            await using var app = builder.Build();
            app.MapGet("/health/ready", () => new
            { status = "ready", serverId = "server", nodeId = "node", serverEpoch = 1, serviceConfiguration = applied!.Value });
            await app.StartAsync();
            using var transport = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual is not null && actual.RawData.AsSpan().SequenceEqual(certificate.RawData)
            };
            using var client = new HttpClient(transport);
            using var observed = await client.GetAsync($"https://localhost:{expected.HttpsPort}/health/ready");
            Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
            var host = new ReadyCheckedBusinessHost(new RunningProcess(), client,
                new Uri("https://localhost:1/health/ready"), "server", "node", 1, expectedConfiguration: expected);
            Assert.True(await host.ObserveReadyAsync(default));
            Assert.Equal(expected, applied!.Value);
            await app.StopAsync();
        }
    }

    private async Task<WebApplicationBuilder> ConfiguredBuilderAsync(ServiceConfiguration configuration)
    {
        var path = Path.Combine(_directory, "service.json");
        await new FileServiceConfigurationStore(path).SaveAsync(configuration, 0);
        return Builder(path);
    }

    private static WebApplicationBuilder Builder(string? path = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node", ["ExamServer:ServerEpoch"] = "1",
            ["Management:ServiceConfigurationPath"] = path, ["Management:MinimumAvailableBytes"] = "1"
        });
        return builder;
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class RunningProcess : Exam.Application.ServiceManagement.IManagedBusinessHost
    {
        public BusinessServiceStatus Status => BusinessServiceStatus.Running;
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Readiness observation must not start a process.");
        public Task StopAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Readiness observation must not stop a process.");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
