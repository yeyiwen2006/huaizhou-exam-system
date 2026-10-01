using System.ComponentModel;
using System.Windows.Input;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Exams;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 考务端主窗口。主页列出全部考试；进入某场考试后，考试设置、设备监控和成绩统计都作用于这场考试。
/// 导航页序号：0 全部考试，1 考试设置，2 考生名单，3 激活与试场分组，4 设备监控，5 成绩统计，6 考务账号（仅管理员）。
/// </summary>
public sealed class MainViewModel : BindableBase, IDisposable
{
    public const int HomePage = 0;
    public const int SettingsPage = 1;
    public const int ActivationPage = 5; // 激活与试场分组：不与具体考试挂钩，不显示当前考试条。
    private readonly AdminApiClient _api;
    private ExamSummary? _currentExam;
    private bool _isCreatingExam;
    private int _selectedPage;

    public MainViewModel() : this(new AdminApiClient())
    {
    }

    /// <summary>使用指定的服务器客户端（测试用合成服务器）；客户端随本对象一同释放。</summary>
    public MainViewModel(AdminApiClient api)
    {
        _api = api;
        Connection = new ConnectionViewModel(_api);
        Exam = new ExamConfigurationViewModel(_api);
        ManifestPreflight = new ManifestPreflightViewModel(_api);
        Roster = new RosterViewModel(new RosterWorkbookService(), new FileDialogService());
        Activation = new ActivationRoomViewModel(_api);
        Monitoring = new MonitoringViewModel(_api);
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        Reports = new Exam.Admin.Reporting.ReportPageViewModel(_api, new FileDialogService());
        Home = new ExamHomeViewModel(_api);
        Accounts = new OperatorAccountsViewModel(_api);
        Home.ExamOpened += OpenExam;
        Home.NewExamRequested += NewExam;
        Exam.ExamSaved += OnExamSaved;
        _api.ConfigurationChanged += OnConfigurationChanged;
        BackToHomeCommand = new RelayCommand(() => SelectedPage = HomePage);
    }

    public ExamHomeViewModel Home { get; }
    public OperatorAccountsViewModel Accounts { get; }
    public RelayCommand BackToHomeCommand { get; }

    public int SelectedPage
    {
        get => _selectedPage;
        set { if (SetProperty(ref _selectedPage, value)) OnPropertyChanged(nameof(ShowCurrentExamBar)); }
    }

    /// <summary>进入某场考试（或新建考试）后，在除主页外的页面顶部显示当前考试。</summary>
    public bool ShowCurrentExamBar => CanEditSettings && SelectedPage != HomePage && SelectedPage != ActivationPage;

    public ExamSummary? CurrentExam
    {
        get => _currentExam;
        private set
        {
            if (SetProperty(ref _currentExam, value))
            {
                OnPropertyChanged(nameof(HasCurrentExam));
                OnPropertyChanged(nameof(CanEditSettings));
                OnPropertyChanged(nameof(CurrentExamTitle));
                OnPropertyChanged(nameof(ShowCurrentExamBar));
            }
        }
    }

    public bool HasCurrentExam => CurrentExam is not null;
    public bool CanEditSettings => HasCurrentExam || _isCreatingExam;

    /// <summary>内容区顶部“当前考试”栏显示的名称；新建尚未保存时显示“新建考试”。</summary>
    public string CurrentExamTitle => CurrentExam?.Name ?? (_isCreatingExam ? "新建考试" : string.Empty);

    public void OpenExam(ExamSummary exam)
    {
        SetCreating(false);
        CurrentExam = exam;
        Monitoring.ExamId = exam.ExamId;
        ManifestPreflight.ExamId = exam.ExamId;
        Reports.ExamId = exam.ExamId;
        Reports.ExamName = exam.Name;
        SelectedPage = SettingsPage;
        Exam.OpenCommand(exam.ExamId).Execute(null);
    }

    public void NewExam()
    {
        CurrentExam = null;
        Monitoring.ExamId = string.Empty;
        ManifestPreflight.ExamId = string.Empty;
        Reports.ExamId = string.Empty;
        Reports.ExamName = string.Empty;
        SetCreating(true);
        Exam.ResetForNew();
        SelectedPage = SettingsPage;
    }

    private void OnExamSaved(ExamDetail detail)
    {
        SetCreating(false);
        CurrentExam = detail.Summary;
        Monitoring.ExamId = detail.Summary.ExamId;
        ManifestPreflight.ExamId = detail.Summary.ExamId;
        Reports.ExamId = detail.Summary.ExamId;
        Reports.ExamName = detail.Summary.Name;
        if (Home.RefreshCommand.CanExecute(null)) Home.RefreshCommand.Execute(null);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        SetCreating(false);
        CurrentExam = null;
        ManifestPreflight.ExamId = string.Empty;
        Reports.ExamName = string.Empty;
        SelectedPage = HomePage;
    }

