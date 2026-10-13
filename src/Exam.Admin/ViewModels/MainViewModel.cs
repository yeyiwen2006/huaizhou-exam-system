using System.ComponentModel;
using System.Windows.Input;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Exams;
using Exam.Infrastructure.Packaging;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 考务端主窗口。主页列出全部考试；进入某场考试后，考试设置、设备监控和成绩统计都作用于这场考试。
/// 导航页序号：0 全部考试，1 考试设置，2 试卷导入与发布，3 考生名单，4 设备监控，5 成绩统计，6 隐藏的考试，7 设备与试场分组，8 考务账号（仅管理员）。
/// </summary>
public sealed class MainViewModel : BindableBase, IDisposable
{
    public const int HomePage = 0;
    public const int SettingsPage = 1;
    public const int ImportPage = 2;
    public const int RosterPage = 3;
    public const int MonitoringPage = 4;
    public const int ReportsPage = 5;
    public const int HiddenExamsPage = 6;
    public const int ActivationPage = 7; // 设备与试场分组：不与具体考试挂钩，不显示当前考试条。
    public const int AccountsPage = 8;
    private readonly AdminApiClient _api;
    private ExamSummary? _currentExam;
    private bool _isCreatingExam;
    private int _selectedPage;

    public MainViewModel() : this(new AdminApiClient())
    {
    }

    /// <summary>使用指定的服务器客户端（测试用合成服务器）；客户端随本对象一同释放。</summary>
    public MainViewModel(AdminApiClient api, IExamImportService? importService = null,
        IExamArchiveService? archiveService = null, IExamArchiveDirectoryDialog? archiveDirectories = null)
    {
        _api = api;
        Connection = new ConnectionViewModel(_api);
        Exam = new ExamConfigurationViewModel(_api);
        Roster = new RosterViewModel(new RosterWorkbookService(), new FileDialogService(), _api);
        Activation = new ActivationRoomViewModel(_api);
        Monitoring = new MonitoringViewModel(_api);
        Import = new ExamImportViewModel(_api, service: importService);
        GradingRevision = new GradingRevisionViewModel(_api);
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        Reports = new Exam.Admin.Reporting.ReportPageViewModel(_api, new FileDialogService());
        Home = new ExamHomeViewModel(_api, archiveService, archiveDirectories);
        Accounts = new OperatorAccountsViewModel(_api);
        Home.ExamOpened += OpenExam;
        Home.NewExamRequested += NewExam;
        Home.ExamReplacedFromArchive += OnExamReplacedFromArchive;
        Exam.ExamSaved += OnExamSaved;
        Import.PaperPublicationChanged += OnPaperPublicationChanged;
        _api.ConfigurationChanged += OnConfigurationChanged;
        BackToHomeCommand = new RelayCommand(() => SelectedPage = HomePage);
        ShowHiddenExamsCommand = new RelayCommand(() => SelectedPage = HiddenExamsPage);
        ShowImportProgressCommand = new RelayCommand(() => SelectedPage = ImportPage);
    }

