using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;

namespace Exam.AdminUnitTests;

public sealed class ServerRosterViewModelTests
{
    [Fact]
    public async Task ExamSwitchClearsRowsAndLateOldReadCannotResurrectThem()
    {
        var old = NewResponse();
        using var api = Client(request => request.RequestUri!.AbsolutePath.Contains("/A/", StringComparison.Ordinal)
            ? old.Task : Task.FromResult(Ok(Snapshot("B", 7))));
        using var vm = Model(api);
        var a = vm.OpenAsync("A");
        await vm.OpenAsync("B");
        old.SetResult(Ok(Snapshot("A", 10, Person("A"))));
        await a;
        Assert.Equal("B", vm.ExamId);
        Assert.Equal(7, vm.LoadedVersion);
        Assert.Empty(vm.Entries);
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task FailedNewExamReadCannotKeepPreviousRosterOrPermitWrites()
    {
        var writes = 0;
        using var api = Client(request =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/A/", StringComparison.Ordinal)
                ? Ok(Snapshot("A", 1, Person("A"))) : Fail(HttpStatusCode.ServiceUnavailable));
        });
        using var vm = Model(api);
        await vm.OpenAsync("A");
        Assert.Single(vm.Entries);
        await vm.OpenAsync("B");
        Assert.Empty(vm.Entries);
        Assert.False(vm.CanEdit);
        SetInput(vm);
        await vm.AddManualAsync();
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task WriteReceiptFromPreviousExamCannotPopulateCurrentRows()
    {
        var write = NewResponse();
        using var api = Client(request => request.Method == HttpMethod.Post ? write.Task
            : Task.FromResult(Ok(Snapshot(request.RequestUri!.AbsolutePath.Contains("/A/", StringComparison.Ordinal) ? "A" : "B", 0))));
        using var vm = Model(api);
        await vm.OpenAsync("A");
        SetInput(vm);
        var saving = vm.AddManualAsync();
        Assert.Empty(vm.Entries);
        await vm.OpenAsync("B");
        write.SetResult(Ok(new RosterImportResult(true, 1, 0, Snapshot("A", 1, Person("A")), [])));
        await saving;
        Assert.Equal("B", vm.ExamId);
        Assert.Empty(vm.Entries);
        Assert.DoesNotContain("新增 1 人", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectClearsCurrentRowsIdentityInputsAndVersion()
    {
        using var api = Client(_ => Task.FromResult(Ok(Snapshot("A", 3, Person("A")))));
        using var vm = Model(api);
        await vm.OpenAsync("A");
        vm.SelectedEntry = vm.Entries[0];
        api.Disconnect();
        Assert.Empty(vm.Entries);
        Assert.Empty(vm.Name);
        Assert.Empty(vm.ClassText);
        Assert.Empty(vm.AdmissionNo);
        Assert.Empty(vm.ExamId);
        Assert.Null(vm.LoadedVersion);
        Assert.False(vm.CanEdit);
    }

    [Fact]
    public async Task VersionConflictDisablesFurtherWritesUntilExplicitRefresh()
    {
        var calls = 0;
        using var api = Client(request =>
        {
            if (request.Method == HttpMethod.Post) { calls++; return Task.FromResult(Fail(HttpStatusCode.Conflict)); }
            return Task.FromResult(Ok(Snapshot("A", 3)));
        });
        using var vm = Model(api);
        await vm.OpenAsync("A");
        SetInput(vm);
        await vm.AddManualAsync();
        Assert.False(vm.CanEdit);
        Assert.Contains("未覆盖", vm.Status, StringComparison.Ordinal);
        await vm.AddManualAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LostResponseRetriesSameRequestWithoutOptimisticLocalSuccess()
    {
        var ids = new List<string>();
        using var api = Client(async request =>
        {
            if (request.Method == HttpMethod.Get) return Ok(Snapshot("A", 0));
            var input = (await request.Content!.ReadFromJsonAsync<ImportCandidateRosterRequest>(JsonDefaults.Create()))!;
            ids.Add(input.RequestId);
            Assert.Equal(0, input.ExpectedVersion);
            Assert.Equal("000001", Assert.Single(input.Rows).AdmissionNo);
            if (ids.Count == 1) throw new HttpRequestException("Synthetic lost response.");
            return Ok(new RosterImportResult(true, 1, 0, Snapshot("A", 1, Person("A")), []));
        });
        using var vm = Model(api);
        await vm.OpenAsync("A");
        SetInput(vm);
        await vm.AddManualAsync();
        Assert.Empty(vm.Entries);
        Assert.Equal("000001", vm.AdmissionNo);
        Assert.Contains("尚未确认", vm.Status, StringComparison.Ordinal);
        await vm.AddManualAsync();
        Assert.Single(vm.Entries);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(1, vm.LoadedVersion);
    }

    [Fact]
    public async Task ServerRejectingWholeBatchDoesNotAddAnyLocalPeople()
    {
        using var api = Client(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Ok(Snapshot("A", 0))
            : Ok(new RosterImportResult(false, 0, 0, Snapshot("A", 0), [new(1, "admissionNo", "合成服务器校验冲突。")]))));
        using var vm = Model(api);
        await vm.OpenAsync("A");
        SetInput(vm);
        await vm.AddManualAsync();
        Assert.Empty(vm.Entries);
        Assert.Contains("整批未写入", vm.Status, StringComparison.Ordinal);
        Assert.Equal(0, vm.LoadedVersion);
    }

    [Fact]
    public async Task WrongExamSnapshotIsRejectedBeforeItBecomesEditable()
    {
        using var api = Client(_ => Task.FromResult(Ok(Snapshot("wrong", 1, Person("wrong")))));
        using var vm = Model(api);
        await vm.OpenAsync("A");
        Assert.False(vm.CanEdit);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task ReopeningModelReadsPersistedServerSnapshotInsteadOfLocalCache()
    {
        var snapshot = Snapshot("A", 5, Person("A"));
        using var api = Client(_ => Task.FromResult(Ok(snapshot)));
        using (var first = Model(api)) { await first.OpenAsync("A"); Assert.Single(first.Entries); }
        using var reopened = Model(api);
        await reopened.OpenAsync("A");
        Assert.Equal("000001", Assert.Single(reopened.Entries).AdmissionNo);
        Assert.Equal(5, reopened.LoadedVersion);
    }

    private static void SetInput(RosterViewModel vm)
    { vm.Name = "合成考生"; vm.ClassText = "001"; vm.AdmissionNo = "000001"; }
    private static CandidateRosterEntry Person(string id) => new("candidate-" + id, "合成考生", "001", "000001");
    private static CandidateRosterSnapshot Snapshot(string id, long version, params CandidateRosterEntry[] entries) => new(id, version, entries);
    private static RosterViewModel Model(AdminApiClient api) => new(new RosterWorkbookService(), new Dialogs(), api, _ => true);
    private static TaskCompletionSource<HttpResponseMessage> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AdminApiClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var api = new AdminApiClient(new Handler(respond));
        api.Configure("https://localhost/", "synthetic-session", "synthetic-operator");
        return api;
    }
    private static HttpResponseMessage Ok<T>(T result) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "roster-context", result, null), options: JsonDefaults.Create()) };
    private static HttpResponseMessage Fail(HttpStatusCode status) => new(status)
    { Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "roster-context", null,
        new ApiError("SYNTHETIC_FAILURE", "服务器名册版本变化或暂时不可用。")), options: JsonDefaults.Create()) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
    private sealed class Dialogs : IFileDialogService
    {
        public string? OpenExcelFile() => null;
        public string? SaveExcelFile(string defaultFileName) => null;
        public string? OpenJsonFile() => null;
    }
}
