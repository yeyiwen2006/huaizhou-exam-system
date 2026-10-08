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
                VerifyLoginInputAlignment(window);
                fixture.Login();
                DrainDispatcher(); window.UpdateLayout();
                Assert.Contains(Descendants<Exam.Desktop.Shared.Controls.StatusPill>(window), pill => pill.Text == "试卷未发布");
                Assert.Contains(Descendants<Exam.Desktop.Shared.Controls.StatusPill>(window), pill => pill.Text == "试卷已发布");
                var firstCard = viewModel.Home.Exams[0];
                var showExam = Assert.Single(Descendants<CheckBox>(window), box => ReferenceEquals(box.DataContext, firstCard) && Equals(box.Content, "在学生端显示"));
                showExam.IsChecked = false;
                AdminUiFixture.Until(() => !firstCard.IsVisibilitySaving);
                Assert.False(firstCard.Exam.VisibleToStudents);
                Assert.Equal(MainViewModel.HomePage, tabsAtStart.SelectedIndex);
                showExam.IsChecked = true;
                AdminUiFixture.Until(() => !firstCard.IsVisibilitySaving);
                Assert.True(firstCard.Exam.VisibleToStudents);
                SaveEditorScreenshot(window, (FrameworkElement)((TabItem)tabsAtStart.Items[MainViewModel.HomePage]).Content, "exam-publication-cards");
                fixture.OpenExam();
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, tabsAtStart.Visibility);
                Assert.Null(((ContentControl)window.FindName("PageRegion")).Content);
                Assert.Equal(MainViewModel.SettingsPage, tabsAtStart.SelectedIndex);
                Assert.All(tabsAtStart.Items.Cast<TabItem>(), tab => Assert.True(tab.IsEnabled));
                var subjectGrid = Assert.Single(Descendants<DataGrid>(window), grid => ReferenceEquals(grid.ItemsSource, viewModel.Exam.Subjects));
                Assert.Same(application.FindResource("Brush.Surface"), subjectGrid.AlternatingRowBackground);
                VerifyExamSettingsEditing(window, viewModel, subjectGrid);
                var horizontalBar = new ScrollBar { Style = (Style)application.FindResource(typeof(ScrollBar)), Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, ViewportSize = 10 };
                horizontalBar.Measure(new Size(200, SystemParameters.HorizontalScrollBarHeight));
                horizontalBar.Arrange(new Rect(0, 0, 200, SystemParameters.HorizontalScrollBarHeight));
                var track = Assert.IsType<Track>(horizontalBar.Template.FindName("PART_Track", horizontalBar));
                Assert.IsType<Thumb>(track.Thumb);
                Assert.False(track.IsDirectionReversed);
                Assert.True(double.IsNaN(horizontalBar.Width));
                Assert.Equal(SystemParameters.HorizontalScrollBarHeight, horizontalBar.Height);
                Assert.Same(ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
                Assert.Same(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
                var group = viewModel.Exam.SelectedSubject!.Groups[0];
                var groupGrid = Assert.Single(Descendants<DataGrid>(window), grid => ReferenceEquals(grid.ItemsSource, viewModel.Exam.SelectedSubject.Groups));
                EditGroupType(groupGrid, group, "multiple");
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
                var countColumn = groupGrid.Columns.Single(column => Equals(column.Header, "题量"));
                var originalCount = group.QuestionCount;
                EditCell(groupGrid, group, countColumn, "invalid-count");
                Assert.Equal(0, group.QuestionCount);
                Assert.Contains(viewModel.Exam.Validate(), error => error.Contains("题量", StringComparison.Ordinal));
                EditCell(groupGrid, group, countColumn, originalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(originalCount, group.QuestionCount);
                EditGroupType(groupGrid, group, "single");
                Assert.True(oneCorrect.IsVisible); Assert.False(allCorrect.IsVisible);
                Assert.Equal("3", group.AllCorrect);
                EditGroupType(groupGrid, group, "indeterminate");
                Assert.True(oneCorrect.IsVisible); Assert.True(allCorrect.IsVisible);
                Assert.Equal("3", group.AllCorrect);
                VerifyScoreTokenEditing(window, viewModel, group);
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
                Assert.Equal("设备与试场分组", ((TabItem)tabs.Items[MainViewModel.ActivationPage]).Header);
                foreach (var tab in tabs.Items.Cast<TabItem>())
                {
                    tabs.SelectedItem = tab;
                    DrainDispatcher();
                    window.UpdateLayout();
                    if ((string)tab.Header == "设备与试场分组")
                    {
                        Assert.DoesNotContain(Descendants<Button>(window), button =>
                            ReferenceEquals(button.Command, viewModel.Activation.ApproveCommand) ||
                            ReferenceEquals(button.Command, viewModel.Activation.RefreshPendingCommand));
                        VerifyRoomInputAlignment(window, viewModel);
                        Assert.Contains(Descendants<Button>(window), button => Equals(button.Content, "重命名") && button.Command is not null);
                        var queryInput = Assert.Single(Descendants<TextBox>(window), input =>
                            BindingOperations.GetBinding(input, TextBox.TextProperty)?.Path.Path == "Activation.QueryDeviceId");
                        var roomFilter = Assert.Single(Descendants<ComboBox>(window), input =>
                            BindingOperations.GetBinding(input, Selector.SelectedItemProperty)?.Path.Path == "Activation.SelectedFilterChoice");
                        var queryButton = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Activation.QueryDeviceCommand));
                        var reloadButton = Assert.Single(Descendants<Button>(window), button => ReferenceEquals(button.Command, viewModel.Activation.RefreshDirectoryCommand));
                        AdminUiFixture.Until(() => viewModel.Activation.CanEditDirectory);
                        foreach (var name in new[] { "全部试场", "未分组", "全部试场", "全部试场" })
                        {
                            roomFilter.SelectedItem = viewModel.Activation.FilterChoices.Single(choice => choice.Name == name);
                            queryButton.Command.Execute(null);
                            AdminUiFixture.Until(() => viewModel.Activation.QueryDeviceCommand.CanExecute(null));
                            Assert.Equal(name, Assert.IsType<RoomGroupChoice>(roomFilter.SelectedItem).Name);
                        }
                        Assert.DoesNotContain(Descendants<TextBox>(window), input =>
                            BindingOperations.GetBinding(input, TextBox.TextProperty)?.Path.Path == "Activation.MoveReason");
                        Assert.DoesNotContain(Descendants<ComboBox>(window), input =>
                            BindingOperations.GetBinding(input, Selector.SelectedItemProperty)?.Path.Path == "Activation.SelectedApprovalChoice");
                        foreach (var binding in new[] { "Activation.SelectedMoveChoice" })
                        {
                            var choice = Assert.Single(Descendants<ComboBox>(window), input =>
                                BindingOperations.GetBinding(input, Selector.SelectedItemProperty)?.Path.Path == binding);
                            Assert.Equal("未分组", Assert.IsType<RoomGroupChoice>(choice.SelectedItem).Name);
                        }
                        var devicesGrid = Assert.Single(Descendants<DataGrid>(window), grid => ReferenceEquals(grid.ItemsSource, viewModel.Activation.Devices));
                        var selectedDevice = viewModel.Activation.Devices[0];
                        devicesGrid.SelectedItem = selectedDevice;
                        selectedDevice.IsSelected = true;
                        var historyRow = new DeviceRoomHistoryRow("2026-10-08 12:00:00", "未分组", "合成试场A", "synthetic-operator", "合成转组");
                        viewModel.Activation.DeviceHistory.Add(historyRow);
                        DrainDispatcher(); window.UpdateLayout();
                        var deviceContainer = Assert.IsType<DataGridRow>(devicesGrid.ItemContainerGenerator.ContainerFromItem(selectedDevice));
                        for (var refresh = 0; refresh < 3; refresh++)
                        {
                            queryButton.Command.Execute(null);
                            AdminUiFixture.Until(() => viewModel.Activation.QueryDeviceCommand.CanExecute(null));
                            DrainDispatcher(); window.UpdateLayout();
                            Assert.Same(selectedDevice, devicesGrid.SelectedItem);
                            Assert.Same(deviceContainer, devicesGrid.ItemContainerGenerator.ContainerFromItem(selectedDevice));
                            Assert.Same(historyRow, Assert.Single(viewModel.Activation.DeviceHistory));
                            Assert.True(selectedDevice.IsSelected);
                        }
                        SaveEditorScreenshot(window, devicesGrid, "stable-device-directory");
                        SaveEditorScreenshot(window, roomFilter, "activation-room-filter");
                        var top = queryInput.TranslatePoint(new Point(0, 0), window).Y;
                        Assert.All(new FrameworkElement[] { roomFilter, queryButton, reloadButton }, control =>
                        {
                            Assert.InRange(Math.Abs(control.ActualHeight - queryInput.ActualHeight), 0, 0.5);
                            Assert.InRange(Math.Abs(control.TranslatePoint(new Point(0, 0), window).Y - top), 0, 0.5);
                        });
                    }
                    if ((string)tab.Header == "试卷导入与发布")
                    {
                        AdminUiFixture.Until(() => viewModel.Import.PublicationText != "正在读取发布状态…");
                        Assert.Equal("试卷未发布", viewModel.Import.PublicationText);
                        var publicationPill = Assert.Single(Descendants<Exam.Desktop.Shared.Controls.StatusPill>(window), pill => pill.Text == "试卷未发布");
                        SaveEditorScreenshot(window, publicationPill, "import-publication-state");
                        Assert.Equal(viewModel.CurrentExam!.Name, viewModel.Import.ExamName);
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "选择目录") && ReferenceEquals(button.Command, viewModel.Import.SelectDirectoryCommand));
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "选择 ZIP") && ReferenceEquals(button.Command, viewModel.Import.SelectZipCommand));
                        Assert.Contains(Descendants<Button>(window), button =>
                            Equals(button.Content, "确认并正式发布") && ReferenceEquals(button.Command, viewModel.Import.PublishCommand));
                        Assert.False(viewModel.Import.CanPublish);
                        VerifyImportPreviewColumns(window, viewModel);
                    }
                    if ((string)tab.Header == "设备监控")
                    {
                        Assert.True(viewModel.Monitoring.IsPageActive);
                        AdminUiFixture.Until(() => viewModel.Monitoring.Rows.Count != 0);
                        var mainGrid = Assert.Single(Descendants<DataGrid>(window), grid =>
                            BindingOperations.GetBinding(grid, DataGrid.SelectedItemProperty)?.Path.Path == "Monitoring.SelectedTableRow");
                        var missingGrid = Assert.Single(Descendants<DataGrid>(window), grid =>
                            BindingOperations.GetBinding(grid, DataGrid.SelectedItemProperty)?.Path.Path == "Monitoring.SelectedUncollectedRow");
                        var collected = viewModel.Monitoring.Rows.First(row => !viewModel.Monitoring.UncollectedRows.Contains(row));
                        mainGrid.SelectedItem = collected;
                        DrainDispatcher();
                        Assert.Same(collected, viewModel.Monitoring.SelectedRow);
                        Assert.Null(missingGrid.SelectedItem);
                        var missing = viewModel.Monitoring.UncollectedRows.First(row => row.AvailableCommands.Any(command => command.CanResend));
                        missingGrid.SelectedItem = missing;
                        DrainDispatcher();
                        Assert.Same(missing, viewModel.Monitoring.SelectedRow);
                        Assert.Same(missing, mainGrid.SelectedItem);
                        Assert.Contains(viewModel.Monitoring.SelectedCommands, command => command.CanResend);
                        foreach (var header in new[] { "准考证号", "学生端编号", "当前页面", "最后通信" })
                        {
                            var column = Assert.Single(mainGrid.Columns, column => Equals(column.Header, header));
                            Assert.True(column.Width.IsSizeToCells);
                            Assert.True(column.MinWidth >= (header == "最后通信" ? 210 : 140));
                        }
                        viewModel.Monitoring.DecisionReason = "合成界面保留输入检查";
                        DrainDispatcher();
                        var participant = Assert.IsType<MonitoringRow>(viewModel.Monitoring.SelectedRow).ParticipationId;
                        var commandId = viewModel.Monitoring.EventId;
                        viewModel.Monitoring.LoadCommand.Execute(null);
                        AdminUiFixture.Until(() => viewModel.Monitoring.Status.Contains("实际参加考试"));
                        DrainDispatcher();
                        Assert.Equal(participant, viewModel.Monitoring.SelectedRow?.ParticipationId);
                        Assert.Same(viewModel.Monitoring.SelectedRow, mainGrid.SelectedItem);
                        Assert.Same(viewModel.Monitoring.SelectedRow, missingGrid.SelectedItem);
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

    private static void VerifyScoreTokenEditing(MainWindow window, MainViewModel viewModel, GroupDraftViewModel group)
    {
        var fields = (WrapPanel)window.FindName("MultipleCorrectScoreFields");
        var oneFields = (WrapPanel)window.FindName("OneCorrectScoreFields");
        var toolbar = Assert.Single(Descendants<WrapPanel>(window), panel => ReferenceEquals(panel.Tag, fields));
        var oneToolbar = Assert.Single(Descendants<WrapPanel>(window), panel => ReferenceEquals(panel.Tag, oneFields));
        var partial = Assert.Single(Descendants<TextBox>(fields), box =>
            BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "PartialCorrect");
        var correct = Assert.Single(Descendants<TextBox>(oneFields), box =>
            BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "Correct");
        var originalPartial = group.PartialCorrect;
        var originalCorrect = group.Correct;
        Button Token(Panel panel, string token) => Assert.Single(Descendants<Button>(panel), button => Equals(button.Tag, token));
        void Click(Panel panel, string token)
        {
            var button = Token(panel, token);
            Assert.True(button.IsEnabled);
            button.Focus(); // Keyboard activation must preserve the previous text selection too.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            DrainDispatcher();
        }
        Assert.Equal(new[] { "{正确选项总数}", "{选择的正确选项数}", "+", "-", "*", "/", "()" },
            Descendants<Button>(toolbar).Select(button => button.Tag));
        partial.BringIntoView(); DrainDispatcher();
        Assert.True(partial.Focus());
        partial.SelectAll();
        Click(toolbar, "()");
        Assert.Equal("(" + originalPartial + ")", partial.Text);
        Click(toolbar, "{选择的正确选项数}");
        foreach (var token in new[] { "+", "{正确选项总数}" }) Click(toolbar, token);
        Assert.Equal("({选择的正确选项数}+{正确选项总数})", partial.Text);
        partial.Select(partial.Text.Length, 0);
        Click(toolbar, "*");
        partial.SelectedText = "0.5";
        partial.Select(partial.Text.Length, 0);
        Click(toolbar, "-");
        partial.SelectedText = "1";
        partial.Select(partial.Text.Length, 0);
        Click(toolbar, "/");
        partial.SelectedText = "2";
        DrainDispatcher();
        Assert.Equal("({选择的正确选项数}+{正确选项总数})*0.5-1/2", group.PartialCorrect);
        Assert.True(Exam.Domain.Scoring.ScoreExpressionParser.Check(group.PartialCorrect).IsValid);
        Assert.Equal(originalCorrect, group.Correct);

        correct.BringIntoView(); DrainDispatcher();
        Assert.True(correct.Focus()); correct.SelectAll();
        Click(oneToolbar, "{正确选项总数}");
        Assert.Equal("{正确选项总数}", group.Correct);
        Assert.Equal("({选择的正确选项数}+{正确选项总数})*0.5-1/2", group.PartialCorrect);
        SaveEditorScreenshot(window, correct, "scoring-rules");

        // A new group starts with no selected score field; its toolbar cannot alter the old group.
        viewModel.Exam.AddGroupCommand.Execute(null);
        DrainDispatcher();
        Assert.False(oneToolbar.IsEnabled);
        viewModel.Exam.RemoveGroupCommand.Execute(null);
        viewModel.Exam.SelectedSubject!.SelectedGroup = group;
        group.Correct = originalCorrect; group.PartialCorrect = originalPartial;
        DrainDispatcher();
    }

    private static void VerifyImportPreviewColumns(MainWindow window, MainViewModel viewModel)
    {
        const string pages = "A:1页、B:1页、C:1页、D:1页";
        for (var index = 1; index <= 7; index++)
            viewModel.Import.Questions.Add(new($"preview-{index}", index, "数学", "一、不定项选择题", "不定项选择", "4",
                "A、B、C、D", "synthetic.pdf", "A、B、D", 1, pages));
        DrainDispatcher(); window.UpdateLayout();
        var grid = Assert.Single(Descendants<DataGrid>(window), item => ReferenceEquals(item.ItemsSource, viewModel.Import.Questions));
        grid.BringIntoView(); DrainDispatcher();
        var first = viewModel.Import.Questions[0];
        var subject = grid.Columns.Single(column => Equals(column.Header, "科目"));
        var type = grid.Columns.Single(column => Equals(column.Header, "题型"));
        var pageColumn = grid.Columns.Single(column => Equals(column.Header, "选项绑定页"));
        Assert.True(subject.ActualWidth < pageColumn.ActualWidth);
        var typeText = Assert.IsType<TextBlock>(type.GetCellContent(first));
        Assert.Equal("不定项选择", typeText.Text);
        Assert.True(typeText.ActualWidth >= 6 * typeText.FontSize);
        var pageText = Assert.IsType<TextBlock>(pageColumn.GetCellContent(first));
        Assert.Equal(pages, pageText.Text);
        Assert.Equal(TextWrapping.Wrap, pageText.TextWrapping);
        Assert.True(double.IsNaN(grid.RowHeight));
        SaveEditorScreenshot(window, grid, "import-preview-columns");

        var originalWidth = window.Width;
        window.Width = window.MinWidth; DrainDispatcher(); window.UpdateLayout();
        var scroll = Assert.Single(Descendants<ScrollViewer>(grid), viewer => viewer.Name == "DG_ScrollViewer");
        Assert.True(scroll.ScrollableWidth > 0);
        scroll.ScrollToRightEnd(); DrainDispatcher();
        Assert.InRange(scroll.ScrollableWidth - scroll.HorizontalOffset, 0, 1);
        SaveEditorScreenshot(window, grid, "import-preview-columns-narrow");
        window.Width = originalWidth;
        viewModel.Import.Questions.Clear();
        DrainDispatcher(); window.UpdateLayout();
    }

    private static void VerifyExamSettingsEditing(MainWindow window, MainViewModel viewModel, DataGrid subjectGrid)
    {
        var originalSubject = viewModel.Exam.SelectedSubject!;
        var originalName = originalSubject.Name;
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var originalSubjects = viewModel.Exam.Subjects.ToArray();
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        while (viewModel.Exam.Subjects.Count < 3) viewModel.Exam.AddSubjectCommand.Execute(null);
        DrainDispatcher(); window.UpdateLayout();
        subjectGrid.BringIntoView(); DrainDispatcher();
        var subjectScroll = Assert.Single(Descendants<ScrollViewer>(subjectGrid), viewer => viewer.Name == "DG_ScrollViewer");
        Assert.Equal(0, subjectScroll.ScrollableHeight);
        Assert.False(subjectGrid.CanUserSortColumns);
        var subjects = viewModel.Exam.Subjects.ToArray();
        var names = new[] { "数学", "物理", "化学" };
        var nameColumn = subjectGrid.Columns.Single(column => Equals(column.Header, "科目名称"));
        for (var index = 0; index < subjects.Length; index++)
        {
            EditCell(subjectGrid, subjects[index], nameColumn, names[index]);
            Assert.Equal(subjects, subjectGrid.Items.Cast<SubjectDraftViewModel>());
            Assert.Equal(0, subjectScroll.VerticalOffset);
        }
        Assert.Equal(names, subjectGrid.Items.Cast<SubjectDraftViewModel>().Select(subject => subject.Name));
        Assert.Empty(subjectGrid.Items.SortDescriptions);
        foreach (var subject in subjects.Take(2))
        {
            var row = Assert.IsType<DataGridRow>(subjectGrid.ItemContainerGenerator.ContainerFromItem(subject));
            Assert.False(row.IsSelected);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(row.Background).Color);
        }
        SaveEditorScreenshot(window, subjectGrid, "exam-subjects-three");

        var groups = viewModel.Exam.SelectedSubject!.Groups;
        viewModel.Exam.AddGroupCommand.Execute(null);
        DrainDispatcher(); window.UpdateLayout();
        var groupGrid = Assert.Single(Descendants<DataGrid>(window), grid => ReferenceEquals(grid.ItemsSource, groups));
        Assert.False(groupGrid.CanUserSortColumns);
        Assert.Same(subjectGrid.AlternatingRowBackground, groupGrid.AlternatingRowBackground);
        var groupNameColumn = groupGrid.Columns.Single(column => Equals(column.Header, "题组名"));
        EditCell(groupGrid, groups[0], groupNameColumn, "一、化学选择题");
        Assert.Equal("一、化学选择题", groups[0].Name);
        Assert.Equal(groups, groupGrid.Items.Cast<GroupDraftViewModel>());
        Assert.All(groupGrid.Columns, column => Assert.False(column.IsReadOnly));
        var originalOrder = groups[0].OrderText;
        var originalCount = groups[0].QuestionCountText;
        var originalScore = groups[0].MaxScore;
        EditCell(groupGrid, groups[0], groupGrid.Columns.Single(column => Equals(column.Header, "顺序")), "3");
        Assert.Equal(3, groups[0].Order);
        EditCell(groupGrid, groups[0], groupGrid.Columns.Single(column => Equals(column.Header, "题量")), "12");
        Assert.Equal(12, groups[0].QuestionCount);
        EditCell(groupGrid, groups[0], groupGrid.Columns.Single(column => Equals(column.Header, "满分")), "24");
        Assert.Equal("24", groups[0].MaxScore);
        Assert.Equal(groups, groupGrid.Items.Cast<GroupDraftViewModel>());
        SaveEditorScreenshot(window, groupGrid, "exam-group-fields-inline");
        groups[0].OrderText = originalOrder;
        groups[0].QuestionCountText = originalCount;
        groups[0].MaxScore = originalScore;

        while (viewModel.Exam.Subjects.Count < 8) viewModel.Exam.AddSubjectCommand.Execute(null);
        DrainDispatcher(); window.UpdateLayout();
        Assert.True(subjectScroll.ScrollableHeight > 0);
        subjectScroll.ScrollToEnd(); DrainDispatcher();
        Assert.True(subjectScroll.VerticalOffset > 0);
        subjectScroll.ScrollToTop(); DrainDispatcher();
        Assert.Equal(0, subjectScroll.VerticalOffset);
        foreach (var subject in viewModel.Exam.Subjects.Except(originalSubjects).ToArray()) viewModel.Exam.Subjects.Remove(subject);
        originalSubject.Name = originalName;
        viewModel.Exam.SelectedSubject = originalSubject;
        window.Width = originalWidth;
        window.Height = originalHeight;
        DrainDispatcher(); window.UpdateLayout();
    }

    private static void EditCell(DataGrid grid, object item, DataGridColumn column, string text)
    {
        grid.ScrollIntoView(item, column);
        grid.SelectedItem = item;
        grid.CurrentCell = new DataGridCellInfo(item, column);
        grid.Focus();
        Assert.True(grid.BeginEdit());
        DrainDispatcher();
        var editor = Assert.IsType<TextBox>(column.GetCellContent(item));
        var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(item));
        var originalTop = row.TranslatePoint(new Point(0, 0), grid).Y;
        editor.Text = text;
        editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Assert.True(grid.CommitEdit(DataGridEditingUnit.Row, true));
        DrainDispatcher();
        Assert.Equal(originalTop, row.TranslatePoint(new Point(0, 0), grid).Y);
    }

    private static void EditGroupType(DataGrid grid, GroupDraftViewModel group, string type)
    {
        var column = grid.Columns.Single(column => Equals(column.Header, "题型"));
        grid.ScrollIntoView(group, column);
        grid.SelectedItem = group;
        grid.CurrentCell = new DataGridCellInfo(group, column);
        grid.Focus();
        Assert.True(grid.BeginEdit());
        DrainDispatcher();
        var selector = Assert.Single(Descendants<ComboBox>(column.GetCellContent(group)));
        Assert.Equal(new[] { "single", "multiple", "indeterminate" }, selector.Items.Cast<QuestionTypeOption>().Select(item => item.Value));
        Assert.Equal(new[] { "单项选择题", "多项选择题", "不定项选择题" }, selector.Items.Cast<QuestionTypeOption>().Select(item => item.Label));
        selector.SelectedValue = type;
        DrainDispatcher();
        Assert.Equal(type, group.Type);
        Assert.True(grid.CommitEdit(DataGridEditingUnit.Row, true));
        DrainDispatcher();
    }

    private static void VerifyRoomInputAlignment(MainWindow window, MainViewModel viewModel)
    {
        foreach (var (binding, command) in new[]
        {
            ("Activation.NewGroupName", viewModel.Activation.AddGroupCommand),
            ("Activation.SelectedGroupName", viewModel.Activation.RenameGroupCommand)
        })
        {
            var input = Assert.Single(Descendants<TextBox>(window), box =>
                BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == binding);
            var button = Assert.Single(Descendants<Button>(window), item => ReferenceEquals(item.Command, command));
            input.BringIntoView(); DrainDispatcher(); window.UpdateLayout();
            var position = input.TranslatePoint(new Point(0, 0), window);
            var buttonPosition = button.TranslatePoint(new Point(0, 0), window);
            Assert.InRange(Math.Abs(position.Y - buttonPosition.Y), 0, 0.5);
            Assert.InRange(Math.Abs(input.ActualHeight - button.ActualHeight), 0, 0.5);
            Assert.InRange(buttonPosition.X - position.X - input.ActualWidth, 11.5, 12.5);
            SaveEditorScreenshot(window, input, "room-groups");
        }
    }

    private static void VerifyLoginInputAlignment(Window window)
    {
        var server = Assert.Single(Descendants<TextBox>(window), box => box.Name == "ServerAddressBox");
        var port = Assert.Single(Descendants<TextBox>(window), box => box.Name == "ServerPortBox");
        Assert.DoesNotContain("://", server.Text);
        var connection = Assert.IsType<ConnectionViewModel>(server.DataContext);
        server.Text = "synthetic-server.invalid";
        port.Text = "5443";
        DrainDispatcher();
        Assert.Equal("synthetic-server.invalid", connection.ServerAddress);
        Assert.Equal("5443", connection.ServerPort);
        var username = Assert.Single(Descendants<TextBox>(window), box => box.Name == "UsernameBox");
        var password = Assert.Single(Descendants<PasswordBox>(window), box => box.Name == "PasswordInput");
        foreach (var value in new[] { string.Empty, "synthetic-input" })
        {
            username.Text = value;
            password.Password = value;
            DrainDispatcher(); window.UpdateLayout();
            var usernameHost = (ScrollViewer)username.Template.FindName("PART_ContentHost", username);
            var passwordHost = (ScrollViewer)password.Template.FindName("PART_ContentHost", password);
            var usernameView = Assert.IsAssignableFrom<Visual>(usernameHost.Content);
            var passwordView = Assert.IsAssignableFrom<Visual>(passwordHost.Content);
            var textStart = usernameView.TransformToAncestor(username).Transform(new Point()).X;
            var passwordStart = passwordView.TransformToAncestor(password).Transform(new Point()).X;
            Assert.InRange(Math.Abs(textStart - passwordStart), 0, 1);
            Assert.Equal(TextAlignment.Left, username.TextAlignment);
            Assert.Equal(HorizontalAlignment.Left, password.HorizontalContentAlignment);
        }
        SaveEditorScreenshot(window, username, "aligned-login-inputs");
        connection.IsRegistering = true;
        DrainDispatcher(); window.UpdateLayout();
        var name = Assert.Single(Descendants<TextBox>(window), box => box.Name == "DisplayNameBox");
        Assert.True(name.IsVisible);
        Assert.True(username.TransformToAncestor(window).Transform(new Point()).Y < name.TransformToAncestor(window).Transform(new Point()).Y);
        Assert.True(name.TransformToAncestor(window).Transform(new Point()).Y < password.TransformToAncestor(window).Transform(new Point()).Y);
        SaveEditorScreenshot(window, name, "registration-field-order");
        connection.IsRegistering = false;
        DrainDispatcher(); window.UpdateLayout();
        Assert.False(name.IsVisible);
    }

    private static void SaveEditorScreenshot(Window window, FrameworkElement input, string name)
    {
        var directory = Environment.GetEnvironmentVariable("EXAM_EDITOR_SCREENSHOT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        input.BringIntoView(); DrainDispatcher(); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
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
