using System.Diagnostics;
using System.IO;
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
            AdminUiFixture? fixture = null;
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
                fixture = new AdminUiFixture(application);
                window = fixture.Window;
                var viewModel = fixture.ViewModel;
                var tabsAtStart = Assert.Single(Descendants<TabControl>(window));
                Assert.Equal(MainViewModel.HomePage, tabsAtStart.SelectedIndex);
                Assert.False(((TabItem)tabsAtStart.Items[MainViewModel.SettingsPage]).IsEnabled);
                Assert.Equal(Visibility.Collapsed, tabsAtStart.Visibility);
                var connect = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Connection.ConnectCommand));
                Assert.Equal(new Thickness(14, 0, 14, 0), connect.Padding);
                fixture.Login();
                fixture.OpenExam();
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, tabsAtStart.Visibility);
                Assert.Null(((ContentControl)window.FindName("PageRegion")).Content);
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
                Assert.Equal("多项选择题", group.TypeLabel);
                var oneCorrect = Assert.Single(Descendants<TextBox>(window), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "Correct");
                var allCorrect = Assert.Single(Descendants<TextBox>(window), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "AllCorrect");
                Assert.False(oneCorrect.IsVisible);
                Assert.True(allCorrect.IsVisible);
                allCorrect.Text = "3";
                allCorrect.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal("3", group.AllCorrect);
                var countInput = Assert.Single(Descendants<TextBox>(window), box =>
                    BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path is "QuestionCount" or "QuestionCountText");
                var originalCount = group.QuestionCount;
                countInput.Text = "invalid-count";
                countInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal(0, group.QuestionCount);
                Assert.Contains(viewModel.Exam.Validate(), error => error.Contains("题量", StringComparison.Ordinal));
                countInput.Text = originalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                countInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.Equal(originalCount, group.QuestionCount);
                typeSelector.SelectedValue = "single";
                DrainDispatcher();
                Assert.True(oneCorrect.IsVisible); Assert.False(allCorrect.IsVisible);
                Assert.Equal("3", group.AllCorrect);
                typeSelector.SelectedValue = "indeterminate";
                DrainDispatcher();
                Assert.True(oneCorrect.IsVisible); Assert.True(allCorrect.IsVisible);
                Assert.Equal("3", group.AllCorrect);
                // 考生说明预览按字形分段渲染：正文黑体、数字 Times New Roman，并显示预览来源说明。
                Assert.Single(Descendants<ItemsControl>(window), control =>
                    BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path.Path == "Exam.InstructionPreview");
                Assert.Single(Descendants<TextBlock>(window), block =>
                    BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path.Path == "Exam.InstructionSourceText");
                var rendered = Descendants<TextBlock>(window)
                    .Where(block => block.DataContext is InstructionLineViewModel).ToArray();
                var runs = rendered.SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Run>()).ToArray();
                Assert.Contains(runs, run => Equals(run.FontFamily.Source, InstructionText.NumberFont));
                Assert.Contains(runs, run => Equals(run.FontFamily.Source, InstructionText.BodyFont));
                Assert.All(rendered, block => Assert.Equal(((InstructionLineViewModel)block.DataContext).Text,
                    string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text))));

                var tabs = Assert.Single(Descendants<TabControl>(window));
                Assert.Equal("试卷导入与发布", ((TabItem)tabs.Items[MainViewModel.ImportPage]).Header);
                Assert.Equal("考生名单", ((TabItem)tabs.Items[MainViewModel.RosterPage]).Header);
                Assert.Equal("激活与试场分组", ((TabItem)tabs.Items[MainViewModel.ActivationPage]).Header);
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    tabs.SelectedItem = tab;
                    DrainDispatcher();
                    window.UpdateLayout();
                    if ((string)tab.Header == "激活与试场分组")
                    {
                        Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "重命名") && button.Command is not null);
                        var queryInput = Assert.Single(Descendants<TextBox>(window), input =>
                            BindingOperations.GetBinding(input, TextBox.TextProperty)?.Path.Path == "Activation.QueryDeviceId");
                        var roomFilter = Assert.Single(Descendants<ComboBox>(window), input =>
                            BindingOperations.GetBinding(input, Selector.SelectedValueProperty)?.Path.Path == "Activation.FilterRoomGroupId");
                        var queryButton = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Activation.QueryDeviceCommand));
                        var reloadButton = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Activation.RefreshDirectoryCommand));
                        var top = queryInput.TranslatePoint(new Point(0, 0), window).Y;
                        Assert.All(new FrameworkElement[] { roomFilter, queryButton, reloadButton }, control =>
                        {
                            Assert.InRange(Math.Abs(control.ActualHeight - queryInput.ActualHeight), 0, 0.5);
                            Assert.InRange(Math.Abs(control.TranslatePoint(new Point(0, 0), window).Y - top), 0, 0.5);
                        });
                    }
                    if ((string)tab.Header == "试卷导入与发布")
                    {
                        Assert.Equal(viewModel.CurrentExam!.Name, viewModel.Import.ExamName);
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "选择目录") && ReferenceEquals(button.Command, viewModel.Import.SelectDirectoryCommand));
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "选择 ZIP") && ReferenceEquals(button.Command, viewModel.Import.SelectZipCommand));
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "确认并正式发布") && ReferenceEquals(button.Command, viewModel.Import.PublishCommand));
                        Assert.False(viewModel.Import.CanPublish);
                    }
                    if ((string)tab.Header == "设备监控")
                    {
                        Assert.True(viewModel.Monitoring.IsPageActive);
                        AdminUiFixture.Until(() => viewModel.Monitoring.Rows.Count != 0);
                        viewModel.Monitoring.SelectedRow = viewModel.Monitoring.Rows.First(row => row.AvailableCommands.Any(command => command.CanResend));
                        viewModel.Monitoring.DecisionReason = "合成界面保留输入检查";
                        DrainDispatcher();
                        var participant = viewModel.Monitoring.SelectedRow.ParticipationId;
                        var commandId = viewModel.Monitoring.EventId;
                        viewModel.Monitoring.LoadCommand.Execute(null);
                        AdminUiFixture.Until(() => viewModel.Monitoring.Status.Contains("实际参试"));
                        DrainDispatcher();
                        Assert.Equal(participant, viewModel.Monitoring.SelectedRow?.ParticipationId);
                        Assert.Equal(commandId, viewModel.Monitoring.EventId);
                        Assert.Equal("合成界面保留输入检查", viewModel.Monitoring.DecisionReason);
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
                    if ((string)tab.Header is "设备监控" or "成绩统计")
                    {
                        var selectedIndex = tabs.SelectedIndex;
                        window.OpenManagement();
                        DrainDispatcher();
                        Assert.False(viewModel.Monitoring.IsPageActive);
                        Assert.False(viewModel.Reports.IsPageActive);
                        window.ReturnToBusiness();
                        DrainDispatcher();
                        Assert.Equal(selectedIndex, tabs.SelectedIndex);
                        Assert.Same(tab, tabs.SelectedItem);
                        Assert.Equal((string)tab.Header == "设备监控", viewModel.Monitoring.IsPageActive);
                        Assert.Equal((string)tab.Header == "成绩统计", viewModel.Reports.IsPageActive);
                    }
                }
                listener.Flush();
                Assert.Equal(string.Empty, output.ToString());

            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try { fixture?.Dispose(); window?.Close(); application?.Shutdown(); }
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
