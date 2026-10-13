using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Admin;
using Exam.Admin.ServiceManagement;
using Exam.Admin.ViewModels;
using Exam.Admin.Services;
using Exam.Admin.Views;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;
using Exam.Contracts.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

[Collection("Admin WPF")]
[Trait("Category", "Wpf")]
public sealed class ManagementViewTests
{
    [Fact]
    public Task ManagementCannotInterruptRegistrationBeforeBusinessLogin() => Sta(() =>
    {
        using var model = Synthetic();
        var business = new MainViewModel(new AdminApiClient(new SyntheticRegistration()));
        var window = new MainWindow(business, model); Theme(window);
        window.ShowInTaskbar = false; window.ShowActivated = false; window.Left = -4000;
        try
        {
            window.Show(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            business.Connection.Username = "synthetic-operator"; business.Connection.DisplayName = "合成考务账号";
            business.Connection.ServerAddress = "exam.example.test";
            business.Connection.Password = "synthetic"; business.Connection.ConfirmPassword = "synthetic";
            business.Connection.RegisterCommand.Execute(null);
            for (var i = 0; i < 200 && !business.Connection.RegisterCommand.CanExecute(null); i++)
            { Drain(); Thread.Sleep(10); }
            Drain(); Assert.True(business.Connection.ShowRegistrationSuccess);
            Assert.IsType<RegistrationSuccessPage>(((ContentControl)window.FindName("PageRegion")).Content);
            window.OpenManagement(); Drain(); Assert.True(business.Connection.ShowRegistrationSuccess);
            window.ReturnToBusiness(); Drain();
            Assert.IsType<RegistrationSuccessPage>(((ContentControl)window.FindName("PageRegion")).Content);
            business.Connection.AcknowledgeRegistrationCommand.Execute(null); Drain();
            Assert.IsType<AdminLoginPage>(((ContentControl)window.FindName("PageRegion")).Content);
        }
        finally { window.Close(); }
    });

    private sealed class SyntheticRegistration : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var account = new OperatorAccount("synthetic-operator", "合成考务账号", OperatorStatus.Pending, false, DateTimeOffset.UtcNow, null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(
                new ApiEnvelope<RegisterOperatorResult>("synthetic-server", "synthetic-node", 1, "synthetic-registration", new(account), null), options: JsonDefaults.Create()) });
        }
    }

    [Fact]
    public async Task MainWindowUsesBusinessIdentityForManagementAndClearsItOnLogout() => await Sta(() =>
    {
        using var model = Synthetic();
        var business = new MainViewModel(new AdminApiClient(new BusinessSessionFixture()));
        var window = new MainWindow(business, model); Theme(window);
        window.ShowInTaskbar = false; window.ShowActivated = false; window.Left = -4000;
        try
        {
            window.Show(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); Drain();
            Assert.Equal(AdminWorkspaceRoute.BusinessLogin, window.WorkspaceRoute);
            Assert.DoesNotContain(Descendants<Button>(window), b => b.IsVisible && Equals(b.Content, "服务器管理"));
            window.OpenManagement(); Assert.Equal(AdminWorkspaceRoute.BusinessLogin, window.WorkspaceRoute);
            business.Connection.ServerAddress = "exam.example.test";
            business.Connection.Username = "synthetic-operator"; business.Connection.Password = "synthetic-password";
            business.Connection.ConnectCommand.Execute(null);
            for (var i = 0; i < 200 && !business.Connection.IsConnected; i++) { Drain(); Thread.Sleep(10); }
            Assert.True(business.Connection.IsConnected, business.Connection.Status);
            window.OpenManagement(); Drain();
            Assert.Equal(AdminWorkspaceRoute.LocalManagement, window.WorkspaceRoute);
            Assert.True(model.IsPageActive); Assert.True(model.IsSignedIn);
            var view = Assert.IsType<ServiceManagementView>(((ContentControl)window.FindName("PageRegion")).Content);
            Assert.Empty(Descendants<PasswordBox>(view));
            Assert.Equal(Visibility.Collapsed, ((TabControl)window.FindName("MainTabArea")).Visibility);
            business.Connection.LogoutCommand.Execute(null);
            for (var i = 0; i < 200 && business.Connection.IsConnected; i++) { Drain(); Thread.Sleep(10); }
            Drain(); Assert.False(model.IsSignedIn); Assert.False(model.IsPageActive);
            Assert.Equal(AdminWorkspaceRoute.BusinessLogin, window.WorkspaceRoute);
            Assert.IsType<AdminLoginPage>(((ContentControl)window.FindName("PageRegion")).Content);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1d, 1366d, 768d)]
    [InlineData(1.25d, 1366d, 768d)]
    [InlineData(1.5d, 1366d, 768d)]
    [InlineData(1d, 1120d, 720d)]
    [InlineData(1.25d, 1120d, 720d)]
    [InlineData(1.5d, 1120d, 720d)]
    public async Task ServiceViewUsesRealBindingsAndScrollableLayoutAtEachScale(double scale, double width, double height) => await Sta(() =>
    {
        using var model = Synthetic();
        var view = new ServiceManagementView(model);
        var window = new Window { Content = view, Width = width, Height = height, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Left = -5000, Top = -5000 };
        Theme(window);
        using var output = new StringWriter(); using var listener = new TextWriterTraceListener(output);
        var trace = PresentationTraceSources.DataBindingSource; var previous = trace.Switch.Level;
        trace.Switch.Level = SourceLevels.Error; trace.Listeners.Add(listener);
        try
        {
            window.Show(); Drain(); window.UpdateLayout();
            Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "重新连接"));
            Assert.Empty(Descendants<PasswordBox>(view));
            var connectionSettings = (Expander)view.FindName("ConnectionSettingsExpander");
            Assert.False(connectionSettings.IsExpanded);
            Assert.DoesNotContain(Descendants<TextBox>(view), b => b.IsVisible);
            Assert.False(model.ReconnectCommand.CanExecute(null));
            connectionSettings.IsExpanded = true; Drain();
            Assert.Equal(3, Descendants<TextBox>(view).Count(b => b.IsVisible));
            connectionSettings.IsExpanded = false; Drain();
            Assert.All(Descendants<TextBox>(view).Where(b => b.IsVisible), b => Assert.True(b.ActualWidth >= 120));
            Save(view, scale, width, height, "login");
            model.SetBusinessSession(BusinessSessionFixture.CaptureAsync().GetAwaiter().GetResult());
            Drain(); window.UpdateLayout();
            Assert.True(model.IsSignedIn);
            Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "查询请求处理状态") && b.Command is not null);
            Assert.DoesNotContain(Descendants<Button>(view), b => Equals(b.Content, "启动业务") || Equals(b.Content, "停止业务") || Equals(b.Content, "按原内容重试") || Equals(b.Content, "续办原恢复"));
            Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "按此备份恢复") && b.Command is not null);
            var scroller = (ScrollViewer)view.FindName("PageScroller");
            Assert.True(scroller.ScrollableHeight > 0); Assert.Equal(0, scroller.ScrollableWidth);
            Save(view, scale, width, height, "service");
            scroller.ScrollToEnd(); Drain(); window.UpdateLayout(); Save(view, scale, width, height, "recovery");
            var operation = (TextBlock)view.FindName("PersistentOperationText");
            Assert.Null(view.FindName("PersistentPendingText")); Assert.False(operation.IsVisible);
            model.RequestIdToQuery = "synthetic-history"; model.QueryAsync().GetAwaiter().GetResult(); Drain();
            Assert.True(operation.IsVisible);
            Assert.DoesNotContain(scroller, Ancestors(operation));
            Assert.True(operation.TranslatePoint(new Point(), view).Y < scroller.TranslatePoint(new Point(), view).Y);
            listener.Flush(); Assert.DoesNotContain("Error:", output.ToString());
            window.Content = null; Drain(); Assert.False(model.IsPageActive);
        }
        finally { trace.Listeners.Remove(listener); trace.Switch.Level = previous; window.Close(); }
    });

    [Fact]
    public Task MissingConnectionShowsGuidanceAndDisablesLogin() => Sta(() =>
    {
        using var model = new ServiceManagementViewModel(_ => new SyntheticConnection(), new EmptyStore());
        var view = new ServiceManagementView(model);
        var window = new Window { Content = view, Width = 1120, Height = 720,
            ShowInTaskbar = false, ShowActivated = false, Left = -5000, Top = -5000 };
        Theme(window);
        try
        {
            window.Show(); Drain(); window.UpdateLayout();
            Assert.False(model.ReconnectCommand.CanExecute(null));
            Assert.Contains(Descendants<TextBlock>(view), text => text.Text.Contains("联系系统部署人员"));
            var import = Assert.Single(Descendants<Button>(view), button => Equals(button.Content, "导入连接配置"));
            Assert.True(import.IsEnabled);
            Save(view, 1, 1120, 720, "missing-connection");
        }
        finally { window.Close(); }
    });

    private static ServiceManagementViewModel Synthetic() => new(_ => new SyntheticConnection(), new EmptyStore())
    { Address = "https://localhost:9443/", ServerId = "synthetic-server", NodeId = "synthetic-node", Username = "synthetic-operator" };
    private static void Theme(Window window) => window.Resources.MergedDictionaries.Add(new ResourceDictionary
    { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
    private static void Save(FrameworkElement view, double scale, double width, double height, string state)
    {
        var directory = Environment.GetEnvironmentVariable("EXAM_UI_SCREENSHOT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"f04-{state}-{(int)width}x{(int)height}-{(int)(scale * 100)}.png")); encoder.Save(output);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static Task Sta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        { try { action(); completed.SetResult(); } catch (Exception error) { completed.SetException(error); } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completed.Task;
    }
    private sealed class EmptyStore : IPendingManagementStore
    { public PendingManagementRequest? Load(ManagementTarget target) => null; public void Save(PendingManagementRequest request) { } public void Clear(PendingManagementRequest request) { } }
    private sealed class SyntheticConnection : IManagementConnection
    {
        public ManagementTarget Target => new("https://localhost:9443/", "synthetic-server", "synthetic-node");
        public string? Actor => "synthetic-operator";
        public Task LoginAsync(string username, string password, CancellationToken token) => Task.CompletedTask;
        public Task UseBusinessSessionAsync(AdminSessionSnapshot session, CancellationToken token) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
        public Task<ManagementReply<ManagementStatus>> StatusAsync(CancellationToken token) => Task.FromResult(Reply(new ManagementStatus(
            BusinessServiceStatus.Stopped, BusinessReadiness.NotReady,
            new(Target.ServerId, Target.NodeId, "127.0.0.1", 8443, "C:\\synthetic-data", true, 7),
            [new("backup-integrity", false, "合成检查", true), new("node-certificate", true, "合成检查", true)])));
        public Task<ManagementReply<IReadOnlyList<ManagementAuditEntry>>> AuditAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ManagementAuditEntry>>([]));
        public Task<ManagementReply<IReadOnlyList<ProtectedRuntimeLogEntry>>> LogsAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ProtectedRuntimeLogEntry>>([]));
        public Task<ManagementReply<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string body, CancellationToken token) => throw new NotSupportedException();
        public Task<ManagementReply<ManagementOperation>> FindAsync(string id, CancellationToken token) => Task.FromResult(Reply(new ManagementOperation(
            id, Target.ServerId, Target.NodeId, Actor!, ManagementOperationKind.Checks, "synthetic-hash", ManagementOperationPhase.Completed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        private ManagementReply<T> Reply<T>(T result) => new(HttpStatusCode.OK, new(Target.ServerId, Target.NodeId, null, false, null, "synthetic-correlation", result, null));
        public void Dispose() { }
    }
}
