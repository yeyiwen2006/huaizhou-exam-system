using Exam.Contracts.Common;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Server.Api;
using Exam.Server.Diagnostics;
using Exam.Server.Realtime;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Exam.SyntheticHost;

// Isolated loopback-only harness: production contracts, routes, database adapters and workers,
// but synthetic certificates and single-node confirmation. Never a deployable business host.
internal static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--port", var value] || !int.TryParse(value, out var port) || port is < 1 or > 65535) return 2;
        var fixture = Environment.GetEnvironmentVariable("EXAM_SYNTHETIC_FIXTURE")
            ?? throw new InvalidOperationException("Synthetic fixture required.");
        LocalFixtureValidation.Validate(fixture);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(fixture, "synthetic-node.pfx"),
            File.ReadAllText(Path.Combine(fixture, "synthetic-node.password")), X509KeyStorageFlags.EphemeralKeySet);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = "Testing",
            ContentRootPath = Environment.GetEnvironmentVariable("EXAM_SYNTHETIC_CONTENT_ROOT")
                ?? throw new InvalidOperationException("Synthetic content root required.")
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var identity = builder.Configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
            ?? throw new InvalidOperationException("Synthetic identity required.");
        builder.Services.Configure<ServerIdentityOptions>(builder.Configuration.GetSection(ServerIdentityOptions.SectionName));
        builder.Services.AddExamServerTransport();
        builder.Services.AddPostgresExamRuntime(builder.Configuration, identity, allowUninitializedFailoverState: true);
        builder.Services.RemoveAll<IPackageKeyUnwrapper>();
        builder.Services.AddSingleton<IPackageKeyUnwrapper>(new Unwrapper(identity.NodeId, certificate));
        await using var app = builder.Build();
        if (builder.Configuration.GetValue("Database:ApplyMigrations", false))
            await app.Services.GetRequiredService<PostgresMigrationRunner>().MigrateAsync();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.UseMiddleware<BusinessConfirmationMiddleware>();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (PostgresReadinessProbe probe, BusinessRecoveryGuard recovery,
            CancellationToken cancellation) =>
        {
            var readiness = await probe.CheckAsync(cancellation);
            return readiness.Ready && recovery.Snapshot.Ready
                ? Results.Ok(new { status = "ready", identity.ServerId, identity.NodeId, identity.ServerEpoch })
                : Results.Json(new { status = "recovering" }, statusCode: 503);
        });
        app.MapExamApi();
        app.MapExamDiagnostics();
        app.MapHub<ExamEventHub>("/hubs/exam-events");
        // This route is intentionally absent from Exam.Server. The common middleware still requires an operator session.
        app.MapPost("/api/v1/testing/commands", async (CreateCommandEventRequest request,
            ReliableCommandService commands, HttpContext context, IConfiguration configuration, CancellationToken cancellation) =>
        {
            if (request.EventType != CommandEventType.ScheduleChanged)
                return Results.BadRequest();
            var result = await commands.CreateAsync(request, cancellation);
            return result.IsSuccess ? Results.Ok(new
            {
                identity.ServerId, identity.NodeId, identity.ServerEpoch,
                correlationId = request.RequestId, result = result.Value, error = (object?)null
            }) : Results.BadRequest();
        });
        await app.RunAsync();
        return 0;
    }

    private sealed class Unwrapper(string node, X509Certificate2 certificate) : IPackageKeyUnwrapper
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, node, certificate);
    }
}
