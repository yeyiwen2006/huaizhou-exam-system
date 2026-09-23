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
        DataContext = _viewModel;
    }

    private void OperatorToken_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.Connection.OperatorToken = passwordBox.Password;
        }
    }

    private void OnCollectionCompleted(Exam.Contracts.Submissions.CollectionCompletedNotification item)
    {
        if (!IsVisible) return;
        _ = Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "服务器已记录收卷完成",
            $"考试 {item.ExamId} 在 {item.CompletedAt:yyyy-MM-dd HH:mm:ss zzz} 已记录名单版本 {item.RosterVersion}、{item.ParticipantCount} 人收齐。\n这是持久化历史事件，不保证当前名单仍已收齐，也不代表成绩已开放。",
            confirmText: "知道了", cancelText: "关闭");
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Monitoring.CollectionCompleted -= OnCollectionCompleted;
        _viewModel.Dispose();
    }
}
