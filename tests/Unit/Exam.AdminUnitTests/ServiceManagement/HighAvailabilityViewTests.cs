using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Admin;
using Exam.Admin.ServiceManagement;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.HighAvailability;
using Exam.Desktop.Shared.Controls;

namespace Exam.AdminUnitTests.ServiceManagement;

[Collection("Admin WPF")]
[Trait("Category", "Wpf")]
public sealed class HighAvailabilityViewTests
{
    [Theory]
    [InlineData(1d, 1120d, 720d)]
    [InlineData(1.25d, 1120d, 720d)]
    [InlineData(1.5d, 1120d, 720d)]
    [InlineData(1d, 1366d, 768d)]
    [InlineData(1.25d, 1366d, 768d)]
    [InlineData(1.5d, 1366d, 768d)]
    public Task RealHaControlsShowAllProtectionStatesAtEachRenderScale(double scale, double width, double height) => Sta(() =>
    {
        var connection = new HaGuiConnection(); using var model = HighAvailabilityWorkspaceTests.Model(connection);
        var view = new ServiceManagementView(model);
        var window = new Window { Content = view, Width = width, Height = height, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -5000, Top = -5000 };
        Theme(window);
        try
        {
            window.Show(); model.LoginAsync("synthetic").GetAwaiter().GetResult(); Drain();
            var card = (FrameworkElement)view.FindName("HighAvailabilityCard"); var scroller = (ScrollViewer)view.FindName("PageScroller");
            card.BringIntoView(); Drain();
            Assert.True(scroller.ScrollableHeight > 0);
            Assert.Equal(2, model.Nodes.Count);
            var operations = Descendants<Button>(card).Where(button => button.Command == model.TakeoverCommand ||
                button.Command == model.EmergencyCommand || button.Command == model.RebuildCommand || button.Command == model.ReprotectCommand).ToArray();
            Assert.Equal(4, operations.Length);
            Save(view, "synchronous", scale, width, height);
            connection.Mode = ProtectionMode.EmergencySingleNode; model.RefreshAsync().GetAwaiter().GetResult(); Drain();
            Assert.True(model.ShowProtectionWarning); Assert.Contains("单机应急", model.ProtectionText); Save(view, "emergency", scale, width, height);
            connection.Mode = ProtectionMode.Recovering; model.RefreshAsync().GetAwaiter().GetResult(); Drain();
            Assert.Contains("恢复中", model.ProtectionText); Save(view, "recovering", scale, width, height);
            connection.Unavailable = true; model.RefreshAsync().GetAwaiter().GetResult(); Drain();
            Assert.True(model.ShowProtectionWarning); Assert.Contains("未核实", model.ProtectionText);
            Assert.All(operations, button => Assert.False(button.IsEnabled)); Save(view, "unknown", scale, width, height);
        }
        finally { view.Detach(); window.Close(); }
    });

    [Fact]
    public Task ShellKeepsAnEmergencyWarningAfterReturningToBusinessLoginAndLogout() => Sta(() =>
    {
        var connection = new HaGuiConnection { Mode = ProtectionMode.EmergencySingleNode };
        using var model = HighAvailabilityWorkspaceTests.Model(connection);
        var business = new MainViewModel(new AdminApiClient(new BusinessSessionFixture()));
        var window = new MainWindow(business, model) { ShowInTaskbar = false, ShowActivated = false, Left = -5000, Top = -5000,
            Width = 1120, Height = 720, WindowStyle = WindowStyle.None };
        Theme(window);
        try
        {
            window.Show(); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            business.Connection.ServerAddress = "exam.example.test";
            business.Connection.Username = "synthetic-operator"; business.Connection.Password = "synthetic-password";
            business.Connection.ConnectCommand.Execute(null);
            for (var i = 0; i < 200 && !business.Connection.IsConnected; i++) { Drain(); Thread.Sleep(10); }
            Assert.True(business.Connection.IsConnected, business.Connection.Status);
            window.OpenManagement(); Drain(); Assert.True(model.IsSignedIn);
            window.ReturnToBusiness(); Drain(); Assert.Equal(AdminWorkspaceRoute.BusinessWorkspace, window.WorkspaceRoute);
            Assert.Contains(Descendants<Banner>(window), banner => banner.IsOpen && banner.Message.Contains("此前", StringComparison.Ordinal));
            Save(window, "shell-warning", 1d, 1120d, 720d);
            business.Connection.LogoutCommand.Execute(null);
            for (var i = 0; i < 200 && business.Connection.IsConnected; i++) { Drain(); Thread.Sleep(10); }
            Drain(); Assert.Equal(AdminWorkspaceRoute.BusinessLogin, window.WorkspaceRoute); Assert.False(model.IsSignedIn);
            Assert.Contains(Descendants<Banner>(window), banner => banner.IsOpen && banner.Message.Contains("未核实", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    });

    private static void Theme(Window window)
    { if (System.Windows.Application.Current is null) window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") }); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Save(FrameworkElement element, string name, double scale, double width, double height)
    {
        var root = Environment.GetEnvironmentVariable("EXAM_UI_SCREENSHOT_DIRECTORY"); if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(root, $"f04-ha-{name}-{width:0}x{height:0}-{scale * 100:0}.png")); encoder.Save(output);
    }
    private static Task Sta(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); result.SetResult(); } catch (Exception error) { result.SetException(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
}
