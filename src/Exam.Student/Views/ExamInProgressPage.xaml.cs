using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
        Unloaded += (_, _) => _clock?.Stop();
    }

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
    }

    private void OnToggleMark(object sender, RoutedEventArgs e)
    {
        if (_marked.Remove(_currentQuestion))
        {
            if (_boxes.TryGetValue(_currentQuestion, out var box))
            {
                box.IsMarked = false;
            }
        }
        else
        {
            _marked.Add(_currentQuestion);
            if (_boxes.TryGetValue(_currentQuestion, out var box))
            {
                box.IsMarked = true;
            }
        }

        UpdateMarkButton();
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
            // B05 接入最终交卷 API（固定答案快照 + 回执 + 重试）；B01 演示成功页流程
            await Task.Delay(800);
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
