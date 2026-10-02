using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;

namespace Exam.AdminUnitTests;

/// <summary>A small synthetic server for workbook-to-view-model tests; real transaction rules have PostgreSQL tests.</summary>
internal sealed class RosterTestSession : IDisposable
{
    private CandidateRosterSnapshot _snapshot = new("synthetic-roster-exam", 0, []);
    public AdminApiClient Api { get; }
    public RosterViewModel ViewModel { get; }
    public RosterTestSession(RosterWorkbookService workbooks, IFileDialogService dialogs)
    {
        Api = new AdminApiClient(new Handler(Respond));
        Api.Configure("https://localhost/", "synthetic-token", "synthetic-operator");
        ViewModel = new RosterViewModel(workbooks, dialogs, Api, _ => true);
        ViewModel.OpenAsync(_snapshot.ExamId).GetAwaiter().GetResult();
    }
    private async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get) return Ok(_snapshot);
        if (request.Method != HttpMethod.Post || !request.RequestUri!.AbsolutePath.EndsWith("/import", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic roster request.");
        var import = (await request.Content!.ReadFromJsonAsync<ImportCandidateRosterRequest>(JsonDefaults.Create()))!;
        if (import.ExpectedVersion != _snapshot.Version)
            return new(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "roster-test", null,
                    new ApiError("VERSION_CONFLICT", "名册版本已变化。")), options: JsonDefaults.Create())
            };
        var rows = _snapshot.Entries.ToList();
        var duplicates = 0;
        var added = 0;
        foreach (var row in import.Rows)
        {
            var previous = rows.SingleOrDefault(x => x.AdmissionNo == row.AdmissionNo);
            if (previous is not null)
            {
                if (previous.Name != row.Name || previous.ClassText != row.ClassText)
                    return Ok(new RosterImportResult(false, 0, 0, _snapshot, [new(row.RowNumber, "admissionNo", "与现有身份冲突。") ]));
                duplicates++;
            }
            else { rows.Add(new(Guid.NewGuid().ToString("N"), row.Name, row.ClassText, row.AdmissionNo)); added++; }
        }
        if (added > 0) _snapshot = _snapshot with { Version = _snapshot.Version + 1, Entries = rows };
        return Ok(new RosterImportResult(true, added, duplicates, _snapshot, []));
    }
    private static HttpResponseMessage Ok<T>(T result) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "roster-test", result, null), options: JsonDefaults.Create()) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    public void Dispose() { ViewModel.Dispose(); Api.Dispose(); }
}
