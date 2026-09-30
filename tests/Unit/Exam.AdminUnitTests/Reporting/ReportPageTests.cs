using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Admin.Reporting;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;
using Exam.Domain.Reporting;

namespace Exam.AdminUnitTests.Reporting;

// Synthetic envelopes and participants only; the real API/CLI is covered by PostgreSQL integration tests.
public sealed partial class ReportViewReviewTests
{
    [Fact]
    public async Task Page_ReadyPollInvalidationAuthorizationFailureAndRecovery() => await OnUiThreadAsync(async () =>
    {
        var status = ReadyStatus(); var denied=false;
        using var handler=new PageHandler(request=>Task.FromResult(denied ?
            new HttpResponseMessage(HttpStatusCode.Unauthorized) : request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal)
                ? PageResponse(status) : PageResponse(Published(status))));
        using var api=PageApi(handler); using var page=Page(api);
        await page.RefreshAsync();
        Assert.True(page.Session.HasTables); Assert.True(page.ExportCommand.CanExecute(null));
        status=status with { SnapshotId="new-version",GradingVersion=2 };
        await page.PollAsync();
        Assert.False(page.Session.HasTables); Assert.False(page.ExportCommand.CanExecute(null));
        await page.RefreshAsync(); Assert.True(page.Session.HasTables);
        denied=true; await page.PollAsync(); Assert.False(page.Session.HasTables);
        denied=false; status=status with { State=ReportState.Collecting,SnapshotId=null,CollectedCount=0,GradedCount=0,
            MissingParticipants=[new("synthetic-candidate","0001","合成考生","01")] };
        await page.RefreshAsync(); Assert.Single(page.MissingParticipants); Assert.False(page.Session.HasTables);
        status=ReadyStatus(); await page.RefreshAsync(); Assert.True(page.Session.HasTables);
        api.Disconnect(); Assert.False(page.Session.HasTables); Assert.False(page.ExportCommand.CanExecute(null));
    });

    [Fact]
    public async Task Page_ExamSwitchAndCancelRejectLateResponses() => await OnUiThreadAsync(async () =>
    {
        var response=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler=new PageHandler(_=>response.Task);
        using var api=PageApi(handler); using var page=Page(api);
        var pending=page.RefreshAsync();
        Assert.True(page.CancelCommand.CanExecute(null));
        page.CancelCommand.Execute(null); page.ExamId="synthetic-other-exam";
        response.SetResult(PageResponse(ReadyStatus())); await pending;
        Assert.False(page.Session.HasTables); Assert.False(page.ExportCommand.CanExecute(null));
    });

    [Fact]
    public async Task Page_RejectsCrossExamStatusAndCancelledFileDialogPreservesTables() => await OnUiThreadAsync(async () =>
    {
        var status=ReadyStatus();
        using var handler=new PageHandler(request=>Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal)
            ? PageResponse(status) : PageResponse(Published(status))));
        using var api=PageApi(handler); using var page=Page(api);
        await page.RefreshAsync(); await page.ExportAsync();
        Assert.True(page.Session.HasTables); Assert.Contains("取消",page.Status);
        status=status with { ExamId="synthetic-other-exam" };
        await page.RefreshAsync(); Assert.False(page.Session.HasTables);
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    public async Task Page_FormalViewRendersSyntheticReportAndInvalidationClearsIt(double scale) => await OnUiThreadAsync(async () =>
    {
        var status=ReadyStatus();
        using var handler=new PageHandler(request=>Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/status",StringComparison.Ordinal)
            ? PageResponse(status) : PageResponse(Published(status))));
        using var api=PageApi(handler); using var page=Page(api);
        var view=new ReportPageView { DataContext=page };
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source=new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        var buttonStyle=new Style(typeof(System.Windows.Controls.Button),(Style)view.Resources["Btn.Secondary"]);
        buttonStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,0,8,8)));
        buttonStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty,96d));
        view.Resources["ActionButton"]=buttonStyle;
        var window=new Window { Content=view,Width=1366/scale,Height=768/scale,Left=-10000,Top=-10000,
            ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None };
        try
        {
            window.Show(); await page.RefreshAsync(); Drain(); window.UpdateLayout();
            Assert.Contains(Descendants<System.Windows.Controls.Button>(view),button=>Equals(button.Content,"导出 Excel") && button.IsEnabled);
            Assert.Contains(Descendants<System.Windows.Controls.TextBlock>(view),text=>text.Text=="Synthetic candidate");
            var directory=Environment.GetEnvironmentVariable("EXAM_REPORT_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                var image=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth*scale),(int)Math.Ceiling(view.ActualHeight*scale),
                    96*scale,96*scale,PixelFormats.Pbgra32); image.Render(view);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file=File.Create(Path.Combine(directory,$"report-page-{(int)(scale*100)}.png")); encoder.Save(file);
            }
            page.IsPageActive=false; Drain(); Assert.False(page.Session.HasTables);
        }
        finally { window.Close(); }
    });

    private static ReportStatus ReadyStatus()=>new("synthetic-exam",ReportState.Ready,"合成报表（非真实考生）已开放。",1,1,1,1,1,1,"synthetic-current",[]);
    private static PublishedReport Published(ReportStatus status)=>new(1,status,
        JsonSerializer.SerializeToElement(ReportTableSessionTests.Result(2),ReportJson.Create()));
    private static HttpResponseMessage PageResponse<T>(T result)=>new(HttpStatusCode.OK)
    { Content=JsonContent.Create(new ApiEnvelope<T>("synthetic-server","synthetic-node",1,"synthetic-request",result,null),options:JsonDefaults.Create()) };
    private static AdminApiClient PageApi(HttpMessageHandler handler)
    { var api=new AdminApiClient(handler); api.Configure("https://localhost/","synthetic-token","synthetic-operator"); return api; }
    private static ReportPageViewModel Page(AdminApiClient api)=>new(api,new CancelFileDialog())
    { ExamId="synthetic-exam",ConnectionReady=true,IsPageActive=true };
    private sealed class CancelFileDialog:IFileDialogService
    { public string? OpenExcelFile()=>null; public string? SaveExcelFile(string suggestedName)=>null; }
    private sealed class PageHandler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request); }
    private static async Task OnUiThreadAsync(Func<Task> action)
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async ()=>
            {
                try { await action(); completed.SetResult(); } catch(Exception error) { completed.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
