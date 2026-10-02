using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Exam.Admin.Infrastructure;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed record ManagementConfirmation(string Title, string Body, bool Dangerous);
public sealed record ManagementCheckRow(string Name, string Result, string WriteGate);
public sealed record ManagementLogRow(string Time, string Source, string Kind, string Phase, string Code);

public sealed partial class ServiceManagementViewModel : BindableBase, IDisposable
{
    private readonly Func<ManagementTarget, IManagementConnection> _factory;
    private readonly IPendingManagementStore _store;
    private readonly ManagementEndpointSettings? _settings;
    private readonly TimeProvider _time;
    private IManagementConnection? _connection;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reads;
    private CancellationTokenSource? _polling;
    private long _generation;
    private bool _disposed, _busy, _readBusy, _active, _signedIn;
    private PendingManagementRequest? _pending;
    private ManagementOperation? _operation;
    private string _address = string.Empty, _server = string.Empty, _node = string.Empty, _username = string.Empty;
    private string _message = "业务停止时仍可登录本机管理。请填写部署提供的独立管理地址与节点身份。";
    private string _process = "尚未查询", _readiness = "未核实", _epoch = "当前代次未核实";
    private string _bind = string.Empty, _port = string.Empty, _data = string.Empty;
    private bool _ha;
    private long? _configurationVersion;
    private string _destination = string.Empty, _backupId = string.Empty, _source = string.Empty;
    private string _operationText = "暂无操作", _restoreText = "隔离暂存：未核实；恢复核对：未核实；业务就绪：未核实";
    private bool _canResume;
    private bool _missingOriginal;

