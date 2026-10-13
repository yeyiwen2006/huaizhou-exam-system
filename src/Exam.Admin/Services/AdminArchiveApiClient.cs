using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Archives;
using Exam.Contracts.Common;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    private const long MaximumExamArchiveBytes = ExamArchiveFormat.MaximumArchiveBytes;

    public async Task DownloadExamArchiveAsync(string examId, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        using var scope = new ArchiveRequestScope(this, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/exams/{Uri.EscapeDataString(examId)}/archive");
        using var response = await scope.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token);
        scope.RequireCurrent();
        if (!response.IsSuccessStatusCode) await ReadArchiveEnvelopeAsync<object>(response, scope);
        if (response.Content.Headers.ContentType?.MediaType != "application/zip" ||
            response.Content.Headers.ContentLength is > MaximumExamArchiveBytes)
            throw new InvalidDataException("考试归档类型或大小无效。");
        var server = ArchiveHeader(response, "X-Exam-Server-Id");
        var node = ArchiveHeader(response, "X-Exam-Node-Id");
        if (!long.TryParse(ArchiveHeader(response, "X-Exam-Server-Epoch"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            throw new InvalidDataException("考试归档缺少服务器身份。");
        ValidateEnvelope(new ApiEnvelope<object>(server, node, epoch, "archive", new(), null));
        await using var input = await response.Content.ReadAsStreamAsync(scope.Token);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, scope.Token)) != 0)
        {
            total += read;
            if (total > MaximumExamArchiveBytes) throw new InvalidDataException("考试归档超过 512 MiB 上限。");
            await destination.WriteAsync(buffer.AsMemory(0, read), scope.Token);
        }
        scope.RequireCurrent();
        if (total == 0 || response.Content.Headers.ContentLength is { } length && total != length)
            throw new InvalidDataException("考试归档下载不完整。");
    }

    public Task<ExamArchivePreview> PreviewExamArchiveAsync(Stream archive, CancellationToken token = default) =>
        SendArchiveAsync<ExamArchivePreview>("preview", archive, null, null, token);

    public Task<ExamArchiveImportResult> ImportExamArchiveAsync(Stream archive, ArchiveImportDecision decision,
        string requestId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (decision.Choice == ArchiveImportChoice.Overwrite && decision.Target is null)
            throw new ArgumentException("请选择要覆盖的目标考试。");
        return SendArchiveAsync<ExamArchiveImportResult>("import", archive, decision, requestId, token);
    }

    private async Task<T> SendArchiveAsync<T>(string action, Stream archive, ArchiveImportDecision? decision,
        string? requestId, CancellationToken token) where T : class
    {
        if (!archive.CanSeek || archive.Length is <= 0 or > MaximumExamArchiveBytes)
            throw new InvalidDataException("考试归档为空或超过 512 MiB 上限。");
        archive.Position = 0;
        using var scope = new ArchiveRequestScope(this, token);
        using var form = new MultipartFormDataContent();
        form.Add(new ArchiveStreamContent(archive), "file", ExamArchiveService.ArchiveFileName);
        if (decision is not null)
        {
            var mode = decision.Choice switch
            {
                ArchiveImportChoice.Copy => "copy", ArchiveImportChoice.Overwrite => "overwrite", ArchiveImportChoice.Skip => "skip",
                _ => throw new ArgumentOutOfRangeException(nameof(decision))
            };
            form.Add(new StringContent(mode), "mode");
            form.Add(new StringContent(requestId!), "requestId");
            if (decision.Target is { } target)
            {
                form.Add(new StringContent(target.ExamId), "expectedTargetExamId");
                form.Add(new StringContent(target.Version.ToString(CultureInfo.InvariantCulture)), "expectedTargetVersion");
            }
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/exam-archives/" + action) { Content = form };
        using var response = await scope.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, scope.Token);
        return await ReadArchiveEnvelopeAsync<T>(response, scope);
    }

    private async Task<T> ReadArchiveEnvelopeAsync<T>(HttpResponseMessage response, ArchiveRequestScope scope) where T : class
    {
        scope.RequireCurrent();
        ApiEnvelope<T>? envelope = null;
        try { envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(_json, scope.Token); }
        catch (JsonException) { }
        scope.RequireCurrent();
        if (envelope is not null) ValidateEnvelope(envelope);
        if (!response.IsSuccessStatusCode || envelope?.Error is not null)
            throw new AdminApiException(envelope?.Error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                envelope?.Error?.Message ?? "服务器未能完成考试归档操作。", response.StatusCode);
        return envelope?.Result ?? throw new InvalidDataException("考试归档响应无效。");
    }

    private static string ArchiveHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.ToArray() is [var value] ? value : string.Empty;

    private sealed class ArchiveRequestScope : IDisposable
    {
        private readonly AdminApiClient _owner;
        private readonly long _generation;
        private readonly CancellationTokenSource _cancellation;
        public HttpClient Client { get; }
        public CancellationToken Token => _cancellation.Token;

        public ArchiveRequestScope(AdminApiClient owner, CancellationToken token)
        {
            owner.EnsureConfigured();
            _owner = owner; _generation = owner.Generation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _cancellation.CancelAfter(TimeSpan.FromHours(1));
            Client = owner.CreateClient();
            Client.BaseAddress = owner._httpClient.BaseAddress;
            Client.DefaultRequestHeaders.Authorization = owner._httpClient.DefaultRequestHeaders.Authorization;
            Client.Timeout = Timeout.InfiniteTimeSpan;
            owner.ConfigurationChanged += OnConfigurationChanged;
        }
        private void OnConfigurationChanged(object? sender, EventArgs args) => _cancellation.Cancel();
        public void RequireCurrent() { Token.ThrowIfCancellationRequested(); _owner.EnsureCurrentConnection(_generation); }
        public void Dispose()
        {
            _owner.ConfigurationChanged -= OnConfigurationChanged;
            Client.Dispose(); _cancellation.Dispose();
        }
    }

    // Disposing a multipart request must not close the same file needed after preview confirmation.
    private sealed class ArchiveStreamContent(Stream source) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => source.CopyToAsync(destination);
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context, CancellationToken token) => source.CopyToAsync(destination, token);
        protected override bool TryComputeLength(out long length) { length = source.Length - source.Position; return true; }
    }
}
