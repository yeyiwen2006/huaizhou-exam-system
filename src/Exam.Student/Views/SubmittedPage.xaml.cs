using System.Windows.Controls;
using System.Windows.Threading;

namespace Exam.Student.Views;

/// <summary>
/// 已成功交卷页：传输成功后显示，3 秒后跳转选择考试页面。
/// 再次进入同一考试将重新从空白答卷开始（B05 接入再次进入拦截）。
/// </summary>
public partial class SubmittedPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remain = 3;

    public SubmittedPage(MainWindow.Navigation navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        DataContext = App.Services.Session;

        _timer.Tick += OnTick;
        _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remain--;
        if (_remain <= 0)
        {
            _timer.Stop();
            _navigation.GoToExamSelection();
            return;
        }

        RedirectText.Text = $"{_remain} 秒后自动返回选择考试页面";
    }
}
