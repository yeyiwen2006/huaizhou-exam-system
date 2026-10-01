using System.Windows;
using Exam.Admin.ViewModels;

namespace Exam.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        _viewModel = new MainViewModel();
        InitializeComponent();
        _viewModel.Monitoring.CollectionCompleted += OnCollectionCompleted;
        _viewModel.Connection.PasswordsCleared += OnPasswordsCleared;
        DataContext = _viewModel;
    }

    // 口令框不支持绑定；口令只交给登录条的视图模型，登录或注册结束后清空。
    private void Password_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Connection.Password = PasswordInput.Password;

    private void ConfirmPassword_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Connection.ConfirmPassword = ConfirmPasswordInput.Password;

    private void OnPasswordsCleared()
    {
        PasswordInput.Clear();
        ConfirmPasswordInput.Clear();
    }

    private void OnCollectionCompleted(Exam.Contracts.Submissions.CollectionCompletedNotification item)
    {
        if (!IsVisible) return;
        _ = Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "本场考试全部答卷已成功回收",
            $"考试 {item.ExamId} 的 {item.ParticipantCount} 名实际参试考生答卷已全部保存（{item.CompletedAt:yyyy-MM-dd HH:mm:ss}）。\n如果之后又有考生进入考试，系统会重新统计；成绩在考试结束后生成。",
            confirmText: "知道了", cancelText: "关闭");
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Monitoring.CollectionCompleted -= OnCollectionCompleted;
        _viewModel.Connection.PasswordsCleared -= OnPasswordsCleared;
        _viewModel.Dispose();
    }
}
