using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Exam.Admin.ViewModels;
using Exam.Admin.Views;
using Exam.Admin.ServiceManagement;

namespace Exam.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private AdminLoginPage? _loginPage;
    private readonly AdminWorkspaceRouter _router = new();
    public ServiceManagementViewModel Management { get; }
    public AdminWorkspaceRoute WorkspaceRoute => _router.Route;
    private ServiceManagementView? _managementPage;
    private int _businessPage;
    private bool _confirmingLogout;

    public MainWindow() : this(new MainViewModel(), ServiceManagementViewModel.CreateDefault()) { }

    public MainWindow(MainViewModel viewModel) : this(viewModel, ServiceManagementViewModel.CreateDefault()) { }

    public MainWindow(MainViewModel viewModel, ServiceManagementViewModel management)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Management = management ?? throw new ArgumentNullException(nameof(management));
        if (System.Windows.Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        InitializeComponent();
        _viewModel.Monitoring.CollectionCompleted += OnCollectionCompleted;
        _viewModel.ConfirmImportPublicationAsync = message =>
            Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "确认正式发布试卷", message,
                confirmText: "确认发布", cancelText: "取消", isDanger: true);
        _viewModel.Home.ChooseAddExamAsync = () => ExamArchiveDialogs.ShowAddAsync(this);
        _viewModel.Home.ChooseArchiveConflictAsync = preview => ExamArchiveDialogs.ShowConflictAsync(this, preview);
        DataContext = _viewModel;

        _viewModel.Connection.PropertyChanged += OnConnectionPropertyChanged;
        _viewModel.BusinessSessionChanged += OnBusinessSessionChanged;
        Management.ActiveNodeVerified += OnActiveNodeVerified;
        ShowLoginPage();
    }

    private void ShowLoginPage()
    {
        DetachLogin();
        _loginPage = new AdminLoginPage(_viewModel.Connection);
        FailureManagementButton.Visibility = Visibility.Visible;
        PageRegion.Content = _loginPage;
        MainTabArea.Visibility = Visibility.Collapsed;
    }

    private void ShowRegistrationSuccessPage()
    {
        DetachLogin();
        FailureManagementButton.Visibility = Visibility.Collapsed;
        PageRegion.Content = new RegistrationSuccessPage(_viewModel.Connection);
        MainTabArea.Visibility = Visibility.Collapsed;
    }

    private void ShowMainArea()
    {
        DetachLogin();
        FailureManagementButton.Visibility = Visibility.Collapsed;
        PageRegion.Content = null;
        MainTabArea.Visibility = Visibility.Visible;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
        {
            OnBusinessSessionChanged();
            _router.BusinessConnectionChanged(_viewModel.Connection.IsConnected);
            if (!_viewModel.Connection.IsConnected && _router.Route != AdminWorkspaceRoute.LocalManagement)
            { Management.Deactivate(); ShowLoginPage(); }
            else if (_router.Route != AdminWorkspaceRoute.LocalManagement) ShowMainArea();
            return;
        }
        if (_router.Route == AdminWorkspaceRoute.LocalManagement) return;
        if (e.PropertyName == nameof(ConnectionViewModel.ShowRegistrationSuccess))
        {
            if (_viewModel.Connection.ShowRegistrationSuccess)
                ShowRegistrationSuccessPage();
            else
                ShowLoginPage();
            return;
        }
    }

    private void OnBusinessSessionChanged()
    {
        var session = _viewModel.CaptureBusinessSession();
        // Management has its own authenticated sessions and must remain usable while
        // the business endpoint goes away during takeover or a service restart.
        if (session is null && _router.Route == AdminWorkspaceRoute.LocalManagement) return;
        Management.SetBusinessSession(session);
    }

    private async void OnActiveNodeVerified(string serverId, string nodeId, long epoch)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try { await _viewModel.ReconnectToActiveNodeAsync(serverId, nodeId, epoch, deadline.Token); }
        catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException or OperationCanceledException or
            System.Security.Cryptography.CryptographicException) { /* Business requests retain their original IDs and retry the trusted route. */ }
    }

    private void OnManagementRequested(object sender, RoutedEventArgs e) => OpenManagement();

    private async void OnLogoutRequested(object sender, RoutedEventArgs e)
    {
        if (_confirmingLogout || !_viewModel.Connection.IsConnected) return;
        _confirmingLogout = true;
        try
        {
            if (await Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "确认退出登录",
                "确定退出当前考务账号并返回登录页面吗？", confirmText: "确定", cancelText: "取消") &&
                _viewModel.Connection.LogoutCommand.CanExecute(null))
            {
                await Management.LogoutAsync();
                Management.SetBusinessSession(null);
                _viewModel.Connection.LogoutCommand.Execute(null);
            }
        }
        finally { _confirmingLogout = false; }
    }

    private void OnExamHidingMenuOpened(object sender, RoutedEventArgs e)
    {
        // The scope selector is inside the clickable exam card; opening it must not open the exam.
        e.Handled = true;
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnExamArchiveClicked(object sender, RoutedEventArgs e)
    {
        // The export action belongs to the card but must not invoke its OpenCommand.
        e.Handled = true;
    }

    private void OnScoreFieldFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Panel fields && e.NewFocus is TextBox input)
            fields.Tag = input;
    }

    private void OnScoreFieldsDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Changing the selected group must not reuse an input selection from the previous group.
        if (sender is Panel fields) fields.Tag = null;
    }

    private void OnScoreTokenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Panel { Tag: Panel { Tag: TextBox input } fields } ||
            e.OriginalSource is not Button { Tag: string token } || !input.IsEnabled || input.IsReadOnly ||
            !fields.IsAncestorOf(input) || !ReferenceEquals(input.DataContext, fields.DataContext)) return;

        var start = input.SelectionStart;
        var selected = input.SelectedText;
        input.BeginChange();
        try
        {
            input.SelectedText = token == "()" ? "(" + selected + ")" : token;
            if (token == "()") input.Select(start + 1, selected.Length);
            else input.Select(start + token.Length, 0);
        }
        finally { input.EndChange(); }
        input.Focus();
        e.Handled = true;
    }
    public void OpenManagement()
    {
        if (_router.Route == AdminWorkspaceRoute.LocalManagement && MainTabArea.SelectedIndex == MainViewModel.ManagementPage &&
            ManagementPageRegion.Content is not null) return;
        Management.SetBusinessSession(_viewModel.CaptureBusinessSession());
        FailureManagementButton.Visibility = Visibility.Collapsed;
        if (_router.Route != AdminWorkspaceRoute.LocalManagement && _viewModel.SelectedPage != MainViewModel.ManagementPage)
            _businessPage = _viewModel.SelectedPage;
        _router.OpenManagement();
        DetachLogin();
        _viewModel.Activation.StopPolling();
        _viewModel.Monitoring.IsPageActive = false;
        _viewModel.Reports.IsPageActive = false;
        PageRegion.Content = null;
        MainTabArea.Visibility = Visibility.Visible;
        if (_managementPage is null)
        {
            _managementPage = new ServiceManagementView(Management);
            _managementPage.BackRequested += ReturnToBusiness;
        }
        ManagementPageRegion.Content = _managementPage;
        _viewModel.SelectedPage = MainViewModel.ManagementPage;
        Management.Activate();
    }
    public void ReturnToBusiness()
    {
        Management.Deactivate();
        _router.BusinessConnectionChanged(_viewModel.Connection.IsConnected);
        _router.ReturnToBusiness();
        if (_router.Route == AdminWorkspaceRoute.BusinessWorkspace)
        {
            ShowMainArea();
            _viewModel.SelectedPage = _businessPage;
            if (_viewModel.SelectedPage == MainViewModel.ActivationPage) _viewModel.Activation.StartPolling();
            _viewModel.Monitoring.IsPageActive = _viewModel.SelectedPage == MainViewModel.MonitoringPage;
            _viewModel.Reports.IsPageActive = _viewModel.SelectedPage == MainViewModel.ReportsPage;
        }
        else if (_viewModel.Connection.ShowRegistrationSuccess) ShowRegistrationSuccessPage();
        else ShowLoginPage();
    }
    private void DetachLogin()
    {
        if (_loginPage is null) return;
        _loginPage.Detach(); _loginPage = null;
    }

    private void OnSelectedPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabArea)) return;
        if (MainTabArea.SelectedIndex == MainViewModel.ManagementPage) { OpenManagement(); return; }
        if (_router.Route == AdminWorkspaceRoute.LocalManagement)
        {
            Management.Deactivate();
            _router.BusinessConnectionChanged(_viewModel.Connection.IsConnected);
            _router.ReturnToBusiness();
            if (!_viewModel.Connection.IsConnected) { ShowLoginPage(); return; }
            ShowMainArea();
        }
        _businessPage = MainTabArea.SelectedIndex;
        if (_viewModel.SelectedPage == MainViewModel.ActivationPage)
            _viewModel.Activation.StartPolling();
        else
            _viewModel.Activation.StopPolling();
    }

    private readonly Queue<Exam.Contracts.Submissions.CollectionCompletedNotification> _collectionNotifications = new();
    private bool _showingCollectionNotification;

    private async void OnCollectionCompleted(Exam.Contracts.Submissions.CollectionCompletedNotification item)
    {
        _collectionNotifications.Enqueue(item);
        if (_showingCollectionNotification) return;
        _showingCollectionNotification = true;
        try
        {
            while (_collectionNotifications.TryDequeue(out var notification))
            {
                // 收齐弹窗显示考试名称；查不到名称时用中性说法，不把内部标识显示给考务。
                var examName = _viewModel.DescribeExam(notification.ExamId);
                var subject = examName is null ? "本场考试" : $"“{examName}”考试";
                await Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "本场考试全部答卷已成功回收",
                    $"{subject}的 {notification.ParticipantCount} 名实际参加考试考生答卷已全部保存（{notification.CompletedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}）。\n如果之后又有考生进入考试，系统会重新统计；成绩在考试结束后生成。",
                    confirmText: "知道了", cancelText: "关闭");
            }
        }
        finally { _showingCollectionNotification = false; }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _viewModel.BusinessSessionChanged -= OnBusinessSessionChanged;
        Management.ActiveNodeVerified -= OnActiveNodeVerified;
        _viewModel.Monitoring.CollectionCompleted -= OnCollectionCompleted;
        DetachLogin();
        if (_managementPage is not null) { _managementPage.BackRequested -= ReturnToBusiness; _managementPage.Detach(); }
        Management.Dispose();
        _viewModel.Dispose();
    }
}
