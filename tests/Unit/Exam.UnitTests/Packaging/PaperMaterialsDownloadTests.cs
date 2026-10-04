using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Infrastructure.Packaging;

namespace Exam.UnitTests.Packaging;

public sealed class PaperMaterialsDownloadTests
{
    private static byte[] Archive(string exam = "exam", long version = 1) => PaperMaterialsArchive.Build(exam, version,
        [new KeyValuePair<string, byte[]>("manifest.json", Encoding.UTF8.GetBytes("{}"))]);

    private static HttpResponseMessage Response(byte[] bytes, string server = "server", string node = "node", long epoch = 2)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.Add("X-Exam-Server-Id", server);
        response.Headers.Add("X-Exam-Node-Id", node);
        response.Headers.Add("X-Exam-Server-Epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("X-Exam-Paper-Version", "1");
        response.Headers.Add("X-Exam-Materials-Sha256", Convert.ToHexString(SHA256.HashData(bytes)));
        return response;
    }

    [Fact]
    public async Task VerifiedBinaryResponseCarriesTheSameIdentityContractAsJson()
    {
        var bytes = Archive();
        using var response = Response(bytes);
        var envelope = await PaperMaterialsDownload.ReadAsync(response, "exam", 1);
        Assert.Equal(bytes, envelope.Result);
        Assert.Equal("server", envelope.ServerId);
        Assert.Equal("node", envelope.NodeId);
        Assert.Equal(2, envelope.ServerEpoch);
    }

    [Theory]
    [InlineData("missing-header")]
    [InlineData("duplicate-header")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("other-exam")]
    [InlineData("other-archive-version")]
    [InlineData("length")]
    public async Task InvalidBinaryEvidenceIsRejectedBeforeExtraction(string fault)
    {
        using var response = Response(fault == "other-exam" ? Archive("different") :
            fault == "other-archive-version" ? Archive(version: 2) : Archive());
        switch (fault)
        {
            case "missing-header": response.Headers.Remove("X-Exam-Server-Id"); break;
            case "duplicate-header": response.Headers.Add("X-Exam-Server-Id", "another"); break;
            case "hash":
                response.Headers.Remove("X-Exam-Materials-Sha256");
                response.Headers.Add("X-Exam-Materials-Sha256", new string('a', 64)); break;
            case "version":
                response.Headers.Remove("X-Exam-Paper-Version"); response.Headers.Add("X-Exam-Paper-Version", "2"); break;
            case "length": response.Content.Headers.ContentLength = (long)PaperMaterialsArchive.MaxArchiveBytes + 1; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => PaperMaterialsDownload.ReadAsync(response, "exam", 1));
    }

    [Theory]
    [InlineData("other-server")]
    [InlineData("old-epoch")]
    [InlineData("same-epoch-other-node")]
    public async Task HttpAdapterDoesNotAcceptAValidArchiveFromAChangedIdentity(string fault)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/recipients", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<PublicationRecipients>("server", "node", 2, "read", new([]), null),
                    options: JsonDefaults.Create())
            }
            : Response(Archive(), fault == "other-server" ? "other" : "server",
                fault == "same-epoch-other-node" ? "other" : "node", fault == "old-epoch" ? 1 : 2));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var api = new HttpExamPublicationClient(http);
        await api.GetPublicationRecipientsAsync();
        var error = await Assert.ThrowsAsync<ExamPublicationApiException>(() => api.DownloadPaperMaterialsAsync("exam", 1));
        Assert.Equal("INVALID_SERVER_IDENTITY", error.Code);
    }

    [Fact]
    public async Task MissingMaterialsRetainsItsBusinessErrorCodeInsteadOfGenericHttp404()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = JsonContent.Create(new ApiEnvelope<object>("server", "node", 1, "read", null,
                new("MATERIALS_NOT_FOUND", "No archive exists.")), options: JsonDefaults.Create())
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var error = await Assert.ThrowsAsync<ExamPublicationApiException>(() =>
            new HttpExamPublicationClient(http).DownloadPaperMaterialsAsync("exam", 1));
        Assert.Equal("MATERIALS_NOT_FOUND", error.Code);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