    public ServiceManagementViewModel(Func<ManagementTarget, IManagementConnection> factory,
        IPendingManagementStore store, ManagementEndpointSettings? settings = null, IProtectionWarningStore? protectionMemory = null,
        TimeProvider? time = null)
    {
        _factory = factory; _store = store; _settings = settings; _protectionMemory = protectionMemory; _time = time ?? TimeProvider.System;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsSignedIn && !_readBusy, OnError);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => IsSignedIn, OnError);
        StartCommand = OperationCommand(ManagementOperationKind.Start);
        StopCommand = OperationCommand(ManagementOperationKind.Stop);
        ChecksCommand = OperationCommand(ManagementOperationKind.Checks);
        ConfigureCommand = OperationCommand(ManagementOperationKind.Configure);
        BackupCommand = OperationCommand(ManagementOperationKind.Backup);
        RestoreCommand = OperationCommand(ManagementOperationKind.Restore);
        TakeoverCommand = OperationCommand(ManagementOperationKind.Takeover);
        EmergencyCommand = OperationCommand(ManagementOperationKind.Emergency);
        RebuildCommand = OperationCommand(ManagementOperationKind.Rebuild);
        ReprotectCommand = OperationCommand(ManagementOperationKind.Reprotect);
        QueryCommand = new AsyncRelayCommand(QueryAsync, () => IsSignedIn && _pending is not null && !_readBusy, OnError);
        ResumeRestoreCommand = new AsyncRelayCommand(ResumeRestoreAsync, () => IsSignedIn && CanResumeRestore && !IsBusy, OnError);
        RetryOriginalCommand = new AsyncRelayCommand(RetryOriginalAsync, () => IsSignedIn && _pending is not null && _missingOriginal && !IsBusy && !_readBusy, OnError);
        CancelWaitCommand = new RelayCommand(CancelWaiting, () => IsBusy || _readBusy);
        LoadSettings();
        LoadProtectionWarning();
    }

    public static ServiceManagementViewModel CreateDefault()
    {
        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exam.Admin", "Management");
        return new(target => new ManagementConnection(target), new ProtectedPendingManagementStore(System.IO.Path.Combine(directory, "Pending")),
            new ManagementEndpointSettings(System.IO.Path.Combine(directory, "endpoint.json")),
            new ProtectedProtectionWarningStore(System.IO.Path.Combine(directory, "Protection")));
    }

    public Func<ManagementConfirmation, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public string ServerId { get => _server; set => SetProperty(ref _server, value); }
    public string NodeId { get => _node; set => SetProperty(ref _node, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string ProcessText { get => _process; private set => SetProperty(ref _process, value); }
    public string ReadinessText { get => _readiness; private set => SetProperty(ref _readiness, value); }
    public string EpochText { get => _epoch; private set => SetProperty(ref _epoch, value); }
    public string BindAddress { get => _bind; set => SetProperty(ref _bind, value); }
    public string HttpsPort { get => _port; set => SetProperty(ref _port, value); }
    public string DataDirectory { get => _data; set => SetProperty(ref _data, value); }
    public bool HighAvailabilityEnabled { get => _ha; set => SetProperty(ref _ha, value); }
    public string ConfigurationVersionText => _configurationVersion is { } v ? $"读取版本：{v}；保存按此版本核对" : "配置版本未核实，不能保存";
    public string BackupDestination { get => _destination; set => SetProperty(ref _destination, value); }
    public string RestoreBackupId { get => _backupId; set => SetProperty(ref _backupId, value); }
    public string RestoreSource { get => _source; set => SetProperty(ref _source, value); }
    public string OperationText { get => _operationText; private set => SetProperty(ref _operationText, value); }
    public string RestoreText { get => _restoreText; private set => SetProperty(ref _restoreText, value); }
    public string PendingText => _pending is null ? "无待核对请求" : $"原请求：{_pending.RequestId} · {KindText(_pending.Kind)} · 待查询原结果";
    public PendingManagementRequest? Pending => _pending;
    public ManagementOperation? Operation => _operation;
    public bool IsSignedIn { get => _signedIn; private set { if (SetProperty(ref _signedIn, value)) { OnPropertyChanged(nameof(IsSignedOut)); UpdateCommands(); } } }
    public bool IsSignedOut => !IsSignedIn;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) UpdateCommands(); } }
    public bool CanResumeRestore { get => _canResume; private set { if (SetProperty(ref _canResume, value)) UpdateCommands(); } }
    public string TargetText => _connection is null ? "本机服务管理" : $"{_connection.Target.ServerId} / {_connection.Target.NodeId} · 独立管理会话";
    public ObservableCollection<ManagementCheckRow> Checks { get; } = [];
    public ObservableCollection<ManagementLogRow> Logs { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ChecksCommand { get; }
    public AsyncRelayCommand ConfigureCommand { get; }
    public AsyncRelayCommand BackupCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand QueryCommand { get; }
    public AsyncRelayCommand ResumeRestoreCommand { get; }
    public AsyncRelayCommand RetryOriginalCommand { get; }
    public RelayCommand CancelWaitCommand { get; }
    private AsyncRelayCommand OperationCommand(ManagementOperationKind kind) =>
        new(() => RequestAsync(kind), () => IsSignedIn && !IsBusy && _pending is null && CanIssueHighAvailability(kind), OnError);

    public void Activate() { if (_disposed) return; _active = true; StartPolling(); }
    public void Deactivate()
    {
        if (!_active) return;
        _active = false; _generation++; _polling?.Cancel(); CancelWaiting();
        MarkProtectionUnknown();
        _reads = null; _readBusy = false; IsBusy = false; UpdateCommands();
    }
    public bool IsPageActive => _active;

    public async Task LoginAsync(string password)
    {
        if (_disposed || IsBusy) return;
        ResetSession();
        var generation = _generation;
        var target = new ManagementTarget(Address.Trim(), ServerId.Trim(), NodeId.Trim());
        target.Validate();
        var connection = _factory(target);
        _connection = connection;
        IsBusy = true;
        try
        {
            await connection.LoginAsync(Username, password, _lifetime.Token);
            if (!Current(connection, generation)) return;
            // Read before allowing writes. A damaged pending file blocks this node instead of silently losing its request.
            _pending = _store.Load(target);
            LoadProtectionWarning(target);
            _settings?.Save(target);
            IsSignedIn = true;
            Message = "已建立独立管理会话。";
            NotifyPending(); OnPropertyChanged(nameof(TargetText));
        }
        catch { if (Current(connection, generation)) ResetSession(); throw; }
        finally { if (generation == _generation) IsBusy = false; }
        if (IsSignedIn) await RefreshAsync();
        StartPolling();
    }

    public async Task LogoutAsync()
    {
        var old = _connection;
        ResetSession(dispose: false);
        Message = "本机管理会话已清除。";
        if (old is null) return;
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await old.LogoutAsync(deadline.Token); }
        catch { Message = "本机会话已清除；服务端撤销未确认，管理宿主重启也会使旧会话失效。"; }
        finally { old.Dispose(); }
    }

    public async Task RefreshAsync()
    {
        if (!IsSignedIn || _connection is null || _readBusy) return;
        var connection = _connection; var generation = _generation;
        using var reads = BeginRead();
        try
        {
            var status = await connection.StatusAsync(reads.Token);
            if (reads.IsCancellationRequested) return;
            if (!Accept(connection, generation, status.IsUnauthorized)) return;
            if (status.Envelope.Error is not null) { MarkUnknown(); Message = SafeError(status.StatusCode); return; }
            var value = status.Envelope.Result ?? throw new System.IO.InvalidDataException();
            if (!Enum.IsDefined(value.ProcessStatus) || !Enum.IsDefined(value.Readiness) || value.Checks is null)
                throw new System.IO.InvalidDataException();
            ApplyStatus(value, status.Envelope.EpochVerified, status.Envelope.ServerEpoch);
            var availability = await connection.HighAvailabilityAsync(reads.Token);
            if (reads.IsCancellationRequested || !Accept(connection, generation, availability.IsUnauthorized)) return;
            if (availability.Envelope.Error is null && availability.Envelope.Result is { } observed) ApplyHighAvailability(observed);
            else MarkProtectionUnknown();
            // Preserve a user's form, including after a conflict or pending write. Explicit reload is separate.
            if (_configurationVersion is null && _pending?.Kind != ManagementOperationKind.Configure && value.Configuration is not null)
                LoadConfiguration(value.Configuration);
            var audit = await connection.AuditAsync(50, reads.Token);
            if (reads.IsCancellationRequested) return;
            if (!Accept(connection, generation, audit.IsUnauthorized)) return;
            var logs = await connection.LogsAsync(50, reads.Token);
            if (reads.IsCancellationRequested) return;
            if (!Accept(connection, generation, logs.IsUnauthorized)) return;
            Logs.Clear();
            if (audit.Envelope.Result is { } entries && audit.Envelope.Error is null)
            {
                if (entries.Count > 50 || entries.Any(e => !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Phase) || e.UpdatedAt == default))
                    throw new System.IO.InvalidDataException("限定操作日志无效。");
                foreach (var entry in entries) Logs.Add(new(entry.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"), "操作", KindText(entry.Kind), PhaseText(entry.Phase), entry.ErrorCode is null ? "—" : "操作未完成"));
            }
            if (logs.Envelope.Result is { } runtime && logs.Envelope.Error is null)
            {
                if (runtime.Count > 50 || runtime.Any(e => e.Source is not ("business" or "management") ||
                    e.Level is not ("Warning" or "Error" or "Critical") || e.Timestamp == default || e.Category is null ||
                    e.Category.Length is < 1 or > 200 || !e.Category.StartsWith("Exam.", StringComparison.Ordinal) ||
                    e.Category.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '+'))))
                    throw new System.IO.InvalidDataException("限定运行日志无效。");
                foreach (var entry in runtime) Logs.Add(new(entry.Timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"),
                    entry.Source == "business" ? "业务服务" : "本机管理", "运行记录",
                    entry.Level switch { "Warning" => "警告", "Error" => "错误", _ => "严重错误" }, $"事件编号：{entry.EventId.ToString(CultureInfo.InvariantCulture)}"));
            }
            Message = audit.Envelope.Error is null && logs.Envelope.Error is null ? "本机状态和限定日志已刷新。" : "状态已刷新；部分限定日志暂不可用。";
        }
        catch (OperationCanceledException) { if (Current(connection, generation)) { MarkUnknown(); Message = "已停止本次查询；当前状态需重新核实。"; } }
        catch { if (Current(connection, generation)) MarkUnknown(); throw; }
        finally { EndRead(reads); }
    }

    public async Task ReloadConfigurationAsync()
    {
        if (!IsSignedIn || _connection is null || IsBusy || _pending is not null) return;
        if (!await ConfirmAsync(new("重新载入配置", "将覆盖尚未保存的表单。确认读取服务器当前配置和版本？", false))) return;
        _configurationVersion = null;
        await RefreshAsync();
    }

    public async Task RequestAsync(ManagementOperationKind kind)
    {
        if (!IsSignedIn || _connection?.Actor is not { } actor || IsBusy || _pending is not null) return;
        var connection = _connection; var generation = _generation;
        var id = Guid.NewGuid().ToString("N");
        var body = BuildBody(kind, connection.Target, actor, id);
        var request = PendingManagementRequest.Create(connection.Target, actor, kind, id, body);
        IsBusy = true; // One gate covers all writes, including a confirmation dialog.
        try
        {
            if (!await ConfirmAsync(new("确认" + KindText(kind), ConfirmationBody(kind, request), kind is not ManagementOperationKind.Checks))) return;
            if (!Current(connection, generation) || !IsSignedIn) return;
            _store.Save(request); // Failure here must prevent network side effects.
            _pending = request; NotifyPending();
            using var wait = BeginRead();
            try { var reply = await connection.ExecuteAsync(kind, body, wait.Token); if (!wait.IsCancellationRequested) Apply(reply, request, connection, generation); }
            catch (Exception) { if (Current(connection, generation)) Message = "操作结果尚未确认。原请求已保留，请查询原结果；取消等待不会撤销服务器操作。"; }
            finally { EndRead(wait); }
        }
        finally { if (Current(connection, generation)) IsBusy = false; }
        StartPolling();
    }

    public async Task QueryAsync()
    {
        if (!IsSignedIn || _connection is null || _pending is null || _readBusy) return;
        var connection = _connection; var generation = _generation; var pending = _pending;
        if (connection.Actor != pending.Actor) { Message = "该请求属于原管理账号。请用原账号登录后查询。"; return; }
        using var wait = BeginRead();
        try { var reply = await connection.FindAsync(pending.RequestId, wait.Token); if (!wait.IsCancellationRequested) Apply(reply, pending, connection, generation); }
        catch (Exception) { if (Current(connection, generation)) Message = "查询未确认，保留原请求。请恢复连接后再次查询。"; }
        finally { EndRead(wait); }
    }

    public async Task ResumeRestoreAsync()
    {
        if (!IsSignedIn || _connection is null || _pending is not { Kind: ManagementOperationKind.Restore } request ||
            !CanResumeRestore || IsBusy || _connection.Actor != request.Actor) return;
        var connection = _connection; var generation = _generation;
        IsBusy = true;
        try
        {
            if (!await ConfirmAsync(new("续办原恢复请求", "仅在管理员已补齐最新持久确认和恢复材料后续办。沿用原备份、原路径与原请求号，不创建新恢复。", true)) ||
                !Current(connection, generation)) return;
            using var wait = BeginRead();
            try { var reply = await connection.ExecuteAsync(request.Kind, request.Body, wait.Token); if (!wait.IsCancellationRequested) Apply(reply, request, connection, generation); }
            catch { if (Current(connection, generation)) Message = "恢复续办结果未确认，继续查询原请求。"; }
            finally { EndRead(wait); }
        }
        finally { if (Current(connection, generation)) IsBusy = false; }
    }

    public async Task RetryOriginalAsync()
    {
        if (!IsSignedIn || _connection is null || _pending is not { } request || !_missingOriginal || IsBusy || _readBusy ||
            _connection.Actor != request.Actor) return;
        var connection = _connection; var generation = _generation;
        IsBusy = true;
        try
        {
            if (!await ConfirmAsync(new("按原内容重试", "尚未找到原操作不证明它未执行。此次只重新发送完全相同的请求号与原内容，由服务器执行幂等核对。\n" +
                ConfirmationBody(request.Kind, request), true)) || !Current(connection, generation)) return;
            using var wait = BeginRead();
            try { var reply = await connection.ExecuteAsync(request.Kind, request.Body, wait.Token); if (!wait.IsCancellationRequested) Apply(reply, request, connection, generation); }
            catch { if (Current(connection, generation)) Message = "原请求重试结果仍未确认，继续查询原请求。"; }
            finally { EndRead(wait); }
        }
        finally { if (Current(connection, generation)) IsBusy = false; }
        StartPolling();
    }

    private void Apply(ManagementReply<ManagementOperation> reply, PendingManagementRequest request, IManagementConnection connection, long generation)
    {
        if (!Accept(connection, generation, reply.IsUnauthorized)) return;
        if (reply.Envelope.Result is not { } operation)
        { _missingOriginal = reply.StatusCode == HttpStatusCode.NotFound; UpdateCommands(); Message = SafeError(reply.StatusCode) + "；原请求保留，请查询原结果。"; return; }
        if (operation.ActorId != request.Actor || operation.RequestId != request.RequestId || operation.Kind != request.Kind ||
            operation.ServerId != request.Target.ServerId || operation.NodeId != request.Target.NodeId || operation.ContentHash != request.BodyHash)
            throw new System.IO.InvalidDataException("操作身份与原请求不一致。");
        if (!Enum.IsDefined(operation.Phase) || operation.Phase == ManagementOperationPhase.Failed && operation.Error is null ||
            operation.Phase is ManagementOperationPhase.Accepted or ManagementOperationPhase.Completed or ManagementOperationPhase.Staged && operation.Error is not null)
            throw new System.IO.InvalidDataException("操作阶段证据无效。");
        _missingOriginal = false;
        _operation = operation;
        OnPropertyChanged(nameof(Operation));
        OperationText = $"{KindText(operation.Kind)}：{PhaseText(operation.Phase)} · {operation.RequestId}";
        CanResumeRestore = operation.Kind == ManagementOperationKind.Restore &&
            (operation.Phase == ManagementOperationPhase.Staged || operation.Phase == ManagementOperationPhase.NeedsVerification &&
                operation.Restore is { IsolationStaged: true } && operation.RecoveryActivation is null &&
                operation.Checks?.Any(c => c.Name == "recovery-confirmed-state" && !c.Passed && c.BlocksWrites) == true);
        if (operation.Status is { } status) ApplyStatus(status, reply.Envelope.EpochVerified, reply.Envelope.ServerEpoch);
        if (operation.HighAvailability is { } availability) ApplyHighAvailability(availability);
        if (operation.Checks is not null) SetChecks(operation.Checks);
        if (operation.Restore is { } restore) RestoreText = $"隔离暂存：{YesNo(restore.IsolationStaged)}；恢复核对：{YesNo(restore.RecoveryVerified)}；业务就绪：{YesNo(restore.BusinessReady)}";
        Message = operation.Phase switch
        {
            ManagementOperationPhase.Completed when operation.Kind == ManagementOperationKind.Checks && operation.Checks!.Any(c => !c.Passed && c.BlocksWrites) => "自检已完成，存在阻止业务写入的失败项。",
            ManagementOperationPhase.Completed when operation.Backup is { } => "备份已完成：数据库、受保护配置和版本资源均已包含。",
            ManagementOperationPhase.Completed => "操作已完成，并已核对后端完成证据。",
            ManagementOperationPhase.Staged => "恢复仅完成隔离暂存，业务尚未就绪。",
            ManagementOperationPhase.NeedsVerification => "操作仍待核对；不能创建新请求代替原请求。",
            ManagementOperationPhase.Failed => SafeError(reply.StatusCode),
            _ => "操作已受理，继续查询原结果。"
        };
        if (operation.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed)
        {
            _polling?.Cancel();
            if (operation.Configuration is not null && operation.Phase == ManagementOperationPhase.Completed) LoadConfiguration(operation.Configuration);
            _store.Clear(request); _pending = null; CanResumeRestore = false; NotifyPending();
        }
    }

    private string BuildBody(ManagementOperationKind kind, ManagementTarget target, string actor, string id)
    {
        object body = kind switch
        {
            ManagementOperationKind.Configure => new ManagementConfigurationRequest(id, target.ServerId, target.NodeId,
                _configurationVersion ?? throw new ArgumentException("先读取当前配置版本。"), BuildConfiguration(target)),
            ManagementOperationKind.Backup => new BackupRequest(id, RequirePath(BackupDestination), actor),
            ManagementOperationKind.Restore => new RestoreRequest(id,
                string.IsNullOrWhiteSpace(RestoreBackupId) ? throw new ArgumentException("请输入备份标识。") : RestoreBackupId.Trim(), RequirePath(RestoreSource), actor),
            ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect =>
                BuildHighAvailabilityRequest(kind, target, actor, id),
            _ => new ManagementServiceRequest(id, target.ServerId, target.NodeId)
        };
        return JsonSerializer.Serialize(body, JsonDefaults.Create());
    }
    private ServiceConfiguration BuildConfiguration(ManagementTarget target)
    {
        if (!int.TryParse(HttpsPort, out var port) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(BindAddress))
            throw new ArgumentException("请输入有效的业务监听地址和端口。");
        return new(target.ServerId, target.NodeId, BindAddress.Trim(), port, RequirePath(DataDirectory), HighAvailabilityEnabled, _configurationVersion!.Value);
    }
    private static string RequirePath(string path) => !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathFullyQualified(path.Trim())
        ? path.Trim() : throw new ArgumentException("请输入本机绝对目录路径。");
    private static string ConfirmationBody(ManagementOperationKind kind, PendingManagementRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        var fields = document.RootElement;
        var details = kind switch
        {
            ManagementOperationKind.Configure => $"\n预期版本：{fields.GetProperty("expectedVersion")}\n目标配置：{fields.GetProperty("configuration").GetProperty("bindAddress")}:{fields.GetProperty("configuration").GetProperty("httpsPort")}\n数据目录：{fields.GetProperty("configuration").GetProperty("dataDirectory")}",
            ManagementOperationKind.Backup => $"\n备份目录：{fields.GetProperty("destinationDirectory")}",
            ManagementOperationKind.Restore => $"\n备份：{fields.GetProperty("backupId")}\n来源：{fields.GetProperty("sourceDirectory")}",
            ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect =>
                $"\n目标节点：{fields.GetProperty("targetNodeId")}\n预期代次：{fields.GetProperty("expectedEpoch")}；状态版本：{fields.GetProperty("expectedVersion")}\n原因：{fields.GetProperty("reason")}" +
                (kind == ManagementOperationKind.Takeover ? $"\n原主机：{fields.GetProperty("isolation").GetProperty("isolatedNodeId")}；断电确认：{YesNo(fields.GetProperty("isolation").GetProperty("hostPoweredOff").GetBoolean())}；业务网络隔离确认：{YesNo(fields.GetProperty("isolation").GetProperty("businessNetworkIsolated").GetBoolean())}" : ""),
            _ => string.Empty
        };
        return $"目标：{request.Target.ServerId} / {request.Target.NodeId}\n{KindText(kind)}使用稳定请求号。" + (kind switch
        {
            ManagementOperationKind.Stop => "停止会中断业务服务。",
            ManagementOperationKind.Configure => "后端核对已停止状态与当前配置版本；冲突时不覆盖。",
            ManagementOperationKind.Backup => "须先停止业务；备份结果必须包含三项完整性证据。",
            ManagementOperationKind.Restore => "须先停止业务。先隔离暂存，再核对最新持久确认；暂存不表示业务恢复。",
            ManagementOperationKind.Takeover => "仅在原主机断电或业务网络隔离后提升本机备库。提升后进入单机应急；心跳丢失不能作为隔离证据。",
            ManagementOperationKind.Emergency => "停止本机业务并切换为单机持久确认。完成后请自检并启动业务；应急期间保护降低，请尽快恢复备用节点。",
            ManagementOperationKind.Rebuild => "停止本机服务，从当前主机重新建立备库和资源。旧数据保留在隔离目录，禁止自动回写到新主机。",
            ManagementOperationKind.Reprotect => "只有实际复制、材料和双侧最新持久确认全部核实后才恢复同步保护；失败保持待核对。",
            _ => "操作结果以服务器实际检查为准。"
        }) + details;
    }
    private void LoadConfiguration(ServiceConfiguration value)
    {
        if (_connection is null || value.ServerId != _connection.Target.ServerId || value.NodeId != _connection.Target.NodeId || value.Version <= 0)
            throw new System.IO.InvalidDataException("配置身份或版本无效。");
        BindAddress = value.BindAddress; HttpsPort = value.HttpsPort.ToString(CultureInfo.InvariantCulture); DataDirectory = value.DataDirectory;
        HighAvailabilityEnabled = value.HighAvailabilityEnabled; _configurationVersion = value.Version; OnPropertyChanged(nameof(ConfigurationVersionText));
    }
    private void ApplyStatus(ManagementStatus value, bool verified, long? epoch)
    {
        if (!Enum.IsDefined(value.ProcessStatus) || !Enum.IsDefined(value.Readiness) || value.Checks is null ||
            value.Readiness == BusinessReadiness.Ready && (value.ProcessStatus != BusinessServiceStatus.Running ||
                value.Checks.Count == 0 || value.Checks.Any(c => c.BlocksWrites && !c.Passed)))
            throw new System.IO.InvalidDataException("业务就绪证据不一致。");
        ProcessText = value.ProcessStatus switch { BusinessServiceStatus.Stopped => "已停止", BusinessServiceStatus.Starting => "正在启动",
            BusinessServiceStatus.Recovering => "恢复中", BusinessServiceStatus.Running => "正在运行", BusinessServiceStatus.Stopping => "正在停止", _ => "故障" };
        ReadinessText = value.Readiness switch { BusinessReadiness.Ready => "业务就绪", BusinessReadiness.NotReady => "业务未就绪", _ => "业务就绪未核实" };
        EpochText = verified ? $"已核实代次：{epoch}" : "当前代次未核实";
        SetChecks(value.Checks);
    }
    private void SetChecks(IReadOnlyList<StartupCheckResult> checks)
    {
        Checks.Clear();
        foreach (var check in checks.Take(100)) Checks.Add(new(CheckName(check.Name), check.Passed ? "通过" : "失败", check.BlocksWrites && !check.Passed ? "阻止写入" : "—"));
        if (checks.Count == 0) Checks.Add(new("尚无有效检查结果", "未核实", "不能据此判定就绪"));
    }
    private static string CheckName(string name) => name.StartsWith("activation-", StringComparison.Ordinal)
        ? "启动前：" + CheckName(name[11..]) : name switch {
            "database" => "数据库连接", "database-migrations" => "数据库版本", "database-role" => "数据库角色",
            "database-node-epoch" => "活动节点与服务代次", "node-certificate-private-key" => "节点证书与私钥",
            "backup-integrity" => "备份完整性", "recovery-snapshot" => "恢复数据核对", "recovery-target" => "恢复目标",
            "recovery-working-copy" => "隔离恢复副本", "recovery-confirmed-state" => "最新持久确认",
            "failover-state" => "主备状态", "synchronous-protection" => "双机同步保护", "service-configuration" => "业务配置",
            "data-directory" => "数据目录", "data-free-space" => "可用空间", "data-write" => "持久写入",
            "activation-sequences" => "设备编号记录", "version-projections" => "试卷版本记录", "published-resources" => "已发布试卷资源",
            "submission-receipts" => "答卷与交卷回执", "reliable-events" => "考试指令记录", "recovery-wal-replay" => "数据库恢复进度",
            _ => "服务完整性检查" };
    private bool Current(IManagementConnection connection, long generation) => !_disposed && ReferenceEquals(_connection, connection) && _generation == generation;
    private bool Accept(IManagementConnection connection, long generation, bool unauthorized)
    {
        if (!Current(connection, generation)) return false;
        if (!unauthorized) return true;
        ResetSession(); Message = "管理会话已失效。原请求保留，重新登录后查询。"; return false;
    }
    private CancellationTokenSource BeginRead()
    {
        _reads?.Cancel(); _reads = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _readBusy = true; UpdateCommands(); return _reads;
    }
    private void EndRead(CancellationTokenSource source)
    { if (!ReferenceEquals(_reads, source)) return; _reads = null; _readBusy = false; UpdateCommands(); }
    public void CancelWaiting() { _reads?.Cancel(); Message = _pending is null ? "已取消本次等待。" : "已取消等待；原请求保留，服务器操作不会因此撤销。"; }
    private void ResetSession(bool dispose = true)
    {
        _generation++; _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new(); _reads?.Cancel();
        _polling?.Cancel();
        if (dispose) _connection?.Dispose(); _connection = null; IsSignedIn = false; IsBusy = false;
        _readBusy = false; _pending = null; _operation = null; _missingOriginal = false; CanResumeRestore = false;
        _configurationVersion = null; Checks.Clear(); Logs.Clear(); MarkUnknown(); NotifyPending();
        OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(ConfigurationVersionText));
    }
    private void MarkUnknown() { ProcessText = "当前进程状态未核实"; ReadinessText = "业务就绪未核实"; EpochText = "当前代次未核实"; MarkProtectionUnknown(); }
    private void StartPolling()
    {
        if (!_active || !IsSignedIn || _pending is null || _polling is { IsCancellationRequested: false }) return;
        var polling = _polling = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = PollAsync(polling);
    }
    private async Task PollAsync(CancellationTokenSource polling)
    {
        try
        {
            // Bounded, read-only and serialized with explicit reads; never repeats a mutation.
            for (var attempt = 0; attempt < 10 && _active && IsSignedIn && _pending is not null; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), polling.Token);
                if (!_readBusy && !IsBusy && !polling.IsCancellationRequested) await QueryAsync();
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_polling, polling)) _polling = null; polling.Dispose(); }
    }
    private void LoadSettings()
    {
        try { if (_settings?.Load() is { } target) { Address = target.Address; ServerId = target.ServerId; NodeId = target.NodeId; } }
        catch { Message = "管理连接配置不可读取，请按部署信息重新填写。"; }
    }
    public void ReportError(Exception? error = null) => OnError(error ?? new InvalidOperationException());
    private void OnError(Exception error) => Message = error is ArgumentException ? "输入无效，请核对目标身份、路径、端口及配置版本。" :
        error is UnauthorizedAccessException ? "管理认证失败，请核对已启用的考务账号。" : "管理操作未确认，请核对连接、证书及本机部署。";
    private void NotifyPending() { OnPropertyChanged(nameof(Pending)); OnPropertyChanged(nameof(PendingText)); UpdateCommands(); }
    private void UpdateCommands()
    {
        foreach (var command in new[] { RefreshCommand, LogoutCommand, StartCommand, StopCommand, ChecksCommand, ConfigureCommand,
                     BackupCommand, RestoreCommand, TakeoverCommand, EmergencyCommand, RebuildCommand, ReprotectCommand,
                     QueryCommand, ResumeRestoreCommand, RetryOriginalCommand }) command?.RaiseCanExecuteChanged();
        CancelWaitCommand?.RaiseCanExecuteChanged();
    }
    private static string SafeError(HttpStatusCode status) => status switch
    { HttpStatusCode.Conflict => "配置版本或请求内容冲突。表单已保留，请核对并重新确认", HttpStatusCode.Forbidden => "操作权限或操作者不一致",
        HttpStatusCode.NotFound => "原操作尚未找到，不能据此判定没有执行", HttpStatusCode.BadRequest => "后端拒绝输入，请核对表单",
        _ => "后端依赖不可用或操作失败，不能判定成功" };
    private static string YesNo(bool value) => value ? "已确认" : "未完成";
    private static string KindText(ManagementOperationKind kind) => kind switch { ManagementOperationKind.Start => "启动", ManagementOperationKind.Stop => "停止",
        ManagementOperationKind.Configure => "保存配置", ManagementOperationKind.Checks => "自检", ManagementOperationKind.Backup => "备份", ManagementOperationKind.Restore => "恢复",
        ManagementOperationKind.Takeover => "接管", ManagementOperationKind.Emergency => "进入单机应急", ManagementOperationKind.Rebuild => "重建旧节点", ManagementOperationKind.Reprotect => "恢复同步保护", _ => "未知操作" };
    private static string PhaseText(ManagementOperationPhase phase) => phase switch { ManagementOperationPhase.Completed => "完成", ManagementOperationPhase.Failed => "失败",
        ManagementOperationPhase.Accepted => "已受理", ManagementOperationPhase.Staged => "仅隔离暂存", _ => "待核对" };
    public void Dispose() { if (_disposed) return; Deactivate(); ResetSession(); _disposed = true; _lifetime.Dispose(); }
}
