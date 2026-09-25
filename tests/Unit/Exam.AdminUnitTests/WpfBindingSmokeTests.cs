using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Exam.Admin;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed class WpfBindingSmokeTests
{
    [Fact]
    [Trait("Category", "Wpf")]
    public async Task MainWindow_AllTabsLoad_QuestionTypeCanBeChanged_WithoutBindingErrors()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            App? application = null;
            MainWindow? window = null;
            using var output = new StringWriter();
            using var listener = new TextWriterTraceListener(output);
            var source = PresentationTraceSources.DataBindingSource;
            var previousLevel = source.Switch.Level;
            try
            {
                source.Switch.Level = SourceLevels.Error;
                source.Listeners.Add(listener);
                application = new App();
                application.InitializeComponent();
                // The testhost, unlike Exam.Admin.exe, is the entry assembly. Qualify the same startup view.
                application.StartupUri = new Uri("pack://application:,,,/Exam.Admin;component/MainWindow.xaml", UriKind.Absolute);
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // Exercise the real App.xaml StartupUri instead of creating a second window.
                DrainDispatcher();
                window = Assert.IsType<MainWindow>(application.MainWindow);
                window.ShowInTaskbar = false;
                window.Left = -2000;
                window.Top = -2000;
                window.UpdateLayout();
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                var connect = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Connection.ConnectCommand));
                Assert.Equal((Thickness)application.FindResource("Padding.Control"), connect.Padding);
                var subjectGrid = Assert.Single(Descendants<DataGrid>(window), grid => ReferenceEquals(grid.ItemsSource, viewModel.Exam.Subjects));
                Assert.Same(application.FindResource("Brush.Surface.Alt"), subjectGrid.AlternatingRowBackground);
                var horizontalBar = new ScrollBar { Style = (Style)application.FindResource(typeof(ScrollBar)), Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, ViewportSize = 10 };
                horizontalBar.Measure(new Size(200, 8));
                horizontalBar.Arrange(new Rect(0, 0, 200, 8));
                var track = Assert.IsType<Track>(horizontalBar.Template.FindName("PART_Track", horizontalBar));
                Assert.IsType<Thumb>(track.Thumb);
                Assert.False(track.IsDirectionReversed);
                Assert.True(double.IsNaN(horizontalBar.Width));
                Assert.Equal(8, horizontalBar.Height);
                Assert.Same(ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
                Assert.Same(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
                var group = viewModel.Exam.SelectedSubject!.Groups[0];
                var typeSelector = Assert.Single(Descendants<ComboBox>(window), combo => ReferenceEquals(combo.DataContext, group));
                Assert.Equal(new[] { "single", "multiple", "indeterminate" }, typeSelector.Items.Cast<string>());
                typeSelector.SelectedItem = "multiple";
                DrainDispatcher();
                Assert.Equal("multiple", group.Type);
                var instructions = Assert.Single(Descendants<TextBox>(window), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "Exam.InstructionsText");
                Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(instructions, TextBox.TextProperty)!.Mode);

                var tabs = Assert.Single(Descendants<TabControl>(window));
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    tabs.SelectedItem = tab;
                    DrainDispatcher();
                    window.UpdateLayout();
                    if ((string)tab.Header == "C03 激活与房间组")
                        Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "重命名草稿") && button.Command is not null);
                    if ((string)tab.Header == "C04 监控与处置")
                        Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "查询收齐事件") && button.Command is not null);
                    if ((string)tab.Header == "C05 清单静态预检")
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "选择 manifest.json 并静态预检") &&
                            ReferenceEquals(button.Command, viewModel.ManifestPreflight.SelectManifestCommand));
                }
                listener.Flush();
                Assert.Equal(string.Empty, output.ToString());

            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try { window?.Close(); application?.Shutdown(); }
                catch (Exception error) { failure ??= error; }
                finally
                {
                    source.Listeners.Remove(listener);
                    source.Switch.Level = previousLevel;
                    try { if (!Dispatcher.CurrentDispatcher.HasShutdownFinished) Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                    catch (Exception error) { failure ??= error; }
                }
            }
            // Completion means cleanup has also finished, not only the assertions.
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "WPF 测试线程没有退出。");
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
