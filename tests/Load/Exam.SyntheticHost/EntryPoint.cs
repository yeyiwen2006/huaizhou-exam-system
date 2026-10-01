using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Exam.SyntheticHost;

// Only certificate storage is replaced. Production Program, middleware, readiness checks,
// PostgreSQL adapters and background workers run unchanged on a real loopback socket.
internal static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--port", var value] || !int.TryParse(value, out var port) || port is < 1 or > 65535) return 2;
        var fixture = Environment.GetEnvironmentVariable("EXAM_SYNTHETIC_FIXTURE") ?? throw new InvalidOperationException("Synthetic fixture required.");
        LocalFixtureValidation.Validate(fixture);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(fixture, "synthetic-node.pfx"),
            File.ReadAllText(Path.Combine(fixture, "synthetic-node.password")), X509KeyStorageFlags.EphemeralKeySet);
        var node = Environment.GetEnvironmentVariable("ExamServer__NodeId") ?? throw new InvalidOperationException("Synthetic node required.");
        await using var factory = new SyntheticFactory(new Unwrapper(node, certificate));
        factory.UseKestrel(port);
        factory.StartServer();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.TrySetResult(); };
        await stopped.Task;
        return 0;
    }

    private sealed class SyntheticFactory(IPackageKeyUnwrapper unwrapper) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseContentRoot(
            Environment.GetEnvironmentVariable("EXAM_SYNTHETIC_CONTENT_ROOT") ?? throw new InvalidOperationException("Synthetic content root required."))
            .ConfigureTestServices(services =>
        {
            services.RemoveAll<IPackageKeyUnwrapper>();
            services.AddSingleton(unwrapper);
        });
    }

    private sealed class Unwrapper(string node, X509Certificate2 certificate) : IPackageKeyUnwrapper
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, node, certificate);
    }
}
