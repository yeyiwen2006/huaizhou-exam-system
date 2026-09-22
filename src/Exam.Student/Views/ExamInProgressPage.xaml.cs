using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 考试进行页（B01 页框）：
/// 左侧试题切换栏（学科 → 题型 → 题号方框）+ 右侧题目区（B03 接入 PDF 与选项）。
/// 题号方框：已答绿色、标记右上角红点、当前题主色描边；点击方框跳转到该题。
/// 时间使用服务器时间（B05 接入 60 秒校时；B01 以本机时钟演示界面结构）。
/// </summary>
public partial class ExamInProgressPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly ExamOverview _exam;
    private readonly ObservableCollection<NavBlock> _blocks = new();
    private readonly Dictionary<int, QuestionBox> _boxes = new();
    private readonly HashSet<int> _marked = new();
    private readonly HashSet<int> _answered = new();

    /// <summary>B04 答题会话：答案/私人标记本地事务存储（版本化 + 待发送快照）。</summary>
    private AnswerSession? _answerSession;

    /// <summary>页头右侧时钟胶囊（因 XAML 命名作用域限制经绑定注入）。</summary>
    public StatusPill ClockPillContent { get; } = new()
    {
        Text = "服务器时间 --:--:--",
        Level = StatusLevel.Info,
    };
    private int _currentQuestion;
    private DispatcherTimer? _clock;

    public ExamInProgressPage(MainWindow.Navigation navigation, ExamOverview exam)
    {
        _navigation = navigation;
        _exam = exam;
        InitializeComponent();
        DataContext = App.Services.Session;

        BuildNav();
        SelectQuestion(1);
        ShowPaperStatus();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
        Loaded += async (_, _) => await StartAnswerSessionAsync();
        Unloaded += (_, _) => _clock?.Stop();
    }

    /// <summary>
    /// B04：启动答题会话——按服务器、考试和考生恢复本地状态并核对权限。
    /// 恢复的私人标记立即回显；权限不符或恢复失败时明示并锁定作答。
    /// </summary>
    private async Task StartAnswerSessionAsync()
    {
        var session = App.Services.Session;
        if (session.Candidate is null || session.CurrentExam is null)
        {
            return;
        }

        var authorization = session.CurrentExam.Authorization;
        if (authorization is null)
        {
            // 理论不可达：进入本页必经 B02 确认参试（授权上下文随解锁传入）
            ShowLocalStateError("参试授权信息缺失，无法启用本地保存。");
            return;
        }

        var answerSession = new AnswerSession(App.Services.LocalState);
        answerSession.WriteBlocked += ShowLocalStateError;

        var restore = await answerSession.StartAsync(
            new LocalStateKey(App.Services.ServerKey, _exam.ExamId, session.Candidate.Identity.CandidateId),
            new AnswerSessionContext(
                authorization.ParticipationId,
                authorization.EntryVersion,
                authorization.AuthorizationVersion,
                authorization.PaperVersion,
                authorization.DeviceId));

        if (!restore.IsUsable)
        {
            ShowLocalStateError(restore.Message ?? "本地状态恢复失败，已锁定作答。");
        }
        else if (restore.Status == AnswerSessionStatus.Active && answerSession.Marks.Count > 0)
        {
            // 恢复的私人标记回显（S03：标记重启保留）
            foreach (var markId in answerSession.Marks)
            {
                if (int.TryParse(markId, out var number) && _boxes.TryGetValue(number, out var box))
                {
                    _marked.Add(number);
                    box.IsMarked = true;
                }
            }

            foreach (var pair in answerSession.Answers)
            {
                if (pair.Value.Kind is AnswerKind.Selected or AnswerKind.Abstain &&
                    int.TryParse(pair.Key, out var number))
                {
                    _answered.Add(number);
                }
            }

            UpdateMarkButton();
        }

        _answerSession = answerSession;
    }

    private void ShowLocalStateError(string message) => PaperBanner.Show(message);

    /// <summary>
    /// 按考试结构生成试题切换栏。结构应由服务器下发的试卷元数据驱动，
    /// 不在代码中写死；B01 用合成样例结构演示页框（B02/B03 接入真实元数据）。
    /// </summary>
    private void BuildNav()
    {
        foreach (var (subjectTitle, groupTitle, count) in GetSyntheticNavStructure())
        {
            var block = new NavBlock(subjectTitle, groupTitle);
            foreach (var n in Enumerable.Range(_boxes.Count + 1, count))
            {
                var box = new QuestionBox { Number = n };
                var captured = n;
                box.BoxClicked += (_, _) => SelectQuestion(captured);
                _boxes[n] = box;
                block.Boxes.Add(box);
            }

            _blocks.Add(block);
        }

        NavList.ItemsSource = _blocks;
    }

    /// <summary>合成样例导航结构（对应需求考试说明示例一，仅演示页框）。</summary>
    private static IEnumerable<(string SubjectTitle, string GroupTitle, int Count)> GetSyntheticNavStructure()
    {
        yield return ("数学（100分）", "不定项选择题（每小题3分/4分）", 30);
        yield return ("物理（60分）", "不定项选择题（每小题3分）", 20);
        yield return ("化学（40分）", "单项选择题（每小题{正确选项总数}分）", 24);
    }

    /// <summary>
    /// B02：显示已解锁试卷的状态。真实题目渲染为 B03（PDF + 选项圆钮）；
    /// 此处仅呈现“已解密”事实与内容规模，证明明文仅在开考后进入内存。
    /// </summary>
    private void ShowPaperStatus()
    {
        var exam = App.Services.Session.CurrentExam;
        PaperBanner.Message = exam is null
            ? "题目内容区：B03 接入原版 PDF 试题显示与选项圆形按钮。当前为 B01 页框。"
            : $"试题已解密（{exam.Payload.Length / 1024.0:F1} KB 合成样例，B02 解锁产物）。题目渲染与选项交互由 B03 接入。";
    }

    private void SelectQuestion(int number)
    {
        if (_boxes.TryGetValue(_currentQuestion, out var previous))
        {
            previous.IsCurrent = false;
        }

        _currentQuestion = number;
        if (_boxes.TryGetValue(number, out var current))
        {
            current.IsCurrent = true;
        }

        CurrentQuestionText.Text = $"第 {number} 题";
        UpdateMarkButton();

        // B04：当前题随本地状态落盘（B05 同步上报 CurrentQuestionId）
        if (_answerSession is { Status: AnswerSessionStatus.Active } session)
        {
            _ = session.SetCurrentQuestionAsync(number.ToString());
        }
    }

    private async void OnToggleMark(object sender, RoutedEventArgs e)
    {
        var nowMarked = !_marked.Contains(_currentQuestion);
        ApplyMark(_currentQuestion, nowMarked);
        UpdateMarkButton();

        // B04：私人标记本地落盘（仅本机；失败回滚并明示阻止新增修改）
        if (_answerSession is { } session)
        {
            var result = await session.ToggleMarkAsync(_currentQuestion.ToString());
            if (!result.Succeeded)
            {
                ApplyMark(_currentQuestion, !nowMarked);
                UpdateMarkButton();
            }
        }
    }

    private void ApplyMark(int number, bool marked)
    {
        if (marked)
        {
            _marked.Add(number);
        }
        else
        {
            _marked.Remove(number);
        }

        if (_boxes.TryGetValue(number, out var box))
        {
            box.IsMarked = marked;
        }
    }

    private void UpdateMarkButton()
    {
        MarkButton.Content = _marked.Contains(_currentQuestion) ? "取消标记" : "标记本题";
    }

    private void UpdateClock()
    {
        // B05 接入服务器时间（60 秒校时 + 服务器指令）；B01 仅展示页框
        ClockPillContent.Text = $"服务器时间 {DateTimeOffset.Now:HH:mm:ss}";
        var remain = _exam.ScheduledEnd - DateTimeOffset.Now;
        ClockPillContent.Text = remain > TimeSpan.Zero
            ? $"{ClockPillContent.Text} · 距结束 {remain:hh\\:mm\\:ss}"
            : $"{ClockPillContent.Text} · 考试已到结束时间";
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            window,
            "提前交卷",
            $"已答 {_answered.Count} 题，已标记 {_marked.Count} 题。\n确定提前交卷吗？交卷后不能继续修改。",
            "提前交卷",
            "继续答题",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        Busy.IsBusy = true;
        try
        {
            // B04：先本地固定提交（固定答卷快照；写失败阻止交卷并明示），
            // B05 接入最终交卷 API（上报固定快照 + 服务器持久化回执 + 重试）。
            if (_answerSession is { } session)
            {
                var fix = await session.FixSubmissionAsync($"subm-{RequestIds.New()[..12]}");
                if (!fix.Succeeded)
                {
                    ShowLocalStateError($"交卷失败：{fix.Error}");
                    return;
                }
            }
            else
            {
                // 无本地会话（如授权缺失）不允许交卷
                ShowLocalStateError("本地状态不可用，不能交卷。请监考员处理。");
                return;
            }
        }
        finally
        {
            Busy.IsBusy = false;
        }

        _navigation.GoToSubmitted();
    }

    /// <summary>切换栏块：学科 → 题型 → 题号方框。</summary>
    public sealed class NavBlock
    {
        public NavBlock(string subjectTitle, string groupTitle)
        {
            SubjectTitle = subjectTitle;
            GroupTitle = groupTitle;
        }

        public string SubjectTitle { get; }

        public string GroupTitle { get; }

        public ObservableCollection<QuestionBox> Boxes { get; } = new();
    }
}
