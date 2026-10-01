using System.ComponentModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Exams;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 考务端主窗口。主页列出全部考试；进入某场考试后，考试设置、设备监控和成绩统计都作用于这场考试。
/// 导航页序号：0 全部考试，1 考试设置，2 考生名单，3 激活与试场分组，4 设备监控，5 成绩统计。
/// </summary>
public sealed class MainViewModel : BindableBase, IDisposable
{
    public const int HomePage = 0;
    public const int SettingsPage = 1;
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
        Roster = new RosterViewModel(new RosterWorkbookService(), new FileDialogService());
        Activation = new ActivationRoomViewModel(_api);
        Monitoring = new MonitoringViewModel(_api);
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        Reports = new Exam.Admin.Reporting.ReportPageViewModel(_api, new FileDialogService());
        Home = new ExamHomeViewModel(_api);
        Home.ExamOpened += OpenExam;
        Home.NewExamRequested += NewExam;
        Exam.ExamSaved += OnExamSaved;
        _api.ConfigurationChanged += OnConfigurationChanged;
        BackToHomeCommand = new RelayCommand(() => SelectedPage = HomePage);
    }

    public ExamHomeViewModel Home { get; }
    public RelayCommand BackToHomeCommand { get; }

    public int SelectedPage
    {
        get => _selectedPage;
        set { if (SetProperty(ref _selectedPage, value)) OnPropertyChanged(nameof(ShowCurrentExamBar)); }
    }

    /// <summary>进入某场考试（或新建考试）后，在除主页外的页面顶部显示当前考试。</summary>
    public bool ShowCurrentExamBar => CanEditSettings && SelectedPage != HomePage;

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
        Reports.ExamId = exam.ExamId;
        Reports.ExamName = exam.Name;
        SelectedPage = SettingsPage;
        Exam.OpenCommand(exam.ExamId).Execute(null);
    }

    public void NewExam()
    {
        CurrentExam = null;
        Monitoring.ExamId = string.Empty;
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
        Reports.ExamId = detail.Summary.ExamId;
        Reports.ExamName = detail.Summary.Name;
        if (Home.RefreshCommand.CanExecute(null)) Home.RefreshCommand.Execute(null);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        SetCreating(false);
        CurrentExam = null;
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
            if (Connection.IsConnected && Home.RefreshCommand.CanExecute(null)) Home.RefreshCommand.Execute(null);
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

public sealed class ConnectionViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _serverAddress = "https://localhost:5001/";
    private string _operatorId = string.Empty;
    private string _operatorToken = string.Empty;
    private string _status = "尚未连接服务器。";
    private bool _isConnected;

    public ConnectionViewModel(AdminApiClient api)
    {
        _api = api;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, onError: SetError);
    }

    public string ServerAddress
    {
        get => _serverAddress;
        set => SetProperty(ref _serverAddress, value);
    }

    public string OperatorId
    {
        get => _operatorId;
        set => SetProperty(ref _operatorId, value);
    }

    public string OperatorToken
    {
        get => _operatorToken;
        set => SetProperty(ref _operatorToken, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public AsyncRelayCommand ConnectCommand { get; }

    private async Task ConnectAsync()
    {
        IsConnected = false;
        Status = "正在检查服务器…";
        _api.Disconnect();
        _api.Configure(ServerAddress, OperatorToken, OperatorId);
        await _api.CheckConnectionAsync();
        IsConnected = true;
        Status = "已连接服务器。";
    }

    private void SetError(Exception exception)
    {
        _api.Disconnect();
        IsConnected = false;
        Status = $"连接失败：{FriendlyErrors.Describe(exception)}";
    }
}
