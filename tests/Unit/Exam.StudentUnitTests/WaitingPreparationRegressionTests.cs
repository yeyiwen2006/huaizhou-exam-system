using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Student;
using Exam.Desktop.Shared.Controls;
using Exam.Student;
using Exam.Student.Core;
using Exam.Student.Views;

namespace Exam.StudentUnitTests;

public sealed partial class ExamPackageManagerTests
{
    [Fact]
    [Trait("Category", "Wpf")]
    public async Task WaitingPageShowsRejectionAndRefreshesScheduleBeforeConfirmation()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.PreparationError = new ApiError(ParticipationErrorCodes.EntryNotAllowed,
            "试卷尚未发布，当前不可进入该考试。");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke((Action)(async () =>
            {
                try
                {
                    var application = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml")
                    });
                    App.Services.ConfigureDevice(fixture.Device);
                    App.Services.Session.OnLoggedIn(fixture.Session);
                    var localPath = Path.Combine(fixture.Root, "answers.db");
                    Directory.CreateDirectory(fixture.Root);
                    File.WriteAllBytes(localPath, []);
                    File.WriteAllText(localPath + ".identity", System.Text.Json.JsonSerializer.Serialize(
                        new { schemaVersion = 3, databaseId = Guid.NewGuid().ToString("N") }));
                    using var local = new SqliteLocalStateStore(localPath);
                    var runtime = new StudentExamRuntime(fixture.Manager(), fixture.Device, local, "candidate");
                    App.Services.Session.RegisterExam(runtime);
                    var overview = new ExamOverview("exam", "合成候考检查", DateTimeOffset.Now.AddMinutes(10),
                        DateTimeOffset.Now.AddMinutes(30), ["合成科目"], ExamLifecycle.Scheduled);
                    var handler = new ScheduleOverviewHandler(overview);
                    using var overviewApi = new HttpStudentApi(handler);
                    overviewApi.ConfigureConnection(fixture.Device);
                    var page = new WaitingExamPage(new MainWindow.Navigation(new ContentControl()),
                        overview, overviewApi);
                    try
                    {
                        var prepare = typeof(WaitingExamPage).GetMethod("PrepareAndDownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        await (Task)prepare.Invoke(page, null)!;
                        Assert.True(runtime.LocalRecoveryChecked);
                        Assert.Null(runtime.LocalStateError);
                        Assert.True(((Banner)page.FindName("ErrorBanner")).IsOpen);
                        Assert.Contains("当前不可进入", ((Banner)page.FindName("ErrorBanner")).Message);
                        Assert.False(((BusyOverlay)page.FindName("BusyLayer")).IsBusy);
                        Assert.False(((Button)page.FindName("ConfirmButton")).IsEnabled);
                        Assert.True(((Button)page.FindName("RetryDownloadButton")).IsEnabled);
                        Assert.Equal(0, fixture.Api.DownloadCalls);
                        handler.Current = overview with { ScheduledStart = overview.ScheduledStart.AddMinutes(20), ScheduledEnd = overview.ScheduledEnd.AddMinutes(20) };
                        var refresh = typeof(WaitingExamPage).GetMethod("RefreshScheduleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        await (Task)refresh.Invoke(page, null)!;
                        Assert.Contains(handler.Current.ScheduledStart.ToString("yyyy-MM-dd HH:mm"), ((TextBlock)page.FindName("TimeText")).Text);
                        Assert.Contains(handler.Current.ScheduledEnd.ToString("yyyy-MM-dd HH:mm"), ((TextBlock)page.FindName("TimeText")).Text);
                        Assert.False(runtime.Manager.ExamStarted);
                        Assert.Null(runtime.Manager.Participation);
                        var selection = new ExamSelectionPage(new MainWindow.Navigation(new ContentControl()), overviewApi);
                        try
                        {
                            var reload = typeof(ExamSelectionPage).GetMethod("LoadExamsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                            await (Task)reload.Invoke(selection, [true])!;
                            var row = Assert.Single(((ItemsControl)selection.FindName("ExamList")).Items.Cast<ExamSelectionPage.ExamRow>());
                            Assert.Equal(handler.Current.ScheduledStart, row.Overview.ScheduledStart);
                            Assert.False(((BusyOverlay)selection.FindName("Busy")).IsBusy);
                        }
                        finally { selection.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
                    }
                    finally
                    {
                        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                        await App.Services.Session.ReleaseExamAsync();
                        App.Services.Session.Logout();
                    }
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private sealed class ScheduleOverviewHandler(ExamOverview overview) : HttpMessageHandler
    {
        public ExamOverview Current { get; set; } = overview;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/v1/student/exams", request.RequestUri!.AbsolutePath);
            IReadOnlyList<StudentExamOverview> result = [new(Current.ExamId, Current.Name, Current.ScheduledStart,
                Current.ScheduledEnd, Current.Subjects, Current.Lifecycle)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<IReadOnlyList<StudentExamOverview>>("server", "primary", 1,
                    "schedule-test", result, null), options: JsonDefaults.Create())
            });
        }
    }
}
