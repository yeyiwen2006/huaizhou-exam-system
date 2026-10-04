using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Admin;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Grading;
using Exam.Contracts.Submissions;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.AdminUnitTests;

/// <summary>
/// 设计评审用截图：启动真实 App 与 MainWindow，用明显的合成数据填充各页后逐个标签页截图。
/// 仅在设置 EXAM_UI_SCREENSHOT_DIRECTORY 时生效；未设置时直接返回（视为通过）。
/// 所有姓名、编号、成绩均为合成示例，不含任何真实考生或试卷数据。
/// </summary>
[Collection("Admin WPF")]
public sealed class UiScreenshotTests
{
    private const string DirectoryVariable = "EXAM_UI_SCREENSHOT_DIRECTORY";
    private static readonly double[] Scales = [1.0, 1.25, 1.5];

    [Fact]
    [Trait("Category", "Wpf")]
    public async Task MainWindow_EveryTab_WritesScreenshotsWithSyntheticData()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);

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
                source.Switch.Level = SourceLevels.Warning;
                source.Listeners.Add(listener);
                try { application = new App(); }
                catch (InvalidOperationException error)
                {
                    // WPF 每个进程只能创建一次 Application；冒烟测试已创建过时须单独运行本截图测试。
                    throw new InvalidOperationException(
                        "截图测试需单独运行：dotnet test tests/Unit/Exam.AdminUnitTests --filter FullyQualifiedName~UiScreenshotTests", error);
                }
                application.InitializeComponent();
                application.StartupUri = new Uri("pack://application:,,,/Exam.Admin;component/MainWindow.xaml", UriKind.Absolute);
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DrainDispatcher();
                fixture = new AdminUiFixture(application);
                window = fixture.Window;
                fixture.Login();
                fixture.OpenExam();
                var viewModel = fixture.ViewModel;
                Populate(viewModel);
                DrainDispatcher();
                window.UpdateLayout();

                var tabs = Assert.Single(Descendants<TabControl>(window));
                var written = new List<string>();
                var pageHashes = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    DrainDispatcher();
                    // 报表页离开时会按设计清空会话；每次选中后重新放入同一份合成结果。
                    if (viewModel.Reports.IsPageActive && !viewModel.Reports.Session.HasTables)
                        PopulateReport(viewModel);
                    if (viewModel.Monitoring.IsPageActive)
                    {
                        viewModel.Monitoring.LoadCommand.Execute(null);
                        DrainDispatcher();
                        viewModel.Monitoring.SelectedRow = viewModel.Monitoring.Rows.FirstOrDefault();
                    }
                    Settle(window);
                    var tab = Assert.IsType<TabItem>(tabs.Items[index]);
                    Assert.Equal(Visibility.Visible, tabs.Visibility);
                    Assert.True(tab.IsEnabled);
                    Assert.True(tab.IsSelected);
                    Assert.True(Assert.IsAssignableFrom<FrameworkElement>(tab.Content).IsVisible,
                        $"标签页 {index} 没有实际展示，不能将登录页截图当成业务页。");
                    Assert.Null(((ContentControl)window.FindName("PageRegion")).Content);
                    var header = tab.Header as string;
                    var slug = Slug(header);
                    var name = slug.Length == 0
                        ? $"admin-{index}"
                        : $"admin-{index}-{slug}";
                    var pagePaths = SaveScreenshots(window, directory, name).ToArray();
                    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pagePaths[0])));
                    Assert.True(pageHashes.Add(hash), $"页面 {header} 与其他页面像素完全相同，可能没有真正切换页面。");
                    written.AddRange(pagePaths);
                    if (viewModel.Monitoring.IsPageActive)
                    {
                        var scroller = Assert.IsType<ScrollViewer>(tab.Content);
                        scroller.ScrollToBottom(); Settle(window);
                        SaveScreenshots(window, directory, name + "-details").ToArray();
                        scroller.ScrollToTop(); Settle(window);
                    }
                    // 同一页在窗口最小尺寸下再截一张，检查顶部连接条与表单不挤压。
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    Settle(window);
                    written.AddRange(SaveScreenshots(window, directory, name + "-min"));
                    window.Width = 1440;
                    window.Height = 900;
                    Settle(window);
                }

                listener.Flush();
                File.WriteAllText(Path.Combine(directory, "admin-binding-trace.txt"), output.ToString(), new UTF8Encoding(false));
                Assert.Equal(tabs.Items.Count * Scales.Length * 2, written.Count);
                Assert.Equal(tabs.Items.Count, pageHashes.Count);
                Assert.DoesNotContain("System.Windows.Data Error:", output.ToString(), StringComparison.Ordinal);
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
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "WPF 截图线程没有退出。");
    }

    private static void Populate(MainViewModel viewModel)
    {
        viewModel.Connection.ServerAddress = "https://synthetic-server.invalid:5001/";
        viewModel.Connection.Username = "synthetic-admin";

        // The fixture has already authenticated and loaded this exam through the real API client.
        Assert.True(viewModel.Connection.IsConnected);
        Assert.Equal(AdminUiFixture.ExamId, viewModel.Exam.ExamId);
        viewModel.SelectedPage = MainViewModel.HomePage;

        var exam = viewModel.Exam;
        exam.Name = "合成模拟考试";
        exam.ScheduledStartText = "2027-01-15 09:00";
        exam.ScheduledEndText = "2027-01-15 11:00";
        var subjectA = exam.Subjects[0];
        subjectA.SubjectId = "subject-a";
        subjectA.Name = "合成科目A";
        subjectA.MaxScore = "20";
        exam.AddSubjectCommand.Execute(null);
        var subjectB = exam.Subjects[^1];
        subjectB.SubjectId = "subject-b";
        subjectB.Name = "合成科目B";
        subjectB.MaxScore = "20";
        exam.AddGroupCommand.Execute(null);
        var multiple = subjectB.Groups[^1];
        multiple.Name = "2、多项选择题";
        multiple.Type = "multiple";
        multiple.AllCorrect = "2";
        multiple.PartialCorrect = "1";
        multiple.QuestionCount = 5;
        exam.SelectedSubject = subjectA;
        exam.RefreshInstructionsCommand.Execute(null);

        var roster = viewModel.Roster;
        roster.Entries.Add(new RosterEntryDraft("合成考生01", "01", "2027001"));
        roster.Entries.Add(new RosterEntryDraft("合成考生02", "01", "2027002"));
        roster.Entries.Add(new RosterEntryDraft("合成考生03", "02", "2027003"));
        // 名册变化会使预览失效，故先写名册再写预览行。
        roster.PreviewRows.Add(new RosterPreviewRow(2, "合成考生04", "02", "2027004", RosterPreviewState.New, "可导入"));
        roster.PreviewRows.Add(new RosterPreviewRow(3, "合成考生01", "01", "2027001", RosterPreviewState.Duplicate, "与已有记录完全相同"));
        roster.PreviewRows.Add(new RosterPreviewRow(4, "合成考生05", "03", "2027002", RosterPreviewState.Conflict, "准考证号与已有记录冲突"));
        roster.PreviewRows.Add(new RosterPreviewRow(5, "", "0x", "", RosterPreviewState.Error, "姓名不能为空；班级只能包含数字；准考证号不能为空"));
        roster.Name = "合成考生06";
        roster.ClassText = "03";
        roster.AdmissionNo = "2027006";

        // The activation page reads authoritative synthetic groups/devices when it becomes active.
        viewModel.Activation.QueryDeviceId = string.Empty;
        viewModel.Activation.MoveReason = "合成示例：更换试场";

        var monitoring = viewModel.Monitoring;
        monitoring.ExamId = "synthetic-exam";
        monitoring.Rows.Add(new MonitoringRow("synthetic-participation-01", "synthetic-candidate-01", "作答中", "synthetic-device-01", 1, 1, 3, "未提交", "—", "—"));
        monitoring.Rows.Add(new MonitoringRow("synthetic-participation-02", "synthetic-candidate-02", "已交卷", "synthetic-device-02", 1, 1, 5, "已提交", "—", "—"));
        monitoring.Rows.Add(new MonitoringRow("synthetic-participation-03", "synthetic-candidate-03", "候考", "synthetic-device-17", 1, 2, 2, "未提交", "—", "—"));
        monitoring.Rows.Add(new MonitoringRow("synthetic-participation-04", "synthetic-candidate-04", "未参加", "", 0, 0, 1, "未提交", "—", "—"));
        // FilteredRows 是计算属性，借过滤条件的属性通知让表格重新读取。
        monitoring.FilterText = "synthetic";
        monitoring.FilterText = string.Empty;
        monitoring.SelectedRow = monitoring.Rows[0];
        monitoring.DecisionReason = "合成示例：更换故障设备";
        monitoring.EventId = "synthetic-event-07";
        var completedAt = new DateTimeOffset(2027, 1, 15, 11, 2, 30, TimeSpan.FromHours(8));
        monitoring.CollectionEvents.Add(new CollectionCompletedNotification("synthetic-event-1", "synthetic-exam", 1, 3, completedAt));
        monitoring.CollectionEvents.Add(new CollectionCompletedNotification("synthetic-event-2", "synthetic-exam", 2, 4, completedAt.AddMinutes(6)));

        viewModel.Accounts.Accounts.Clear();
        // 考务账号页（管理员可见）：合成账号，覆盖已启用、等待批准和已停用三种状态。
        var created = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(8));
        foreach (var account in new[]
                 {
                     new Exam.Contracts.Operators.OperatorAccount("synthetic-admin", "合成管理员", Exam.Contracts.Operators.OperatorStatus.Active, true, created, null),
                     new Exam.Contracts.Operators.OperatorAccount("synthetic-staff-01", "合成考务人员甲", Exam.Contracts.Operators.OperatorStatus.Active, false, created.AddMinutes(20), "synthetic-admin"),
                     new Exam.Contracts.Operators.OperatorAccount("synthetic-staff-02", "合成考务人员乙", Exam.Contracts.Operators.OperatorStatus.Pending, false, created.AddMinutes(45), null),
                     new Exam.Contracts.Operators.OperatorAccount("synthetic-staff-03", "合成考务人员丙", Exam.Contracts.Operators.OperatorStatus.Disabled, false, created.AddMinutes(50), "synthetic-admin"),
                 })
            viewModel.Accounts.Accounts.Add(new OperatorAccountRowViewModel(account, "synthetic-admin", _ => Task.CompletedTask, _ => { }));

    }

    private static void PopulateReport(MainViewModel viewModel)
    {
        var reports = viewModel.Reports;
        reports.ExamId = "synthetic-exam";
        Assert.True(reports.Session.TryAcceptPublished(reports.Session.BeginLoad("synthetic-exam"), SyntheticReport()));
    }

    /// <summary>两科、每科三题、五名合成考生；分数按固定规则生成，可复现。</summary>
    private static ReportCalculationResult SyntheticReport()
    {
        var subjects = new[]
        {
            new ReportSubject("subject-a", "合成科目A", 1, R(9),
                [new ReportQuestion("a-q1", 1, R(3)), new ReportQuestion("a-q2", 2, R(3)), new ReportQuestion("a-q3", 3, R(3))]),
            new ReportSubject("subject-b", "合成科目B", 2, R(6),
                [new ReportQuestion("b-q1", 1, R(2)), new ReportQuestion("b-q2", 2, R(2)), new ReportQuestion("b-q3", 3, R(2))])
        };
        var participants = Enumerable.Range(1, 5).Select(number => new ReportParticipant(
            $"synthetic-candidate-{number:00}", $"20270{number:00}", $"合成考生{number:00}", number <= 3 ? "01" : "02")).ToArray();
        var submissions = participants.Select((participant, index) =>
        {
            var grades = subjects.Select(subject =>
            {
                var questions = subject.Questions.Select((question, questionIndex) =>
                {
                    var pattern = (index + questionIndex + subject.Order) % 4;
                    var (answer, outcome, score) = pattern switch
                    {
                        0 => ("A", AnswerOutcome.Correct, question.MaximumScore),
                        1 => ("AB", AnswerOutcome.PartialCorrect, R(1)),
                        2 => ("C", AnswerOutcome.Wrong, R(0)),
                        _ => ("", AnswerOutcome.Blank, R(0))
                    };
                    return new ReportQuestionGrade(question.QuestionId, answer, outcome, score);
                }).ToArray();
                return new ReportSubjectGrade(subject.SubjectId,
                    questions.Aggregate(ExactRational.Zero, (sum, grade) => sum + grade.Score), questions);
            }).ToArray();
            return new ReportSubmission($"synthetic-submission-{index + 1:00}", "synthetic-exam", participant.CandidateId, 1,
                grades, grades.Aggregate(ExactRational.Zero, (sum, grade) => sum + grade.FinalScore));
        }).ToArray();
        return ReportCalculator.Calculate(new ReportCalculationInput("synthetic-exam", 1, subjects, participants, submissions));
    }

    private static ExactRational R(int value) => ExactRational.FromInteger(value);

    /// <summary>标签页标题仅保留 ASCII 字母数字；非字符串标题只用序号。</summary>
    private static string Slug(string? header)
    {
        if (header is null) return string.Empty;
        var builder = new StringBuilder();
        foreach (var character in header.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            DrainDispatcher();
        }
    }

    private static IEnumerable<string> SaveScreenshots(Window window, string directory, string name)
    {
        // 渲染窗口模板根（客户区），避免把非客户区尺寸算进位图造成空白边。
        var root = VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement child
            ? child
            : (FrameworkElement)window;
        var paths = new List<string>();
        foreach (var scale in Scales)
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, $"{name}-{scale.ToString("0.00", CultureInfo.InvariantCulture)}.png");
            using (var file = File.Create(path)) encoder.Save(file);
            paths.Add(path);
        }
        return paths;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
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