    private void SetCreating(bool value)
    {
        _isCreatingExam = value;
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CurrentExamTitle));
        OnPropertyChanged(nameof(ShowCurrentExamBar));
    }

    public ConnectionViewModel Connection { get; }
    public ExamConfigurationViewModel Exam { get; }
    public ManifestPreflightViewModel ManifestPreflight { get; }
    public RosterViewModel Roster { get; }
    public ActivationRoomViewModel Activation { get; }
    public MonitoringViewModel Monitoring { get; }
    public Exam.Admin.Reporting.ReportPageViewModel Reports { get; }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.IsConnected))
        {
            Monitoring.ConnectionReady = Connection.IsConnected;
            Reports.ConnectionReady = Connection.IsConnected;
            Accounts.NotifyLoginChanged();
            if (Connection.IsConnected && Home.RefreshCommand.CanExecute(null)) Home.RefreshCommand.Execute(null);
            if (Connection.IsConnected && Accounts.RefreshCommand.CanExecute(null)) Accounts.RefreshCommand.Execute(null);
        }
    }

    public void Dispose()
    {
        Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _api.ConfigurationChanged -= OnConfigurationChanged;
        Monitoring.Dispose();
        Reports.Dispose();
        _api.Dispose();
    }
}

/// <summary>
/// 顶部登录条：未登录时填写服务器地址、用户名和口令登录，或注册新的考务账号；
/// 登录后显示当前账号并可退出登录。口令只在本进程内存中短暂保存，登录或注册后即清空。
/// </summary>
public sealed class ConnectionViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _serverAddress = "https://localhost:5001/";
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _status = "请登录考务账号。";
    private bool _isConnected;
    private bool _isRegistering;

    public ConnectionViewModel(AdminApiClient api)
    {
        _api = api;
        ConnectCommand = new AsyncRelayCommand(LoginAsync, onError: error => SetError("登录", error));
        RegisterCommand = new AsyncRelayCommand(RegisterAsync, onError: error => SetError("注册", error));
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => IsConnected);
        ShowRegisterCommand = new RelayCommand(() => IsRegistering = true);
        CancelRegisterCommand = new RelayCommand(() => IsRegistering = false);
    }

    public string ServerAddress { get => _serverAddress; set => SetProperty(ref _serverAddress, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

    /// <summary>由界面的口令框写入；不绑定显示。</summary>
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool IsRegistering
    {
        get => _isRegistering;
        set
        {
            if (SetProperty(ref _isRegistering, value))
            {
                Status = value ? "填写姓名并再次输入口令后提交注册。" : "请登录考务账号。";
                OnPropertyChanged(nameof(ShowRegisterFields));
                OnPropertyChanged(nameof(ToggleRegisterText));
            }
        }
    }

    public bool ShowRegisterFields => IsRegistering && !IsConnected;

    // 登录页绑定用：主操作按钮文字与命令随注册/登录状态切换。
    public string PrimaryActionText => IsRegistering ? "提交注册" : "登录";
    public ICommand PrimaryActionCommand => IsRegistering ? (ICommand)RegisterCommand : ConnectCommand;
    public bool ShowSecondaryAction => IsRegistering;
    public string SecondaryActionText => "取消";
    public ICommand SecondaryActionCommand => CancelRegisterCommand;
    public string ToggleRegisterText => IsRegistering ? "已有账号？返回登录" : "注册新账号";

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsLoggedOut));
                OnPropertyChanged(nameof(ShowRegisterFields));
                OnPropertyChanged(nameof(AccountTitle));
                OnPropertyChanged(nameof(IsAdministrator));
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(PrimaryActionCommand));
                OnPropertyChanged(nameof(ShowSecondaryAction));
                OnPropertyChanged(nameof(ToggleRegisterText));
                LogoutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoggedOut => !IsConnected;
    public bool IsAdministrator => IsConnected && _api.IsAdministrator;

    public string AccountTitle => _api.Account is { } account && IsConnected
        ? $"{account.DisplayName}（{account.Username}）{(account.IsAdministrator ? " · 管理员" : string.Empty)}"
        : string.Empty;

    /// <summary>登录（沿用原“连接服务器”命令名，供界面和测试共用）。</summary>
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand RegisterCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public RelayCommand ShowRegisterCommand { get; }
    public RelayCommand CancelRegisterCommand { get; }

    /// <summary>清空口令框；由界面订阅，在登录或注册结束后执行。</summary>
    public event Action? PasswordsCleared;

    private async Task LoginAsync()
    {
        IsConnected = false;
        Status = "正在登录…";
        _api.Disconnect();
        try
        {
            await _api.LoginAsync(ServerAddress, Username, Password);
            await _api.CheckConnectionAsync();
        }
        finally { ClearPasswords(); }
        IsRegistering = false;
        IsConnected = true;
        Status = $"已登录服务器 {ServerAddress.Trim()}。";
    }

    private async Task RegisterAsync()
    {
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            ClearPasswords();
            Status = "两次输入的口令不一致，请重新输入。";
            return;
        }
        Status = "正在提交注册…";
        Exam.Contracts.Operators.RegisterOperatorResult result;
        try { result = await _api.RegisterAsync(ServerAddress, Username, DisplayName, Password); }
        finally { ClearPasswords(); }
        IsRegistering = false;
        Status = result.Account.Status == Exam.Contracts.Operators.OperatorStatus.Active
            ? "注册成功。这是服务器上的第一个考务账号，已设为管理员，请输入口令登录。"
            : "注册成功，请等待管理员批准后再登录。";
    }

    private async Task LogoutAsync()
    {
        await _api.LogoutAsync();
        IsConnected = false;
        Status = "已退出登录。";
    }

    private void ClearPasswords()
    {
        Password = string.Empty;
        ConfirmPassword = string.Empty;
        PasswordsCleared?.Invoke();
    }

    private void SetError(string action, Exception exception)
    {
        if (!IsConnected) _api.Disconnect();
        Status = $"{action}失败：{FriendlyErrors.Describe(exception)}";
    }
}
