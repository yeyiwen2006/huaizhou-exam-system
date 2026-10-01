using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Desktop.Shared.Controls;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.ServiceManagement;
using Exam.Student;
using Exam.Student.Core;
using Exam.Student.Views;

namespace Exam.StudentUnitTests;

/// <summary>
/// 设计评审用截图：在普通 Window（1366×768，不走学生端霸屏 MainWindow）里承载真实学生端页面，
/// 用合成设备、合成考生和合成试题渲染后保存 PNG。仅在设置 EXAM_UI_SCREENSHOT_DIRECTORY 时生效，
/// 未设置时直接返回（视为通过）。合成题目由 XeLaTeX 编译（PATH 上找不到时退回拉丁文本最小 PDF），
/// 选项坐标经生产 OptionRegionExtractor 抽取。全部内容为合成示例，不含真实试题或考生数据。
/// </summary>
[Trait("Category", "Latex")]
public sealed class UiScreenshotTests
{
    private const string DirectoryVariable = "EXAM_UI_SCREENSHOT_DIRECTORY";
    private static readonly double[] Scales = [1.0, 1.25, 1.5];
    private static readonly string[] OptionIds = ["A", "B", "C", "D"];

    [Fact]
    [Trait("Category", "Wpf")]
    public async Task StudentPages_WriteScreenshotsWithSyntheticData()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);

        var notes = new StringBuilder();
        var work = Path.Combine(Path.GetTempPath(), $"exam-ui-shots-{Guid.NewGuid():N}");
        try
        {
            var (pdfs, regions) = await CreateQuestionPdfsAsync(work, notes);
            File.WriteAllBytes(Path.Combine(directory, "synthetic-question.pdf"), pdfs["q1"]);
            Func<DeviceRecord, DeviceRecord> device = record => record with { ClientNumber = "A-017", Port = 9 };
            var candidate = new CandidateIdentity("candidate", "合成考生01", "01", "2027017");
            using var waitingFixture = await StudentPaperFixture.CreateAsync(optionRegions: regions, questionPdfs: pdfs,
                device: device, candidate: candidate, allowAbstain: true);
            using var answeringFixture = await StudentPaperFixture.CreateAsync(optionRegions: regions, questionPdfs: pdfs,
                device: device, candidate: candidate, allowAbstain: true);
            await OnStaAsync(() => RenderPagesAsync(directory, waitingFixture, answeringFixture, notes));
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "student-notes.txt"), notes.ToString(), new UTF8Encoding(false));
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { /* 临时目录清理失败不影响截图结论 */ }
        }
    }

    private static async Task RenderPagesAsync(string directory, StudentPaperFixture waitingFixture,
        StudentPaperFixture answeringFixture, StringBuilder notes)
    {
        using var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        var bindingSource = PresentationTraceSources.DataBindingSource;
        var previousLevel = bindingSource.Switch.Level;
        bindingSource.Switch.Level = SourceLevels.Warning;
        bindingSource.Listeners.Add(listener);
        var dispatcherErrors = new List<Exception>();
        void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            dispatcherErrors.Add(e.Exception);
            e.Handled = true;
        }
        Dispatcher.CurrentDispatcher.UnhandledException += OnUnhandled;

        // 不实例化 Exam.Student.App：其 OnStartup 会创建霸屏主窗口，异常处理器会弹出 MessageBox。
        // 这里只合并 App.xaml 当前声明的同一份共享主题。
        var application = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml", UriKind.Absolute)
        });
        // 必须使用生产资源：题号红点引用的 BoolToVis 由共享主题提供，宿主不得补齐缺失资源。
        Assert.IsType<BooleanToVisibilityConverter>(application.FindResource("BoolToVis"));
        // 页面导航写入一个不在任何窗口内的区域，避免页面自行跳转时把宿主窗口改为最大化/无边框。
        var navigationRegion = new ContentControl();
        var navigation = new MainWindow.Navigation(navigationRegion);
        var host = new ContentControl();
        var window = new Window
        {
            Width = 1366, Height = 768, Left = -4000, Top = -4000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.SingleBorderWindow, Title = "合成截图宿主", Content = host,
            Background = (Brush)application.FindResource("Brush.Background"),
            FontFamily = (FontFamily)application.FindResource("Font.Family")
        };
        // App.Services 固定使用 %LOCALAPPDATA%\HuaizhouExam\Student。本机已有激活记录说明装过真实学生端，
        // 截图测试不得读写其中的设备身份与本地答卷，直接拒绝运行。
        foreach (var marker in new[] { "devices.protected", "devices.protected.bak", "device.json" })
            Assert.False(File.Exists(Path.Combine(App.AppDataDirectory, marker)),
                $"检测到本机学生端数据（{marker}），截图测试不会使用该目录；请换一台没有学生端数据的机器或账户运行。");
        var services = App.Services;
        try
        {
            window.Show();

            // 1) 激活页：尚未配置设备，填入合成地址（TEST-NET 文档地址）。
            var activation = new ActivationPage(navigation);
            await ShowAsync(window, host, activation);
            ((ValidatedInput)activation.FindName("HostInput")).Text = "192.0.2.10";
            ((ValidatedInput)activation.FindName("PortInput")).Text = "5443";
            await CaptureAsync(window, directory, "student-activation");
            ((Banner)activation.FindName("ErrorBanner")).Show("服务器 IP 地址格式不正确！");
            await CaptureAsync(window, directory, "student-activation-banner");

            // 2) 登录页：已激活的合成设备 A-017。
            services.ConfigureDevice(waitingFixture.Device);
            var login = new LoginPage(navigation);
            await ShowAsync(window, host, login);
            ((ValidatedInput)login.FindName("NameInput")).Text = "合成考生01";
            ((ValidatedInput)login.FindName("ClassInput")).Text = "01";
            ((ValidatedInput)login.FindName("AdmissionInput")).Text = "2027017";
            await CaptureAsync(window, directory, "student-login");
            ((Banner)login.FindName("ErrorBanner")).Show("请填写姓名，班级和准考证号只能包含数字。");
            await CaptureAsync(window, directory, "student-login-banner");

            // 3) 选择考试页：服务器未配置，列表请求失败后改填合成考试并关闭失败横幅。
            services.Session.OnLoggedIn(waitingFixture.Session);
            var selection = new ExamSelectionPage(navigation);
            await ShowAsync(window, host, selection);
            await WaitUntilAsync(() => !((BusyOverlay)selection.FindName("Busy")).IsBusy, TimeSpan.FromSeconds(15));
            var rows = Assert.IsType<ObservableCollection<ExamSelectionPage.ExamRow>>(((ItemsControl)selection.FindName("ExamList")).ItemsSource);
            var today = DateTimeOffset.Now.Date;
            var running = new ExamOverview("exam", "合成模拟考试（第一场）", today.AddHours(9), today.AddHours(11),
                ["合成科目A", "合成科目B"], ExamLifecycle.Running, null);
            rows.Clear();
            rows.Add(new ExamSelectionPage.ExamRow(running));
            rows.Add(new ExamSelectionPage.ExamRow(new ExamOverview("synthetic-exam-2", "2027届清华复旦交大联合模拟笔试理科综合能力测试第一场正式",
                today.AddHours(14), today.AddHours(16), ["合成科目C"], ExamLifecycle.Scheduled, null)));
            rows.Add(new ExamSelectionPage.ExamRow(new ExamOverview("synthetic-exam-0", "合成练习考试",
                today.AddDays(-1).AddHours(9), today.AddDays(-1).AddHours(10), [], ExamLifecycle.Ended)));
            ((Banner)selection.FindName("EntryBanner")).Hide();
            await CaptureAsync(window, directory, "student-exam-selection");

            // 4) 等待页：试题包已下载、尚未确认参试。
            var waitingRuntime = await waitingFixture.StartRuntimeAsync(confirmParticipation: false);
            services.Session.RegisterExam(waitingRuntime);
            var waiting = new WaitingExamPage(navigation, running);
            await ShowAsync(window, host, waiting);
            await WaitUntilAsync(() => !((BusyOverlay)waiting.FindName("BusyLayer")).IsBusy &&
                ((ItemsControl)waiting.FindName("InstructionList")).Items.Count > 0, TimeSpan.FromSeconds(15));
            await CaptureAsync(window, directory, "student-waiting");
            host.Content = null;
            await IdleAsync();
            await services.Session.ReleaseExamAsync();

            // 5) 答题页：q1 已作答 B，q2 已标记；随后切到 q2 多选题。
            var answeringRuntime = await answeringFixture.StartRuntimeAsync();
            var answers = Assert.IsType<AnswerSession>(answeringRuntime.Answers);
            Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
            Assert.True((await answers.ToggleMarkAsync("q2")).Succeeded);
            Assert.True((await answers.SetCurrentQuestionAsync("q1")).Succeeded);
            services.Session.RegisterExam(answeringRuntime);
            var inProgress = new ExamInProgressPage(navigation, running, answeringRuntime);
            await ShowAsync(window, host, inProgress);
            var questionView = (PdfQuestionView)inProgress.FindName("QuestionView");
            var navList = (ItemsControl)inProgress.FindName("NavList");
            var ready = await WaitUntilAsync(() => questionView.HasQuestion && Descendants<Button>(navList).Any(),
                TimeSpan.FromSeconds(15), throwOnTimeout: false);
            if (!ready) notes.AppendLine("答题页：15 秒内题目或题号导航未出现，截图为当时状态。");
            await IdleAsync(TimeSpan.FromMilliseconds(500));
            await CaptureAsync(window, directory, "student-exam-in-progress");

            ToggleButton Choice(string id) => Assert.Single(Descendants<ToggleButton>(questionView), button => Equals(button.Tag, id));
            bool ReadyAt(string id) => answers.CurrentQuestionId == id && navList.IsEnabled && questionView.HasQuestion;
            async Task GoToAsync(string id)
            {
                Assert.Single(Descendants<Button>(navList), button => Equals(button.Tag, id))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => ReadyAt(id), TimeSpan.FromSeconds(5));
            }

            // 再点已选单选项只取消；选中另一项后，答案与下一题位置均经真实 SQLite 保存。
            Choice("B").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntilAsync(() => answers.Answers["q1"].Kind == AnswerKind.Empty && ReadyAt("q1"), TimeSpan.FromSeconds(5));
            Choice("A").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntilAsync(() => ReadyAt("q2"), TimeSpan.FromSeconds(5));
            var saved = Assert.IsType<AnswerStateRecord>(await answeringFixture.Store.LoadAnswerStateAsync(answeringFixture.Key));
            Assert.Equal(new[] { "A" }, saved.Answers["q1"].OptionIds);
            Assert.Equal("q2", saved.CurrentQuestionId);
            await GoToAsync("q1");

            var abstain = Assert.Single(Descendants<ToggleButton>(questionView), button => button.Tag is null);
            abstain.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitUntilAsync(() => answers.Answers["q1"].Kind == AnswerKind.Abstain && ReadyAt("q2"), TimeSpan.FromSeconds(5));
            await GoToAsync("q1");
            abstain = Assert.Single(Descendants<ToggleButton>(questionView), button => button.Tag is null);
            Assert.True(abstain.IsChecked);
            Assert.All(Descendants<ToggleButton>(questionView).Where(button => button.Tag is string),
                button => Assert.False(button.IsChecked));
            await CaptureAsync(window, directory, "student-exam-in-progress-abstain");
            // 使用真实导航模板测量十一题：前十题同一行，第十一题回到下一行的第一列。
            var originalSections = navList.ItemsSource;
            navList.ItemsSource = new[] { new GroupNavSection("synthetic", "合成科目（100分）", "synthetic-group",
                "一、单项选择题（每小题3分）", Enumerable.Range(1, 11)
                    .Select(i => new QuestionNavBox($"layout-{i}", i, false, false, false)).ToArray()) };
            await IdleAsync();
            window.UpdateLayout();
            var layoutButtons = Descendants<Button>(navList).ToArray();
            Assert.Equal(11, layoutButtons.Length);
            var positions = layoutButtons.Select(b => b.TransformToAncestor(navList).Transform(new Point())).ToArray();
            Assert.All(positions.Take(10), p => Assert.Equal(positions[0].Y, p.Y, 2));
            Assert.True(positions[10].Y > positions[0].Y);
            Assert.Equal(positions[0].X, positions[10].X, 2);
            navList.ItemsSource = originalSections;
            await IdleAsync();
            if (Descendants<Button>(navList).FirstOrDefault(button => Equals(button.Tag, "q2")) is { } q2)
            {
                q2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => ((TextBlock)inProgress.FindName("CurrentQuestionText")).Text.StartsWith("第 2 题", StringComparison.Ordinal) &&
                    questionView.HasQuestion, TimeSpan.FromSeconds(15), throwOnTimeout: false);
                await IdleAsync(TimeSpan.FromMilliseconds(500));
                await CaptureAsync(window, directory, "student-exam-in-progress-q2");
                Choice("A").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => answers.Answers["q2"].OptionIds.Contains("A") && ReadyAt("q2"), TimeSpan.FromSeconds(5));
                Choice("B").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => answers.Answers["q2"].OptionIds.Count == 2 && ReadyAt("q2"), TimeSpan.FromSeconds(5));
                Assert.Single(Descendants<ToggleButton>(questionView), button => button.Tag is null)
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => answers.Answers["q2"].Kind == AnswerKind.Abstain && ReadyAt("q2"), TimeSpan.FromSeconds(5));
                Assert.False(answeringRuntime.HasFixedSubmission); // 末题不循环，也不自动交卷。
                Assert.Single(Descendants<ToggleButton>(questionView), button => button.Tag is null)
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => answers.Answers["q2"].Kind == AnswerKind.Empty && ReadyAt("q2"), TimeSpan.FromSeconds(5));

                await GoToAsync("q1");
                // 关闭测试专用存储注入写失败，验证页面不会在答案尚未保存时前进。
                answeringFixture.Store.Dispose();
                Choice("C").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => answers.Status == AnswerSessionStatus.WriteBlocked, TimeSpan.FromSeconds(5));
                Assert.Equal("q1", answers.CurrentQuestionId);
                Assert.StartsWith("第 1 题", ((TextBlock)inProgress.FindName("CurrentQuestionText")).Text, StringComparison.Ordinal);
                Assert.Equal(AnswerKind.Abstain, answers.Answers["q1"].Kind);
            }
            else notes.AppendLine("答题页：未找到 q2 题号按钮，未生成 q2 截图。");
            host.Content = null;
            await IdleAsync();

            // 6) 交卷成功页：合成服务器回执；3 秒自动返回前截图并卸载。
            var submitted = new SubmittedPage(navigation, new SubmissionReceipt("synthetic-receipt", "subm-synthetic", "exam",
                "candidate", "participation", DateTimeOffset.UtcNow, 1, 1));
            await ShowAsync(window, host, submitted);
            await CaptureAsync(window, directory, "student-submitted");
            host.Content = null;
            await IdleAsync();
            await services.Session.ReleaseExamAsync();

            if (navigationRegion.Content is not null)
                notes.AppendLine($"注意：页面自行导航到了 {navigationRegion.Content.GetType().Name}（未显示在截图中）。");
        }
        finally
        {
            host.Content = null;
            window.Close();
            try { await services.Session.ReleaseExamAsync(); } catch (InvalidOperationException) { }
            Dispatcher.CurrentDispatcher.UnhandledException -= OnUnhandled;
            listener.Flush();
            bindingSource.Listeners.Remove(listener);
            bindingSource.Switch.Level = previousLevel;
            File.WriteAllText(Path.Combine(directory, "student-binding-trace.txt"), trace.ToString(), new UTF8Encoding(false));
            foreach (var group in dispatcherErrors.Select(error => (error.GetType().Name, Inner: error.GetBaseException().Message))
                         .GroupBy(item => item))
                notes.AppendLine($"未处理的调度器异常 ×{group.Count()}：{group.Key.Name}: {group.Key.Inner}");
        }
    }

    /// <summary>用 XeLaTeX 编译两道合成中文题（单选、多选），再按生产抽取器读取选项坐标。</summary>
    private static async Task<(IReadOnlyDictionary<string, byte[]> Pdfs, OptionRegionArtifact Regions)> CreateQuestionPdfsAsync(
        string work, StringBuilder notes)
    {
        var sources = new Dictionary<string, (string File, string Body)>
        {
            ["q1"] = ("questions/衔接合成考试_1.tex", """
                {\linespread{1}\selectfont
                \noindent 合成示例：设 $f(x)=2x^2-x+1$，则 $f(3)$ 的值为（\quad）。
                \par}

                {\linespread{2}\selectfont
                \noindent\optmark{q1}{A}{A.}\hspace{\fontdimen2\font} $14$\\
                \optmark{q1}{B}{B.}\hspace{\fontdimen2\font} $16$\\
                \optmark{q1}{C}{C.}\hspace{\fontdimen2\font} $18$\\
                \optmark{q1}{D}{D.}\hspace{\fontdimen2\font} $20$
                \par}
                """),
            ["q2"] = ("questions/衔接合成考试_2.tex", """
                {\linespread{1}\selectfont
                \noindent 合成多选题：下列各数中，属于偶数的有（\quad）。
                \par}

                {\linespread{2}\selectfont
                \noindent\optmark{q2}{A}{A.}\hspace{\fontdimen2\font} $2$\\
                \optmark{q2}{B}{B.}\hspace{\fontdimen2\font} $4$\\
                \optmark{q2}{C}{C.}\hspace{\fontdimen2\font} $7$\\
                \optmark{q2}{D}{D.}\hspace{\fontdimen2\font} $9$
                \par}
                """)
        };
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        if (toolchain is null)
        {
            notes.AppendLine("PATH 上没有 xelatex：题目使用拉丁文本最小 PDF 与手工坐标，不代表真实排版。");
            return FallbackPdfs();
        }

        var packageRoot = Path.Combine(work, "package");
        var output = Path.Combine(work, "output");
        Directory.CreateDirectory(Path.Combine(packageRoot, "questions"));
        var compiler = new XeLaTeXCompiler(new ExternalProcessRunner(), toolchain, TimeSpan.FromMinutes(3));
        var pdfPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (questionId, (file, body)) in sources)
        {
            File.WriteAllText(Path.Combine(packageRoot, file), $$$"""
                \documentclass{ctexart}
                \usepackage{amsmath}
                \usepackage{unicode-math}
                \setmainfont{Times New Roman}
                \setmathfont{XITS Math}
                \usepackage[a4paper,margin=1in]{geometry}
                \pagestyle{empty}
                \usepackage{hyperref}
                \hypersetup{hidelinks}
                \DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
                \begin{document}
                {{{body}}}
                \end{document}
                """, new UTF8Encoding(false));
            var result = await compiler.CompileAsync(new TeXCompileRequest(packageRoot, file, Path.Combine(output, questionId)));
            Assert.True(result.Succeeded, string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
            pdfPaths[questionId] = result.OutputPdfPath!;
        }
        var extraction = new OptionRegionExtractor().Extract(new OptionRegionRequest("exam", 1,
            pdfPaths.Select(pair => new OptionRegionSourceQuestion(pair.Key, pair.Value, OptionIds)).ToArray()));
        Assert.True(extraction.Succeeded, string.Join("；", extraction.Issues.Select(issue => issue.Message)));
        notes.AppendLine($"题目 PDF 由 {toolchain.ExecutablePath} 编译（合成中文题），选项坐标由 OptionRegionExtractor 抽取。");
        return (pdfPaths.ToDictionary(pair => pair.Key, pair => File.ReadAllBytes(pair.Value)), extraction.Artifact!);
    }

    private static (IReadOnlyDictionary<string, byte[]>, OptionRegionArtifact) FallbackPdfs()
    {
        static byte[] Pdf(string stem, string[] options)
        {
            var content = new StringBuilder($"BT /F1 14 Tf 72 740 Td ({stem}) Tj ET\n");
            for (var i = 0; i < options.Length; i++)
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 14 Tf 90 {700 - i * 30} Td ({OptionIds[i]}. {options[i]}) Tj ET\n");
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
                $"<< /Length {content.Length} >>\nstream\n{content}endstream",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
            };
            var builder = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Length; i++)
            {
                offsets.Add(builder.Length);
                builder.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            var xref = builder.Length;
            builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets) builder.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
            builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }
        static List<Exam.Contracts.Importing.OptionRegion> Regions() => OptionIds.Select((id, i) =>
            new Exam.Contracts.Importing.OptionRegion(id, 0, 88d / 612, (792d - 700 + i * 30 - 13) / 792, 18d / 612, 17d / 792)).ToList();
        return (new Dictionary<string, byte[]>
        {
            ["q1"] = Pdf("Synthetic single choice: if x + 2 = 5, then x = ?", ["1", "3", "5", "7"]),
            ["q2"] = Pdf("Synthetic multiple choice: which numbers are even?", ["2", "4", "7", "9"])
        }, new OptionRegionArtifact(OptionRegionArtifact.CurrentArtifactVersion, OptionRegionArtifact.AnchorSchemeName, "exam", 1,
            [new OptionRegionQuestion("q1", Regions()), new OptionRegionQuestion("q2", Regions())]));
    }

    private static async Task ShowAsync(Window window, ContentControl host, FrameworkElement page)
    {
        host.Content = page;
        await IdleAsync();
        window.UpdateLayout();
    }

    private static async Task CaptureAsync(Window window, string directory, string name)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            await IdleAsync();
        }
        // 渲染窗口模板根（客户区），不包括系统标题栏。
        var root = VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement child
            ? child
            : window;
        foreach (var scale in Scales)
        {
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"{name}-{scale.ToString("0.00", CultureInfo.InvariantCulture)}.png"));
            encoder.Save(file);
        }
    }

    private static async Task IdleAsync(TimeSpan? delay = null)
    {
        if (delay is { } wait) await Task.Delay(wait);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, bool throwOnTimeout = true)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout)
            {
                if (throwOnTimeout) throw new TimeoutException("截图前等待页面就绪超时。");
                return false;
            }
            await IdleAsync(TimeSpan.FromMilliseconds(100));
        }
        return true;
    }

    private static async Task OnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke((Action)(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF 截图线程没有退出。");
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
