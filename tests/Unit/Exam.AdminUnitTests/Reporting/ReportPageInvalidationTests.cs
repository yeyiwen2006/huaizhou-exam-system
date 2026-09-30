using System.Net.Http;
using Exam.Admin.Reporting;
using Exam.Admin.Services;

namespace Exam.AdminUnitTests.Reporting;

public sealed partial class ReportViewReviewTests
{
    [Fact]
    public async Task Page_PollTimeoutHidesOldTablesEvenWhenTransportIgnoresCancellation() => await OnUiThreadAsync(async () =>
    {
        var hang=false; var late=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler=new PageHandler(request=>hang ? late.Task : Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal) ? PageResponse(ReadyStatus()) : PageResponse(Published(ReadyStatus()))));
        using var api=PageApi(handler); using var page=Page(api);
        await page.RefreshAsync(); Assert.True(page.Session.HasTables);
        hang=true; await page.PollAsync();
        Assert.False(page.Session.HasTables); Assert.False(page.ExportCommand.CanExecute(null)); Assert.Contains("超时",page.Status);
        late.SetResult(PageResponse(ReadyStatus()));
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Page_PollInvalidatesPendingTableLoadAndRejectsLateOldSnapshot(bool automatic) => await OnUiThreadAsync(async () =>
    {
        var status=ReadyStatus(); var old=status; var loads=0;
        var late=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler=new PageHandler(request=>request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal)
            ? Task.FromResult(PageResponse(status)) : ++loads==1 ? late.Task : Task.FromResult(PageResponse(Published(status))));
        using var api=PageApi(handler); using var page=Page(api);
        var pending=automatic ? page.PollAsync() : page.RefreshAsync(); status=status with { SnapshotId="synthetic-new",GradingVersion=2 };
        await page.PollAsync(); late.SetResult(PageResponse(Published(old))); await pending;
        Assert.False(page.Session.HasTables); Assert.False(page.ExportCommand.CanExecute(null));
        await page.RefreshAsync(); Assert.True(page.Session.HasTables);
    });
    [Fact]
    public async Task Page_NewerPollBeforeInitialStatusRejectsLateOldStatus() => await OnUiThreadAsync(async () =>
    {
        var initial=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests=0; var tableRequests=0; var old=ReadyStatus(); var current=old with { SnapshotId="synthetic-new",GradingVersion=2 };
        using var handler=new PageHandler(request=>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal))
            { tableRequests++; return Task.FromResult(PageResponse(Published(old))); }
            return ++requests==1 ? initial.Task : Task.FromResult(PageResponse(current));
        });
        using var api=PageApi(handler); using var page=Page(api);
        var pending=page.RefreshAsync(); await page.PollAsync(); initial.SetResult(PageResponse(old)); await pending;
        Assert.False(page.Session.HasTables); Assert.Equal(0,tableRequests);
    });
    [Fact]
    public async Task Page_UserCancelPausesAutomaticReloadUntilManualRefresh() => await OnUiThreadAsync(async () =>
    {
        var late=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var loads=0;
        using var handler=new PageHandler(request=>request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal)
            ? Task.FromResult(PageResponse(ReadyStatus())) : ++loads==1 ? late.Task : Task.FromResult(PageResponse(Published(ReadyStatus()))));
        using var api=PageApi(handler); using var page=Page(api);
        var pending=page.RefreshAsync(); page.CancelCommand.Execute(null);
        late.SetResult(PageResponse(Published(ReadyStatus()))); await pending; await page.PollAsync();
        Assert.Equal(1,loads); Assert.False(page.Session.HasTables);
        await page.RefreshAsync(); Assert.Equal(2,loads); Assert.True(page.Session.HasTables);
    });
    [Fact]
    public async Task Page_ContextChangedDuringSaveDialogNeverDownloads() => await OnUiThreadAsync(async () =>
    {
        var downloads=0;
        using var handler=new PageHandler(request=>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/xlsx",StringComparison.Ordinal)) downloads++;
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/status",StringComparison.Ordinal)
                ? PageResponse(ReadyStatus()) : PageResponse(Published(ReadyStatus())));
        });
        using var api=PageApi(handler); ReportPageViewModel? page=null;
        var dialog=new ChangedContextDialog(()=>page!.CancelCommand.Execute(null));
        using (page=new ReportPageViewModel(api,dialog) { ExamId="synthetic-exam",ConnectionReady=true,IsPageActive=true })
        {
            await page.RefreshAsync(); await page.ExportAsync();
            Assert.Equal(0,downloads); Assert.False(page.Session.HasTables);
        }
    });
    private sealed class ChangedContextDialog(Action change):IFileDialogService
    {
        public string? OpenExcelFile()=>null;
        public string? SaveExcelFile(string suggestedName) { change(); return "synthetic-unused.xlsx"; }
    }
}
