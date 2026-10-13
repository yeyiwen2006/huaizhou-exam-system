using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.Activation;
using Exam.Application.Security;
using Exam.Contracts.Archives;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Archives;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Exam.IntegrationTests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class ExamArchiveBoundaryTests(PostgresDatabaseFixture fixture)
{
    private int _serviceResolutions;

    [Fact]
    public async Task OperatorCanExportPreviewAndImportAnIndependentExamCopyThroughHttp()
    {
        var unique = Guid.NewGuid().ToString("N");
        var catalog = new PostgresExamCatalog(fixture.DataSource,
            new PostgresExamVersioningService(fixture.DataSource), TimeProvider.System);
        var settings = new ExamSettings([new("s", "合成科目", "1",
            [new("g", "单选", "single", 1, 1, "1", false, new("1", "0", "0"), null)])]);
        var now = DateTimeOffset.UtcNow;
        var source = await catalog.CreateAsync(new(unique, "归档接口考试" + unique,
            new(now.AddHours(1), now.AddHours(2)), settings), "synthetic-operator");
        var unusedKeys = new UnusedResourceKeys();
        var service = new PostgresExamArchiveService(fixture.DataSource,
            new PortableExamResources(unusedKeys, unusedKeys, new PackageEncryptionService()));
        await using var app = await StartAsync(service);
        using var client = OperatorClient(app);

        using var exported = await client.GetAsync($"/api/v1/exams/{source.Summary.ExamId}/archive");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("application/zip", exported.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ExamArchiveFormat.FileName,
            exported.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(app.Configuration["ExamServer:ServerId"], Assert.Single(exported.Headers.GetValues("X-Exam-Server-Id")));
        Assert.Equal("synthetic-node", Assert.Single(exported.Headers.GetValues("X-Exam-Node-Id")));
        Assert.Equal("1", Assert.Single(exported.Headers.GetValues("X-Exam-Server-Epoch")));
        var bytes = await exported.Content.ReadAsByteArrayAsync();
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            Assert.NotNull(zip.GetEntry("manifest.json"));

        using var previewForm = ArchiveUpload(bytes);
        using var previewResponse = await client.PostAsync("/api/v1/exam-archives/preview", previewForm);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<ApiEnvelope<ExamArchivePreview>>(JsonDefaults.Create());
        Assert.Null(preview!.Error);
        Assert.Equal(ExamArchiveFormat.Version, preview.Result!.FormatVersion);
        Assert.Equal(source.Summary.ExamId, preview.Result.SourceExamId);
        Assert.Equal(source.Summary.Name, preview.Result.ExamName);

        using var importForm = ArchiveUpload(bytes);
        importForm.Add(new StringContent("copy"), "mode");
        importForm.Add(new StringContent(Guid.NewGuid().ToString("N")), "requestId");
        using var importResponse = await client.PostAsync("/api/v1/exam-archives/import", importForm);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        var imported = await importResponse.Content.ReadFromJsonAsync<ApiEnvelope<ExamArchiveImportResult>>(JsonDefaults.Create());
        Assert.Null(imported!.Error);
        Assert.True(imported.Result!.Imported);
        Assert.False(imported.Result.Skipped);
        Assert.NotEqual(source.Summary.ExamId, imported.Result.ExamId);
        Assert.NotEqual(source.Summary.Name, imported.Result.ExamName);

        using var detailResponse = await client.GetAsync($"/api/v1/exams/{imported.Result.ExamId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<ApiEnvelope<ExamDetail>>(JsonDefaults.Create());
        Assert.Null(detail!.Error);
        Assert.Equal(imported.Result.ExamId, detail.Result!.Summary.ExamId);
        Assert.Equal(imported.Result.ExamName, detail.Result.Summary.Name);
        Assert.Equal("合成科目", Assert.Single(detail.Result.Settings.Subjects).Name);
    }

    [Theory]
    [InlineData("GET", "/api/v1/exams/synthetic-exam/archive", false)]
    [InlineData("GET", "/api/v1/exams/synthetic-exam/archive", true)]
    [InlineData("POST", "/api/v1/exam-archives/preview", false)]
    [InlineData("POST", "/api/v1/exam-archives/preview", true)]
    [InlineData("POST", "/api/v1/exam-archives/import", false)]
    [InlineData("POST", "/api/v1/exam-archives/import", true)]
    public async Task ArchiveRoutesRequireOperatorIdentityEvenWhenDeviceCredentialsAreValid(string method, string path, bool device)
    {
        await using var app = await StartAsync(); using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (device)
        {
            request.Headers.Add("X-Exam-Device-Id", "synthetic-device");
            request.Headers.Add("X-Exam-Device-Credential", "synthetic-device-credential");
        }
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<object>>(JsonDefaults.Create());
        Assert.Equal("UNAUTHORIZED", envelope!.Error!.Code); Assert.Null(envelope.Result);
        Assert.Equal(0, _serviceResolutions);
    }

    [Theory]
    [InlineData("preview", "not-form")]
    [InlineData("preview", "malformed-multipart")]
    [InlineData("preview", "invalid-zip")]
    [InlineData("import", "invalid-zip")]
    public async Task MalformedUploadReturns400WithoutReachingArchiveStorage(string action, string shape)
    {
        await using var app = await StartAsync(); using var client = OperatorClient(app);
        using HttpContent content = shape switch
        {
            "not-form" => new ByteArrayContent([1, 2, 3]),
            "malformed-multipart" => MalformedMultipart(),
            _ => Upload(action == "import")
        };
        using var response = await client.PostAsync("/api/v1/exam-archives/" + action, content);
        await AssertInvalidAsync(response);
        Assert.Equal(0, _serviceResolutions);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IncompleteOverwriteTargetIsRejectedBeforeAnyStorageServiceIsUsed(bool target, bool version)
    {
        await using var app = await StartAsync(); using var client = OperatorClient(app);
        using var content = Upload(import: false);
        content.Add(new StringContent("synthetic-request"), "requestId");
        content.Add(new StringContent("overwrite"), "mode");
        if (target) content.Add(new StringContent("existing-exam"), "expectedTargetExamId");
        if (version) content.Add(new StringContent("7"), "expectedTargetVersion");
        using var response = await client.PostAsync("/api/v1/exam-archives/import", content);
        await AssertInvalidAsync(response);
        Assert.Equal(0, _serviceResolutions);
    }

    private async Task<WebApplication> StartAsync(PostgresExamArchiveService? valid = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        var serverId = "archive-boundary-" + Guid.NewGuid().ToString("N");
        builder.Configuration["ExamServer:ServerId"] = serverId;
        builder.Configuration["ExamServer:NodeId"] = "synthetic-node";
        builder.Configuration["ExamServer:ServerEpoch"] = "1";
        builder.Services.AddSingleton(new ServerNodeEnvelope(serverId, "synthetic-node", 1));
        builder.Services.AddSingleton<IOperatorAuthenticator>(new FixedOperatorAuthenticator("synthetic-operator-token", "synthetic-operator"));
        builder.Services.AddSingleton<IDeviceCredentialValidator, AcceptingDeviceValidator>();
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource,
            new() { ServerId = serverId, NodeId = "synthetic-node", ServerEpoch = 1 }, true));
        builder.Services.AddSingleton<PostgresExamArchiveService>(_ =>
        {
            _serviceResolutions++;
            if (valid is not null) return valid;
            throw new InvalidOperationException("Rejected requests must not resolve the archive storage service.");
        });
        if (valid is not null)
            builder.Services.AddSingleton(new PostgresExamCatalog(fixture.DataSource,
                new PostgresExamVersioningService(fixture.DataSource), TimeProvider.System));
        var app = builder.Build(); app.UseMiddleware<ApiExceptionMiddleware>(); app.UseMiddleware<ExamApiAccessMiddleware>();
        var api = app.MapGroup("/api/v1");
        api.MapExamArchiveApi();
        if (valid is not null) api.MapExamCatalogApi();
        await app.StartAsync(); return app;
    }

    private static HttpClient OperatorClient(WebApplication app)
    { var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-operator-token"); return client; }
    private static MultipartFormDataContent Upload(bool import)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "exam.archive");
        if (import)
        {
            form.Add(new StringContent("synthetic-request"), "requestId");
            form.Add(new StringContent("copy"), "mode");
        }
        return form;
    }
    private static MultipartFormDataContent ArchiveUpload(byte[] archive)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(archive), "file", ExamArchiveFormat.FileName);
        return form;
    }
    private static HttpContent MalformedMultipart()
    {
        var content = new StringContent("--synthetic-boundary\r\ninvalid multipart header");
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=synthetic-boundary");
        return content;
    }
    private static async Task AssertInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<object>>(JsonDefaults.Create());
        Assert.Equal(ErrorCodes.InvalidInput, envelope!.Error!.Code); Assert.Null(envelope.Result);
    }
    private sealed class AcceptingDeviceValidator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken token = default) =>
            ValueTask.FromResult(deviceId == "synthetic-device" && credential == "synthetic-device-credential");
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken token = default) => ValueTask.FromResult(true);
    }
    private sealed class UnusedResourceKeys : IPackageKeyUnwrapper, IPublicationCertificateProvider
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) =>
            throw new InvalidOperationException("An exam without published resources must not unwrap package keys.");
        public PublicationCertificateLease Acquire() =>
            throw new InvalidOperationException("An exam without published resources must not acquire publication certificates.");
    }
}
