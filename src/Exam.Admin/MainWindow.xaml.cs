using System.Windows;
using System.Windows.Controls;
using Exam.Admin.ViewModels;
using Exam.Admin.Views;

namespace Exam.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private AdminLoginPage? _loginPage;

    public MainWindow() : this(new MainViewModel()) { }

    /// <summary>Accept an owned view model so navigation can be tested with the same production controls and an isolated transport.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _viewModel.Monitoring.CollectionCompleted += OnCollectionCompleted;
        DataContext = _viewModel;

        _viewModel.Connection.PropertyChanged += OnConnectionPropertyChanged;
        ShowLoginPage();
    }

    private void ShowLoginPage()
    {
        _loginPage?.Detach();
        _loginPage = new AdminLoginPage(_viewModel.Connection);
        PageRegion.Content = _loginPage;
        MainTabArea.Visibility = Visibility.Collapsed;
    }

    private void ShowRegistrationSuccessPage()
    {
        _loginPage?.Detach();
        _loginPage = null;
        PageRegion.Content = new RegistrationSuccessPage(_viewModel.Connection);
        MainTabArea.Visibility = Visibility.Collapsed;
    }

    private void ShowMainArea()
    {
        if (_loginPage is not null)
        {
            _loginPage.Detach();
            _loginPage = null;
        }
        PageRegion.Content = null;
        MainTabArea.Visibility = Visibility.Visible;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.ShowRegistrationSuccess))
        {
            if (_viewModel.Connection.ShowRegistrationSuccess)
                ShowRegistrationSuccessPage();
            else
                ShowLoginPage();
            return;
        }
        if (e.PropertyName != nameof(ConnectionViewModel.IsConnected)) return;
        if (_viewModel.Connection.IsConnected)
            ShowMainArea();
        else
            ShowLoginPage();
    }

    private void OnSelectedPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedPage == MainViewModel.ActivationPage)
            _viewModel.Activation.StartPolling();
        else
            _viewModel.Activation.StopPolling();
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
        _viewModel.Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _viewModel.Monitoring.CollectionCompleted -= OnCollectionCompleted;
        _loginPage?.Detach();
        _viewModel.Dispose();
    }
}
