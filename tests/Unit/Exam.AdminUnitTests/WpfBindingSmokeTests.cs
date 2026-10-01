using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Admin;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

[Collection("Admin WPF")]
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
                // 启动时停在“全部考试”主页，考试相关页面不可用；进入一场合成考试后再检查各页绑定。
                var tabsAtStart = Assert.Single(Descendants<TabControl>(window));
                Assert.Equal(MainViewModel.HomePage, tabsAtStart.SelectedIndex);
                Assert.False(((TabItem)tabsAtStart.Items[MainViewModel.SettingsPage]).IsEnabled);
                var connect = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Connection.ConnectCommand));
                Assert.Equal(new Thickness(14, 0, 14, 0), connect.Padding);
                // 仅在合成数据的视觉测试中展示已登录区域；认证本身另由服务端测试覆盖。
                typeof(MainWindow).GetMethod("ShowMainArea", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, null);
                DrainDispatcher();
                var start = DateTimeOffset.UtcNow.AddDays(1);
                viewModel.OpenExam(new Exam.Contracts.Exams.ExamSummary("synthetic-exam", "合成考试", start, start.AddHours(2),
                    Exam.Contracts.Common.ExamLifecycle.Scheduled, false, false, 0, 0, 1));
                DrainDispatcher();
                window.UpdateLayout();
                Assert.Equal(MainViewModel.SettingsPage, tabsAtStart.SelectedIndex);
                Assert.All(tabsAtStart.Items.Cast<TabItem>(), tab => Assert.True(tab.IsEnabled));
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
                Assert.Equal(new[] { "single", "multiple", "indeterminate" }, typeSelector.Items.Cast<QuestionTypeOption>().Select(item => item.Value));
                Assert.Equal(new[] { "单项选择题", "多项选择题", "不定项选择题" }, typeSelector.Items.Cast<QuestionTypeOption>().Select(item => item.Label));
                typeSelector.SelectedValue = "multiple";
                DrainDispatcher();
                Assert.Equal("multiple", group.Type);
                var instructions = Assert.Single(Descendants<TextBox>(window), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "Exam.InstructionsText");
                Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(instructions, TextBox.TextProperty)!.Mode);
                var previewControl = Assert.Single(Descendants<ContentControl>(window), control =>
                    BindingOperations.GetBinding(control, ContentControl.ContentProperty)?.Path.Path ==
                    "ManifestPreflight.StructurePreview");
                Assert.Null(previewControl.Content);
                var sampleRoot = new DirectoryInfo(AppContext.BaseDirectory);
                while (sampleRoot is not null && !Directory.Exists(Path.Combine(sampleRoot.FullName, "samples", "synthetic")))
                    sampleRoot = sampleRoot.Parent;
                Assert.NotNull(sampleRoot);
                var sample = File.ReadAllText(Path.Combine(sampleRoot.FullName, "samples", "synthetic", "minimal", "manifest.json"))
                    .Replace("synthetic-minimal", "synthetic-exam", StringComparison.Ordinal);
                viewModel.ManifestPreflight.ValidateManifestJson(sample);
                DrainDispatcher();
                window.UpdateLayout();
                var structure = Assert.IsType<ManifestStructurePreview>(previewControl.Content);
                Assert.Contains(Descendants<DataGrid>(window), grid =>
                    ReferenceEquals(grid.ItemsSource, structure.Questions) && grid.IsReadOnly);
                viewModel.ManifestPreflight.ValidateManifestJson("{");
                DrainDispatcher();
                Assert.Null(previewControl.Content);

                var tabs = Assert.Single(Descendants<TabControl>(window));
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    tabs.SelectedItem = tab;
                    DrainDispatcher();
                    window.UpdateLayout();
                    if ((string)tab.Header == "激活与试场分组")
                        Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "重命名") && button.Command is not null);
                    if ((string)tab.Header == "设备监控")
                    {
                        Assert.True(viewModel.Monitoring.IsPageActive);
                        Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "查询收齐记录") && button.Command is not null);
                        var screenshotPath = Environment.GetEnvironmentVariable("EXAM_C04_SCREENSHOT_PATH");
                        if (!string.IsNullOrWhiteSpace(screenshotPath))
                        {
                            viewModel.Monitoring.ExamId = "synthetic-exam";
                            viewModel.Monitoring.CollectionEvents.Add(new Exam.Contracts.Submissions.CollectionCompletedNotification(
                                "synthetic-event-1", "synthetic-exam", 1, 3, DateTimeOffset.UtcNow));
                            window.Width = 1200;
                            window.Height = 900;
                            window.UpdateLayout();
                            var bitmap = new RenderTargetBitmap(
                                (int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
                                96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(screenshotPath);
                            encoder.Save(file);
                        }
                    }
                    else Assert.False(viewModel.Monitoring.IsPageActive);
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
