using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;
using Exam.Domain.Reporting;
using Exam.Reporting;

internal static partial class ExamCli
{
    private static async Task<int> RunReportAsync(string action, string examId, string[] arguments, CancellationToken cancellationToken)
    {
        var userCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        cancellationToken = deadline.Token;
        if (action is not ("status" or "show" or "export")) return 2;
        var destination = action == "export" && arguments.Length > 0 ? arguments[0] : null;
        if (action == "export" && (destination is null || destination.StartsWith("--", StringComparison.Ordinal))) return 2;
        var options = ParseOptions(destination is null ? arguments : arguments[1..]);
        var server = options.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!TryResolveServer(server, out var address)) return 2;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromMinutes(5) };
        ApiEnvelope<T>? Envelope<T>(ApiEnvelope<T>? value) => value is not null &&
            !string.IsNullOrWhiteSpace(value.ServerId) && !string.IsNullOrWhiteSpace(value.NodeId) && value.ServerEpoch > 0
            ? value : throw new InvalidDataException("报表响应缺少服务器身份。");
        async Task<ApiEnvelope<T>> Get<T>(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            AddCredentials(request);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ReportCliException(response.StatusCode);
            var value = Envelope(await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonDefaults.Create(), cancellationToken))!;
            if (value.Error is not null || value.Result is null) throw new InvalidDataException("报表响应缺少有效结果。");
            return value;
        }
        try
        {
            var root = "/api/v1/reports/" + Uri.EscapeDataString(examId);
            var initial = await Get<ReportStatus>(root + "/status");
            if (initial.Result!.ExamId != examId) throw new InvalidDataException("报表考试不一致。");
            ReportStatusValidator.Validate(initial.Result);
            if (action == "status") { Console.WriteLine(JsonSerializer.Serialize(initial, JsonDefaults.Create())); return 0; }
            if (initial.Result.State != ReportState.Ready || initial.Result.SnapshotId is null)
            { Console.Error.WriteLine(initial.Result.Reason); return 4; }
            var snapshot = initial.Result.SnapshotId;
            if (action == "show")
            {
                var result = await Get<PublishedReport>(root + "?snapshotId=" + Uri.EscapeDataString(snapshot));
                if (result.ServerId != initial.ServerId || result.ServerEpoch < initial.ServerEpoch ||
                    result.ServerEpoch == initial.ServerEpoch && result.NodeId != initial.NodeId ||
                    result.Result!.SchemaVersion != 1 || result.Result.Status.SnapshotId != snapshot || result.Result.Status.ExamId != examId)
                    throw new InvalidDataException("报表版本或服务器不一致。");
                ReportJson.Read(result.Result);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Create())); return 0;
            }
            using var download = new HttpRequestMessage(HttpMethod.Get, root + "/xlsx?snapshotId=" + Uri.EscapeDataString(snapshot));
            AddCredentials(download);
            using var binary = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!binary.IsSuccessStatusCode) throw new ReportCliException(binary.StatusCode);
            var workbook = await ReportWorkbookDownload.ReadAsync(binary, snapshot, cancellationToken);
            if (workbook.ServerId != initial.ServerId || workbook.ServerEpoch < initial.ServerEpoch ||
                workbook.ServerEpoch == initial.ServerEpoch && workbook.NodeId != initial.NodeId)
                throw new InvalidDataException("导出服务器身份不一致。");
            await ReportWorkbookDownload.SaveAsync(workbook, destination!, async () =>
            {
                var current = await Get<ReportStatus>(root + "/status");
                ReportStatusValidator.Validate(current.Result!);
                if (current.ServerId != workbook.ServerId || current.ServerEpoch < workbook.ServerEpoch ||
                    current.ServerEpoch == workbook.ServerEpoch && current.NodeId != workbook.NodeId ||
                    current.Result!.ExamId != examId ||
                    current.Result!.State != ReportState.Ready || current.Result.SnapshotId != snapshot)
                    throw new InvalidDataException("报表已失效，未替换目标文件。");
            }, cancellationToken);
            Console.WriteLine("Excel 已完整保存。"); return 0;
        }
        catch (ReportCliException error)
        {
            Console.Error.WriteLine($"报表请求被拒绝：HTTP {(int)error.Status}。");
            return error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                (int)error.Status >= 500 ? 6 : 4;
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine("NETWORK_UNAVAILABLE: 报表请求或下载超时。");
            return 5;
        }
    }
    private sealed class ReportCliException(HttpStatusCode status) : Exception { public HttpStatusCode Status { get; } = status; }
}
