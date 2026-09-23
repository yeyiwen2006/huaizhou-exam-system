using Exam.Admin.Infrastructure;
using Exam.Admin.Services;

namespace Exam.Admin.ViewModels;

public sealed class MainViewModel : IDisposable
{
    private readonly AdminApiClient _api = new();

    public MainViewModel()
    {
        Connection = new ConnectionViewModel(_api);
        Exam = new ExamConfigurationViewModel(_api);
        Roster = new RosterViewModel(new RosterWorkbookService(), new FileDialogService());
        Activation = new ActivationRoomViewModel(_api);
        Monitoring = new MonitoringViewModel(_api);
        Dependencies = new DependencyStatusViewModel();
        ImportRevision = new ImportRevisionViewModel(_api);
        Reports = new ReportsWorkspaceViewModel();
        HighAvailability = new HighAvailabilityWorkspaceViewModel();
    }

    public ConnectionViewModel Connection { get; }
    public ExamConfigurationViewModel Exam { get; }
    public RosterViewModel Roster { get; }
    public ActivationRoomViewModel Activation { get; }
    public MonitoringViewModel Monitoring { get; }
    public DependencyStatusViewModel Dependencies { get; }
    public ImportRevisionViewModel ImportRevision { get; }
    public ReportsWorkspaceViewModel Reports { get; }
    public HighAvailabilityWorkspaceViewModel HighAvailability { get; }

    public void Dispose() => _api.Dispose();
}

public sealed class ConnectionViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _serverAddress = "https://localhost:5001/";
    private string _operatorId = string.Empty;
    private string _operatorToken = string.Empty;
    private string _status = "尚未连接。令牌只保存在当前进程内，不写入配置或日志。";
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
        Status = "服务器已就绪，考务令牌已验证；操作者身份仍由服务器在写请求中核对。";
    }

    private void SetError(Exception exception)
    {
        _api.Disconnect();
        IsConnected = false;
        Status = $"连接失败：{exception.Message}";
    }
}
