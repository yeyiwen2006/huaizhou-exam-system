using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Contracts.Submissions;

namespace Exam.Student.Views;

/// <summary>
/// 只接受当前考生的服务器持久化回执，3 秒后返回选择页；同场考试保持不可重入。
/// </summary>
public partial class SubmittedPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remain = 3;

    public SubmittedPage(MainWindow.Navigation navigation, SubmissionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        App.Services.Session.RecordSubmission(receipt);
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