    public ExamHomeViewModel Home { get; }
    public OperatorAccountsViewModel Accounts { get; }
    public RelayCommand BackToHomeCommand { get; }
    public RelayCommand ShowHiddenExamsCommand { get; }
    public RelayCommand ShowImportProgressCommand { get; }

    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value)) return;
            OnPropertyChanged(nameof(ShowCurrentExamBar));
            if (value is HomePage or HiddenExamsPage && Home.RefreshCommand.CanExecute(null))
                Home.RefreshCommand.Execute(null);
            if (value == ImportPage && Import.RefreshPublicationCommand.CanExecute(null))
                Import.RefreshPublicationCommand.Execute(null);
        }
    }

    /// <summary>进入某场考试（或新建考试）后，在除主页外的页面顶部显示当前考试。</summary>
    public bool ShowCurrentExamBar => CanEditSettings && SelectedPage != HomePage && SelectedPage != HiddenExamsPage && SelectedPage != ActivationPage;

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
        _ = Roster.OpenAsync(exam.ExamId);
        Monitoring.ExamId = exam.ExamId;
        Import.SetCurrentExam(exam);
        GradingRevision.ExamId = exam.ExamId;
        Reports.ExamId = exam.ExamId;
        Reports.ExamName = exam.Name;
        SelectedPage = SettingsPage;
        Exam.OpenCommand(exam.ExamId).Execute(null);
    }

    public void NewExam()
    {
        CurrentExam = null;
        Roster.Reset();
        Monitoring.ExamId = string.Empty;
        Import.SetCurrentExam(null);
        GradingRevision.ExamId = string.Empty;
        Reports.ExamId = string.Empty;
        Reports.ExamName = string.Empty;
        SetCreating(true);
        Exam.ResetForNew();
        SelectedPage = SettingsPage;
    }

    private void OnExamReplacedFromArchive(string examId)
    {
        GradingRevision.InvalidateForArchiveReplacement(examId);
        if (CurrentExam?.ExamId != examId) return;
        SetCreating(false); CurrentExam = null;
        Exam.InvalidateForArchiveReplacement();
        Roster.Reset(); Monitoring.ExamId = string.Empty;
        Import.SetCurrentExam(null); GradingRevision.ExamId = string.Empty;
        Reports.ExamId = string.Empty; Reports.ExamName = string.Empty;
        SelectedPage = HomePage;
    }

    private void OnExamSaved(ExamDetail detail)
    {
        SetCreating(false);
        CurrentExam = detail.Summary;
        if (!StringComparer.Ordinal.Equals(Roster.ExamId, detail.Summary.ExamId))
            _ = Roster.OpenAsync(detail.Summary.ExamId);
        Monitoring.ExamId = detail.Summary.ExamId;
        Import.SetCurrentExam(detail.Summary);
        GradingRevision.ExamId = detail.Summary.ExamId;
        Reports.ExamId = detail.Summary.ExamId;
        Reports.ExamName = detail.Summary.Name;
        if (Home.RefreshCommand.CanExecute(null)) Home.RefreshCommand.Execute(null);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        BusinessSessionChanged?.Invoke();
        SetCreating(false);
        CurrentExam = null;
        Roster.Reset();
        Import.SetCurrentExam(null);
        GradingRevision.ExamId = string.Empty;
        // 收齐提醒不依赖监测页是否打开：更换服务器时必须清掉考试上下文，
        // 否则会拿上一台服务器的旧考试继续轮询并弹出无关的收齐提醒。
        Monitoring.ExamId = string.Empty;
        Reports.ExamName = string.Empty;
        SelectedPage = HomePage;
    }

    private void OnPaperPublicationChanged(string examId, long? paperVersion)
    {
        if (CurrentExam?.ExamId != examId) return;
        CurrentExam = CurrentExam with { PaperVersion = paperVersion };
        Home.UpdatePaperVersion(examId, paperVersion);
    }

    private void SetCreating(bool value)
    {
        _isCreatingExam = value;
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CurrentExamTitle));
        OnPropertyChanged(nameof(ShowCurrentExamBar));
    }

    public ConnectionViewModel Connection { get; }
    public event Action? BusinessSessionChanged;
    public AdminSessionSnapshot? CaptureBusinessSession() => Connection.IsConnected ? _api.CaptureSession() : null;
    public ExamConfigurationViewModel Exam { get; }
    public RosterViewModel Roster { get; }
    public ActivationRoomViewModel Activation { get; }
    public MonitoringViewModel Monitoring { get; }
    public ExamImportViewModel Import { get; }

    /// <summary>只改答案与计分规则的入口（不重新编译、不组包）。</summary>
    public GradingRevisionViewModel GradingRevision { get; }
    public Exam.Admin.Reporting.ReportPageViewModel Reports { get; }

    /// <summary>
    /// 把内部考试标识换成考务可读的考试名称；宿主列表里查不到时返回 null，
    /// 由调用方改用中性说法，绝不把内部标识显示给考务。
    /// </summary>
    public string? DescribeExam(string examId)
    {
        if (string.IsNullOrWhiteSpace(examId)) return null;
        if (CurrentExam is { } current && StringComparer.Ordinal.Equals(current.ExamId, examId)) return current.Name;
        return Home.Exams.Concat(Home.HiddenExams)
            .FirstOrDefault(card => StringComparer.Ordinal.Equals(card.Exam.ExamId, examId))?.Exam.Name;
    }

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
        Import.PaperPublicationChanged -= OnPaperPublicationChanged;
        Home.ExamReplacedFromArchive -= OnExamReplacedFromArchive;
        Home.Dispose();
        Monitoring.Dispose();
        Import.Dispose();
        GradingRevision.Dispose();
        Roster.Dispose();
        Activation.Dispose();
        Reports.Dispose();
        _api.Dispose();
    }
}

/// <summary>
/// 登录页：填写服务器地址、用户名和口令，默认使用标准 HTTPS 端口；也可注册新的考务账号。
/// 登录后显示当前账号并可退出登录。口令只在本进程内存中短暂保存，登录或注册后即清空。
/// </summary>
public sealed class ConnectionViewModel : BindableBase
{
    private const string ServerAddressGuidance = "请填写服务器 IP、域名或 HTTPS 根地址，例如 exam.example.com；默认使用 HTTPS 443 端口。";
    private readonly AdminApiClient _api;
    private string _serverAddress = string.Empty;
    private string _username = string.Empty;
    private string _displayName = string.Empty;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _status = "请登录考务账号。";
    private bool _isConnected;
    private bool _isRegistering;
    private bool _showRegistrationSuccess;

    public ConnectionViewModel(AdminApiClient api, string? configuredServerUrl = null)
    {
        _api = api;
        var configured = configuredServerUrl ?? Environment.GetEnvironmentVariable("EXAM_ADMIN_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(configured)) _serverAddress = configured.Trim();
        ConnectCommand = new AsyncRelayCommand(LoginAsync, onError: error => SetError("登录", error));
        RegisterCommand = new AsyncRelayCommand(RegisterAsync, onError: error => SetError("注册", error));
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => IsConnected);
        ShowRegisterCommand = new RelayCommand(() => IsRegistering = true);
        CancelRegisterCommand = new RelayCommand(() => IsRegistering = false);
        AcknowledgeRegistrationCommand = new RelayCommand(() => ShowRegistrationSuccess = false);
    }

    public string ServerAddress { get => _serverAddress; set => SetProperty(ref _serverAddress, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

    /// <summary>由界面的口令框写入；不绑定显示。</summary>
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string ConfirmPassword { get => _confirmPassword; set => SetProperty(ref _confirmPassword, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool ShowRegistrationSuccess { get => _showRegistrationSuccess; private set => SetProperty(ref _showRegistrationSuccess, value); }

    public bool IsRegistering
    {
        get => _isRegistering;
        set
        {
            if (SetProperty(ref _isRegistering, value))
            {
                Status = value ? "填写姓名并再次输入密码后提交注册。" : "请登录考务账号。";
                OnPropertyChanged(nameof(ShowRegisterFields));
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(PrimaryActionCommand));
                OnPropertyChanged(nameof(ShowSecondaryAction));
                OnPropertyChanged(nameof(ToggleRegisterText));
                OnPropertyChanged(nameof(ToggleRegisterCommand));
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
    public ICommand ToggleRegisterCommand => IsRegistering ? CancelRegisterCommand : ShowRegisterCommand;

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
    public RelayCommand AcknowledgeRegistrationCommand { get; }

    /// <summary>清空口令框；由界面订阅，在登录或注册结束后执行。</summary>
    public event Action? PasswordsCleared;

    private async Task LoginAsync()
    {
        IsConnected = false;
        Status = "正在登录…";
        _api.Disconnect();
        try
        {
            await _api.LoginAsync(BuildServerAddress(), Username, Password);
            await _api.CheckConnectionAsync();
        }
        finally { ClearPasswords(); }
        IsRegistering = false;
        IsConnected = true;
        Status = $"已登录服务器 {BuildServerAddress()}。";
    }

    private async Task RegisterAsync()
    {
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            ClearPasswords();
            Status = "两次输入的密码不一致，请重新输入。";
            return;
        }
        Status = "正在提交注册…";
        Exam.Contracts.Operators.RegisterOperatorResult result;
        try { result = await _api.RegisterAsync(BuildServerAddress(), Username, DisplayName, Password); }
        finally { ClearPasswords(); }
        IsRegistering = false;
        Status = result.Account.Status == Exam.Contracts.Operators.OperatorStatus.Active
            ? "注册成功。这是服务器上的第一个考务账号，已设为管理员，请输入密码登录。"
            : "注册成功，请等待管理员批准后再登录。";
        ShowRegistrationSuccess = result.Account.Status == Exam.Contracts.Operators.OperatorStatus.Pending;
    }

    private async Task LogoutAsync()
    {
        await _api.LogoutAsync();
        IsConnected = false;
        Status = "已退出登录。";
    }

    private string BuildServerAddress()
    {
        var address = ServerAddress.Trim();
        if (Uri.CheckHostName(address) != UriHostNameType.Unknown)
            return new UriBuilder(Uri.UriSchemeHttps, address).Uri.AbsoluteUri;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Host.Length == 0 || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException(ServerAddressGuidance, nameof(ServerAddress));
        return endpoint.AbsoluteUri;
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
        var description = exception is ArgumentException { ParamName: nameof(ServerAddress) }
            ? ServerAddressGuidance : FriendlyErrors.Describe(exception);
        Status = $"{action}失败：{description}";
    }
}
