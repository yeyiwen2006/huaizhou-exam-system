using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed record ManagementConfirmation(string Title, string Body, bool Dangerous);
public sealed record ManagementCheckRow(string Name, string Result, string WriteGate);
public sealed record ManagementLogRow(string Time, string Source, string Kind, string Phase, string Code, string Identifier);

public sealed partial class ServiceManagementViewModel : BindableBase, IDisposable
{
    private readonly Func<ManagementTarget, IManagementConnection> _factory;
    private readonly IPendingManagementStore _store;
    private readonly ManagementEndpointSettings? _settings;
    private readonly TimeProvider _time;
    private IManagementConnection? _connection;
    private AdminSessionSnapshot? _businessSession;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reads;
    private CancellationTokenSource? _polling;
    private CancellationTokenSource? _restoreProgress;
    private string? _restoreWriteInFlight;
    private long _generation;
    private bool _disposed, _busy, _readBusy, _active, _signedIn;
    private PendingManagementRequest? _pending;
    private ManagementOperation? _operation;
    private string _address = string.Empty, _server = string.Empty, _node = string.Empty, _username = string.Empty;
    private string _message = string.Empty;
    private string _serverStatus = "服务器程序状态未确认", _requestIdToQuery = string.Empty, _lastRequestId = string.Empty;
    private string _bind = string.Empty, _port = string.Empty, _data = string.Empty;
    private bool _ha;
    private long? _configurationVersion;
    private string _destination = string.Empty, _backupId = string.Empty, _source = string.Empty;
    private string _operationText = string.Empty, _restoreText = string.Empty;
    private bool _canResume;
    private bool _missingOriginal;

    public ServiceManagementViewModel(Func<ManagementTarget, IManagementConnection> factory,
        IPendingManagementStore store, ManagementEndpointSettings? settings = null, IProtectionWarningStore? protectionMemory = null,
        TimeProvider? time = null, bool automaticMonitoring = false)
    {
        _factory = factory; _store = store; _settings = settings; _protectionMemory = protectionMemory; _time = time ?? TimeProvider.System;
        _automaticMonitoringEnabled = automaticMonitoring;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsSignedIn && !_readBusy, OnError);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync,
            () => _businessSession is not null && HasConnectionConfiguration && !IsBusy, OnError);
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
        QueryCommand = new AsyncRelayCommand(QueryAsync, () => IsSignedIn && !_readBusy &&
            (!string.IsNullOrWhiteSpace(RequestIdToQuery) || _lastRequestId.Length > 0 || _pending is not null), OnError);
        ResumeRestoreCommand = new AsyncRelayCommand(ResumeRestoreAsync, () => IsSignedIn && CanResumeRestore && !IsBusy, OnError);
        RetryOriginalCommand = new AsyncRelayCommand(RetryOriginalAsync, () => IsSignedIn && _pending is not null && _missingOriginal && !IsBusy && !_readBusy, OnError);
        CancelWaitCommand = new RelayCommand(CancelWaiting, () => IsBusy || _readBusy);
        SaveConnectionCommand = new RelayCommand(SaveConnectionSettings, () => CanConfigureConnection && HasConnectionConfiguration);
        LoadSettings();
        LoadProtectionWarning();
    }

    public static ServiceManagementViewModel CreateDefault()
    {
        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exam.Admin", "Management");
        return new ServiceManagementViewModel(target => new ManagementConnection(target),
            new ProtectedPendingManagementStore(System.IO.Path.Combine(directory, "Pending")),
            new ManagementEndpointSettings(System.IO.Path.Combine(directory, "endpoint.json"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "management-connection.json")),
            new ProtectedProtectionWarningStore(System.IO.Path.Combine(directory, "Protection")), automaticMonitoring: true);
    }

    public Func<ManagementConfirmation, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);
    public string Address { get => _address; set { if (SetProperty(ref _address, value)) ConnectionSettingsChanged(); } }
    public string ServerId { get => _server; set { if (SetProperty(ref _server, value)) ConnectionSettingsChanged(); } }
    public string NodeId { get => _node; set { if (SetProperty(ref _node, value)) ConnectionSettingsChanged(); } }
    public string RestoreGuidance => "须先停止目标服务器。填写服务器上的备份来源目录后，系统会读取备份信息并请你确认。旧备份缺少后续已确认的数据时，恢复条件核对会阻止服务器开放。";
    public bool HasConnectionConfiguration
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Address) || string.IsNullOrWhiteSpace(ServerId) || string.IsNullOrWhiteSpace(NodeId)) return false;
            try { CurrentTarget().Validate(); return true; }
            catch (ArgumentException) { return false; }
        }
    }
    public bool CanConfigureConnection => IsSignedOut && !IsBusy;
    public bool CanLogin => CanEnterManagementCredentials && HasConnectionConfiguration;
    public string ConnectionStatus => !HasConnectionConfiguration
        ? "服务器管理连接尚未配置或配置无效，请联系系统部署人员核对部署配置。"
        : _businessSession is null ? "管理连接已配置；主服务器不可用导致无法正常登录时，可直接使用已启用的考务账号登录管理。"
        : IsSignedIn ? $"正在使用考务账号 {_businessSession.Username} 管理服务器。"
        : "连接已配置，将使用当前考务账号连接服务器管理。";
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string ServerStatusText
    {
        get => _serverStatus;
        private set
        {
            if (!SetProperty(ref _serverStatus, value)) return;
            OnPropertyChanged(nameof(PrimaryServerStatusText)); OnPropertyChanged(nameof(StandbyServerStatusText));
        }
    }
    public string RequestIdToQuery { get => _requestIdToQuery; set { if (SetProperty(ref _requestIdToQuery, value)) QueryCommand.RaiseCanExecuteChanged(); } }
    public string BindAddress { get => _bind; set => SetProperty(ref _bind, value); }
    public string HttpsPort { get => _port; set => SetProperty(ref _port, value); }
    public string DataDirectory { get => _data; set => SetProperty(ref _data, value); }
    public bool HighAvailabilityEnabled { get => _ha; set => SetProperty(ref _ha, value); }
    public string ConfigurationVersionText => _configurationVersion is { } v ? $"读取版本：{v}；保存按此版本核对" : "配置版本未核实，不能保存";
    public string BackupDestination { get => _destination; set => SetProperty(ref _destination, value); }
    public string RestoreBackupId { get => _backupId; set => SetProperty(ref _backupId, value); }
    public string RestoreSource { get => _source; set => SetProperty(ref _source, value); }
    public string OperationText { get => _operationText; private set { if (SetProperty(ref _operationText, value)) OnPropertyChanged(nameof(HasOperationText)); } }
    public bool HasOperationText => !string.IsNullOrWhiteSpace(OperationText);
    public string RestoreText { get => _restoreText; private set => SetProperty(ref _restoreText, value); }
    public string PendingText => _pending is null ? string.Empty : $"请求：{_pending.RequestId} · {KindText(_pending.Kind)} · 待查询处理状态";
    public PendingManagementRequest? Pending => _pending;
    public ManagementOperation? Operation => _operation;
    public bool IsSignedIn { get => _signedIn; private set { if (SetProperty(ref _signedIn, value)) { OnPropertyChanged(nameof(IsSignedOut)); OnPropertyChanged(nameof(ShowManagementLogin)); OnPropertyChanged(nameof(ShowProtectionWarning)); UpdateCommands(); } } }
    public bool IsSignedOut => !IsSignedIn;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) UpdateCommands(); } }
    public bool CanResumeRestore { get => _canResume; private set { if (SetProperty(ref _canResume, value)) UpdateCommands(); } }
    public string TargetText => ManagedServersText;
    public ObservableCollection<ManagementCheckRow> Checks { get; } = [];
    public ObservableCollection<ManagementLogRow> Logs { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
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
    public RelayCommand SaveConnectionCommand { get; }
    private AsyncRelayCommand OperationCommand(ManagementOperationKind kind) =>
        new(() => RequestAsync(kind), () => IsSignedIn && !IsBusy && !HasConflictingManagementRequests(kind) && CanVerifyHighAvailabilityTarget(kind), OnError);

    public void Activate()
    {
        if (_disposed) return;
        var returning = !_active;
        _active = true;
        if (returning && Message == "查询已暂停，返回后可重新刷新状态与日志。") Message = string.Empty;
        if (!IsSignedIn && ReconnectCommand.CanExecute(null)) ReconnectCommand.Execute(null);
        StartPolling();
    }

    public void SetBusinessSession(AdminSessionSnapshot? session)
    {
        if (session is not null && _businessSession?.Matches(session) == true) return;
        if (session is not null && _businessSession?.SameAuthentication(session) == true)
        { _businessSession = session; return; }
        ResetSession();
        _businessSession = session;
        RestartBackupMonitoring();
        _lastRequestId = string.Empty; RequestIdToQuery = string.Empty; OperationText = string.Empty;
        Username = session?.Username ?? string.Empty;
        Message = session is null ? "主服务器不可用导致无法正常登录时，可在此使用考务账号独立登录管理服务。" : string.Empty;
        OnPropertyChanged(nameof(ConnectionStatus)); UpdateCommands();
        if (_active && ReconnectCommand.CanExecute(null)) ReconnectCommand.Execute(null);
    }

    public async Task ReconnectAsync()
    {
        if (_disposed || IsBusy || _businessSession is not { } session || !HasConnectionConfiguration) return;
        ResetSession();
        if (session.ServerId != ServerId.Trim())
        { Message = "管理连接与当前考务会话不属于同一考试系统，请联系部署人员核对主备连接配置。"; return; }
        var target = CurrentTarget(); var generation = _generation;
        IsBusy = true;
        try
        {
            var connection = await ConnectTargetsAsync(target,
                (client, token) => client.UseBusinessSessionAsync(session, token), session.Username, _lifetime.Token);
            if (connection is null || _disposed || generation != _generation || _businessSession?.Matches(session) != true) return;
            _connection = connection;
            LoadCurrentPendingRequest(); LoadProtectionWarning(connection.Target); _settings?.Save(target);
            IsSignedIn = true; RestartBackupMonitoring(); Message = "已使用当前考务账号连接服务器管理。";
            OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(ConnectionStatus));
        }
        catch (Exception) when (generation != _generation || _disposed) { return; }
        catch { ResetSession(); throw; }
        finally { if (generation == _generation) IsBusy = false; }
        if (IsSignedIn) await RefreshAsync();
        StartPolling();
    }
    public void Deactivate()
    {
        if (!_active) return;
        _active = false; _generation++; _polling?.Cancel(); _availabilityPolling?.Cancel(); StopWaiting();
        if (_readBusy) Message = "查询已暂停，返回后可重新刷新状态与日志。";
        MarkProtectionUnknown();
        _reads = null; _readBusy = false; IsBusy = false; UpdateCommands();
    }
    public bool IsPageActive => _active;

    public async Task LoginAsync(string password)
    {
        if (_disposed || IsBusy) return;
        if (_peerLoginTarget is not null) { await LoginPendingPeerAsync(password); return; }
        if (!CanLogin) { Message = ConnectionStatus; return; }
        ResetSession();
        var generation = _generation;
        var target = CurrentTarget(); target.Validate();
        var username = Username;
        IsBusy = true;
        try
        {
            var connection = await ConnectTargetsAsync(target,
                (client, token) => client.LoginAsync(username, password, token), username, _lifetime.Token);
            if (connection is null || _disposed || generation != _generation) return;
            _connection = connection;
            LoadCurrentPendingRequest(); LoadProtectionWarning(connection.Target); _settings?.Save(target);
            IsSignedIn = true; RestartBackupMonitoring(); Message = "已建立独立管理会话。";
            OnPropertyChanged(nameof(TargetText));
        }
        catch (Exception) when (generation != _generation || _disposed) { return; }
        catch { ResetSession(); throw; }
        finally { if (generation == _generation) IsBusy = false; }
        if (IsSignedIn) await RefreshAsync();
        StartPolling();
    }

    public async Task LogoutAsync()
    {
        var old = ResetSession(dispose: false);
        var version = _managementSessionVersion;
        Message = "服务器管理会话已清除。";
        if (old.Length == 0) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Task.WhenAll(old.Select(connection => connection.LogoutAsync(deadline.Token).WaitAsync(deadline.Token)));
        }
        catch { if (version == _managementSessionVersion) Message = "本机会话已清除；部分节点的服务端撤销未确认，管理宿主重启也会使旧会话失效。"; }
        finally { foreach (var connection in old) connection.Dispose(); }
    }

    public async Task RefreshAsync()
    {
        if (!IsSignedIn || _connection is null || _readBusy) return;
        await ReconcileKnownRequestsAsync();
        if (!IsSignedIn || _connection is null || _readBusy) return;
        var connection = _connection; var generation = _generation;
        using var reads = BeginRead();
        StartHighAvailabilityPolling();
        var availabilityRead = ObserveHighAvailabilityAsync(connection, generation, reads.Token);
        var statusSequence = BeginNodeStatusObservation(connection.Target);
        _ = RefreshPeerStatusAsync(force: true);
        var statusApplied = false;
        Message = "正在刷新状态与日志…";
        try
        {
            var status = await connection.StatusAsync(reads.Token);
            if (reads.IsCancellationRequested) return;
            if (!Accept(connection, generation, status.IsUnauthorized)) return;
            if (status.Envelope.Error is not null) { MarkNodeStatusUnavailable(connection.Target, statusSequence); ServerStatusText = "服务器程序状态未确认"; Message = SafeError(status.StatusCode); return; }
            var value = status.Envelope.Result ?? throw new System.IO.InvalidDataException();
            if (!Enum.IsDefined(value.ProcessStatus) || !Enum.IsDefined(value.Readiness) || value.Checks is null)
                throw new System.IO.InvalidDataException();
            ApplyStatus(value, status.Envelope.EpochVerified, status.Envelope.ServerEpoch, statusSequence);
            statusApplied = true;
            await availabilityRead;
            if (reads.IsCancellationRequested || !Current(connection, generation)) return;
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
                foreach (var entry in entries) Logs.Add(new(entry.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"), "操作", KindText(entry.Kind), PhaseText(entry.Phase),
                    entry.ErrorCode is not null || entry.Phase == ManagementOperationPhase.Failed ? "未完成" : entry.Phase == ManagementOperationPhase.Completed ? "成功" : "—", entry.RequestId));
            }
            if (logs.Envelope.Result is { } runtime && logs.Envelope.Error is null)
            {
                if (runtime.Count > 50 || runtime.Any(e => e.Source is not ("business" or "management") ||
                    e.Level is not ("Warning" or "Error" or "Critical") || e.Timestamp == default || e.Category is null ||
                    e.Category.Length is < 1 or > 200 || !e.Category.StartsWith("Exam.", StringComparison.Ordinal) ||
                    e.Category.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '+'))))
                    throw new System.IO.InvalidDataException("限定运行日志无效。");
                foreach (var entry in runtime) Logs.Add(new(entry.Timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss"),
                    entry.Source == "business" ? "业务服务" : "服务器管理", "运行记录",
                    entry.Level switch { "Warning" => "警告", "Error" => "错误", _ => "严重错误" }, "—", entry.EventId.ToString(CultureInfo.InvariantCulture)));
            }
            Message = audit.Envelope.Error is null && logs.Envelope.Error is null ? "服务器状态和限定日志已刷新。" : "状态已刷新；部分限定日志暂不可用。";
        }
        catch (OperationCanceledException)
        {
            if (Current(connection, generation) && ReferenceEquals(_reads, reads))
            {
                if (!statusApplied) { MarkNodeStatusUnavailable(connection.Target, statusSequence); ServerStatusText = "服务器程序状态未确认"; }
                // A transport timeout does not cancel our token. Only the cancel command reports user cancellation.
                if (!reads.IsCancellationRequested) Message = "刷新状态与日志超时，请检查连接后重试。";
            }
        }
        catch { if (Current(connection, generation) && !statusApplied) { MarkNodeStatusUnavailable(connection.Target, statusSequence); ServerStatusText = "服务器程序状态未确认"; } throw; }
        finally
        {
            // Even a failed status or log request must observe the independent HA task.
            try { await availabilityRead; } catch { }
            EndRead(reads);
        }
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
        if (!IsSignedIn || _connection is null || IsBusy || HasConflictingManagementRequests(kind)) return;
        if (kind is ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild &&
            !await RouteHighAvailabilityToSelectedNodeAsync(kind)) return;
        if (_connection?.Actor is not { } actor || !IsSignedIn || IsBusy || HasConflictingManagementRequests(kind)) return;
        var connection = _connection; var generation = _generation;
        var id = Guid.NewGuid().ToString("N");
        IsBusy = true; // One gate covers all writes, including a confirmation dialog.
        try
        {
            BackupInfo? backup = null;
            if (kind == ManagementOperationKind.Restore)
            {
                backup = await ReadRestoreBackupAsync(connection, generation);
                if (backup is null) return;
            }
            IsolationEvidence? isolation = null;
            var confirmedRebuild = false;
            if (kind is ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild)
            {
                if (!CanIssueHighAvailability(kind) || _availability?.State is not { } before)
                    throw new ArgumentException("请刷新主备状态后重新选择目标节点。");
                var targetNode = HighAvailabilityTarget.Trim();
                var note = HighAvailabilityReason.Trim();
                if (note.Length > 500) throw new ArgumentException("备注最多500字。");
                var confirmationText = kind == ManagementOperationKind.Takeover
                    ? $"准备变为主服务器的节点：{targetNode}\n当前主机：{before.ActiveNodeId}\n" +
                    "确认表示你已在现场将当前主机的全部业务网络隔离，且会在恢复处理完成前保持隔离。\n" +
                    "点击确定后，所选备机将接管为主服务器，原主机角色将变为隔离；点击取消不会执行接管。"
                    : $"准备重建的隔离节点：{targetNode}\n当前主机：{before.ActiveNodeId}\n" +
                    "系统将停止所选节点的旧服务，保留旧数据库和资源的隔离副本，从当前主机重新复制数据并启动为备机。\n" +
                    "请保持旧业务隔离，并确保重建所需的管理和复制连接可用。点击确定开始重建，点击取消不执行。";
                if (note.Length > 0) confirmationText += $"\n备注：{note}";
                if (!await ConfirmAsync(new(kind == ManagementOperationKind.Takeover ? "确认接管为主服务器" : "确认重建隔离的服务器并作为备机", confirmationText, true))) return;
                if (!Current(connection, generation) || !IsSignedIn) return;
                // A human may need more than the proof lifetime to confirm. Re-read the selected node,
                // and require another confirmation if the active node, epoch or selection changed.
                await ObserveHighAvailabilityAsync(connection, generation, _lifetime.Token);
                if (!Current(connection, generation) || _availability?.State is not { } after ||
                    after.ActiveNodeId != before.ActiveNodeId || after.ServerEpoch != before.ServerEpoch ||
                    after.Version != before.Version || HighAvailabilityTarget.Trim() != targetNode ||
                    HighAvailabilityReason.Trim() != note || !CanIssueHighAvailability(kind))
                {
                    Message = "主备状态或本次目标已变化，请核对后重新发起操作。";
                    return;
                }
                if (kind == ManagementOperationKind.Takeover)
                    isolation = new(before.ActiveNodeId, false, true, actor, "考务员通过接管确认对话，确认旧主全部业务网络已隔离并将保持隔离。");
                else confirmedRebuild = true;
            }
            var body = BuildBody(kind, connection.Target, actor, id, backup, isolation, confirmedRebuild);
            var request = PendingManagementRequest.Create(connection.Target, actor, kind, id, body);
            var confirmation = kind == ManagementOperationKind.Restore
                ? new ManagementConfirmation("确认按此备份恢复", RestoreConfirmationBody(request, backup!), true)
                : new ManagementConfirmation("确认" + KindText(kind), ConfirmationBody(kind, request), kind is not ManagementOperationKind.Checks);
            if (kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild) && !await ConfirmAsync(confirmation)) return;
            if (!Current(connection, generation) || !IsSignedIn) return;
            _store.Save(request); // Failure here must prevent network side effects.
            _knownPendingRequests[request.Target.Key] = request;
            _pending = request; _lastRequestId = request.RequestId; RequestIdToQuery = request.RequestId; NotifyPending();
            if (kind == ManagementOperationKind.Restore) await ExecuteRestoreAsync(request, connection, generation);
            else
            {
                using var wait = BeginRead();
                try { var reply = await connection.ExecuteAsync(kind, body, wait.Token); if (!wait.IsCancellationRequested) Apply(reply, request, connection, generation); }
                catch (Exception) { if (Current(connection, generation)) Message = "操作结果尚未确认。原请求已保留，请查询请求处理状态；取消等待不会撤销服务器操作。"; }
                finally { EndRead(wait); }
            }
        }
        finally { if (Current(connection, generation)) IsBusy = false; }
        StartPolling();
    }

    public async Task QueryAsync()
    {
        var id = string.IsNullOrWhiteSpace(RequestIdToQuery) ? _lastRequestId : RequestIdToQuery.Trim();
        if (id.Length == 0) id = _pending?.RequestId ?? _knownPendingRequests.Values.FirstOrDefault()?.RequestId ?? string.Empty;
        if (!await RoutePendingQueryAsync(id)) return;
        await QueryRequestAsync(id);
    }

    private async Task ReconcileKnownRequestsAsync()
    {
        var version = _managementSessionVersion;
        var generation = _generation;
        using var wait = BeginRead();
        var pending = _knownPendingRequests.Values.Append(_pending).OfType<PendingManagementRequest>()
            .DistinctBy(request => request.Target.Key).ToArray();
        try { await Task.WhenAll(pending.Select(async request =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var connection = await EnsureTargetSessionAsync(request.Target, deadline.Token);
                if (connection?.Actor != request.Actor) return;
                var reply = await connection.FindAsync(request.RequestId, deadline.Token);
                if (deadline.IsCancellationRequested || version != _managementSessionVersion || generation != _generation ||
                    reply.IsUnauthorized || reply.Envelope.Result is not { } operation) return;
                ValidatePendingOperation(operation, request);
                if (ReferenceEquals(connection, _connection)) Apply(reply, request, connection, generation);
                else if (operation.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed)
                {
                    _store.Clear(request);
                    _knownPendingRequests.TryRemove(request.Target.Key, out _);
                    NotifyPending();
                }
            }
            catch (Exception) { /* Keep the exact original request until its owner can verify it. */ }
        })); }
        finally { EndRead(wait); }
    }

    private async Task QueryRequestAsync(string id)
    {
        if (!IsSignedIn || _connection is null || id.Length == 0 || _readBusy) return;
        var connection = _connection; var generation = _generation;
        var pending = _pending?.RequestId == id ? _pending : null;
        if (pending is not null && connection.Actor != pending.Actor)
        { Message = "该请求属于原管理账号。请用原账号登录考务端后查询。"; return; }
        using var wait = BeginRead();
        try
        {
            var reply = await connection.FindAsync(id, wait.Token);
            if (wait.IsCancellationRequested || !Accept(connection, generation, reply.IsUnauthorized)) return;
            if (pending is not null)
            {
                Apply(reply, pending, connection, generation,
                    restoreInProgress: _restoreWriteInFlight == pending.RequestId && _restoreProgress is { IsCancellationRequested: false });
                if (pending.Kind == ManagementOperationKind.Restore && RestoreCompleted(_restoreOperation))
                {
                    EndRead(wait);
                    await RefreshRestoredServerAsync(connection, generation);
                }
                return;
            }
            if (reply.StatusCode == HttpStatusCode.NotFound)
            { Message = $"未找到请求 {id}，请核对请求编号。"; return; }
            if (reply.Envelope.Result is not { } operation)
            { Message = SafeError(reply.StatusCode); return; }
            if (operation.RequestId != id || operation.ActorId != connection.Actor ||
                operation.ServerId != connection.Target.ServerId || operation.NodeId != connection.Target.NodeId ||
                !Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.Phase))
                throw new System.IO.InvalidDataException("请求处理记录与当前身份不一致。");
            // Historical results describe that request only; never settle another pending request or overwrite current server status.
            _operation = operation; OnPropertyChanged(nameof(Operation));
            OperationText = $"{KindText(operation.Kind)}：{PhaseText(operation.Phase)} · {operation.RequestId}";
            Message = string.Empty;
        }
        catch (Exception) { if (Current(connection, generation)) Message = "请求处理状态尚未确认，请核对编号和连接后再次查询。"; }
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
            await ExecuteRestoreAsync(request, connection, generation);
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
            if (request.Kind == ManagementOperationKind.Restore) await ExecuteRestoreAsync(request, connection, generation);
            else
            {
                using var wait = BeginRead();
                try { var reply = await connection.ExecuteAsync(request.Kind, request.Body, wait.Token); if (!wait.IsCancellationRequested) Apply(reply, request, connection, generation); }
                catch { if (Current(connection, generation)) Message = "原请求重试结果仍未确认，继续查询请求处理状态。"; }
                finally { EndRead(wait); }
            }
        }
        finally { if (Current(connection, generation)) IsBusy = false; }
        StartPolling();
    }

    private void Apply(ManagementReply<ManagementOperation> reply, PendingManagementRequest request, IManagementConnection connection, long generation,
        bool restoreInProgress = false)
    {
        if (!Accept(connection, generation, reply.IsUnauthorized)) return;
        if (reply.Envelope.Result is not { } operation)
        { _missingOriginal = reply.StatusCode == HttpStatusCode.NotFound; UpdateCommands();
            Message = _missingOriginal ? $"未找到请求 {request.RequestId}，原请求保留，请核对编号后再次查询处理状态。"
                : SafeError(reply.StatusCode) + "；原请求保留，请查询处理状态。"; return; }
        ValidatePendingOperation(operation, request);
        if (operation.Kind == ManagementOperationKind.Restore)
        {
            // A delayed checkpoint must not move the current restore backwards after its final response.
            if (_restoreOperation is { } previous && previous.RequestId == operation.RequestId &&
                (operation.UpdatedAt < previous.UpdatedAt ||
                 previous.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed &&
                 operation.Phase is not (ManagementOperationPhase.Completed or ManagementOperationPhase.Failed))) return;
            if (operation.Phase == ManagementOperationPhase.Completed && !RestoreCompleted(operation))
                throw new System.IO.InvalidDataException("恢复完成证据不完整。");
            _restoreOperation = operation;
            RestoreText = RestoreStatus(operation, restoreInProgress);
        }
        _missingOriginal = false;
        _operation = operation;
        OnPropertyChanged(nameof(Operation));
        OperationText = $"{KindText(operation.Kind)}：{PhaseText(operation.Phase)} · {operation.RequestId}";
        CanResumeRestore = operation.Kind == ManagementOperationKind.Restore &&
            (operation.Phase == ManagementOperationPhase.Staged || operation.Phase == ManagementOperationPhase.NeedsVerification &&
                operation.Restore is { IsolationStaged: true } && operation.RecoveryActivation is null &&
                operation.Checks?.Any(c => c.Name == "recovery-confirmed-state" && !c.Passed && c.BlocksWrites) == true);
        if (operation.Status is { } status) ApplyStatus(status, reply.Envelope.EpochVerified, reply.Envelope.ServerEpoch);
        if (operation.HighAvailability is { } availability) ApplyHighAvailability(availability, proofAlreadyPersisted: false,
            completedRoleTransition: operation.Phase == ManagementOperationPhase.Completed &&
                operation.Kind is ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild);
        if (operation.Checks is not null) SetChecks(operation.Checks);
        if (operation.Phase == ManagementOperationPhase.Completed && operation.Backup is { } backup)
        {
            RestoreBackupId = backup.BackupId;
            RestoreSource = backup.DestinationDirectory;
        }
        Message = operation.Phase switch
        {
            ManagementOperationPhase.Completed when operation.Kind == ManagementOperationKind.Checks && operation.Checks!.Any(c => !c.Passed && c.BlocksWrites) => "状态检测已完成，存在阻止业务写入的失败项。",
            ManagementOperationPhase.Completed when operation.Backup is { } => "备份已完成：数据库、受保护配置和版本资源均已包含。",
            ManagementOperationPhase.Completed => "操作已完成，并已核对后端完成证据。",
            ManagementOperationPhase.Staged => "恢复仅完成隔离暂存，业务尚未就绪。",
            ManagementOperationPhase.NeedsVerification => "操作仍待核对；不能创建新请求代替原请求。",
            ManagementOperationPhase.Failed => SafeError(reply.StatusCode),
            _ => "操作已受理，继续查询请求处理状态。"
        };
        if (operation.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed)
        {
            _polling?.Cancel();
            if (operation.Configuration is not null && operation.Phase == ManagementOperationPhase.Completed) LoadConfiguration(operation.Configuration);
            _store.Clear(request); _knownPendingRequests.TryRemove(request.Target.Key, out _);
            _pending = null; CanResumeRestore = false; LoadCurrentPendingRequest();
        }
    }

    private static void ValidatePendingOperation(ManagementOperation operation, PendingManagementRequest request)
    {
        if (operation.ActorId != request.Actor || operation.RequestId != request.RequestId || operation.Kind != request.Kind ||
            operation.ServerId != request.Target.ServerId || operation.NodeId != request.Target.NodeId || operation.ContentHash != request.BodyHash)
            throw new System.IO.InvalidDataException("操作身份与原请求不一致。");
        if (!Enum.IsDefined(operation.Phase) || operation.Phase == ManagementOperationPhase.Failed && operation.Error is null ||
            operation.Phase is ManagementOperationPhase.Accepted or ManagementOperationPhase.Completed or ManagementOperationPhase.Staged && operation.Error is not null)
            throw new System.IO.InvalidDataException("操作阶段证据无效。");
    }

    private string BuildBody(ManagementOperationKind kind, ManagementTarget target, string actor, string id, BackupInfo? backup = null, IsolationEvidence? confirmedIsolation = null, bool confirmedRebuild = false)
    {
        object body = kind switch
        {
            ManagementOperationKind.Configure => new ManagementConfigurationRequest(id, target.ServerId, target.NodeId,
                _configurationVersion ?? throw new ArgumentException("先读取当前配置版本。"), BuildConfiguration(target)),
            ManagementOperationKind.Backup => new BackupRequest(id, RequirePath(BackupDestination), actor),
            ManagementOperationKind.Restore => new RestoreRequest(id,
                backup?.BackupId ?? throw new InvalidOperationException("备份信息尚未读取。"), backup.SourceDirectory, actor),
            ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect =>
                BuildHighAvailabilityRequest(kind, target, actor, id, confirmedIsolation, confirmedRebuild),
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
    private static string RequirePath(string path)
    {
        var value = path.Trim();
        // These paths belong to the server, whose operating system may differ from this workstation.
        var absolute = value.StartsWith('/') ||
            value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/' ||
            value.StartsWith("\\\\", StringComparison.Ordinal) && value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        if (!absolute || value.Any(char.IsControl)) throw new ArgumentException("请输入目标服务器上的绝对目录路径。");
        return value;
    }
    private string ConfirmationBody(ManagementOperationKind kind, PendingManagementRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        var fields = document.RootElement;
        var details = kind switch
        {
            ManagementOperationKind.Configure => $"\n预期版本：{fields.GetProperty("expectedVersion")}\n目标配置：{fields.GetProperty("configuration").GetProperty("bindAddress")}:{fields.GetProperty("configuration").GetProperty("httpsPort")}\n数据目录：{fields.GetProperty("configuration").GetProperty("dataDirectory")}",
            ManagementOperationKind.Backup => $"\n备份目录：{fields.GetProperty("destinationDirectory")}",
            ManagementOperationKind.Restore => $"\n备份：{fields.GetProperty("backupId")}\n来源：{fields.GetProperty("sourceDirectory")}",
            ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect =>
                $"\n目标节点：{fields.GetProperty("targetNodeId")}\n备注：{fields.GetProperty("reason")}" +
                (kind == ManagementOperationKind.Takeover ? $"\n原主机：{fields.GetProperty("isolation").GetProperty("isolatedNodeId")}；断电确认：{YesNo(fields.GetProperty("isolation").GetProperty("hostPoweredOff").GetBoolean())}；业务网络隔离确认：{YesNo(fields.GetProperty("isolation").GetProperty("businessNetworkIsolated").GetBoolean())}" : ""),
            _ => string.Empty
        };
        return $"目标：{request.Target.ServerId} / {request.Target.NodeId}\n{KindText(kind)}使用稳定请求号。" + (kind switch
        {
            ManagementOperationKind.Stop => "停止会中断业务服务。",
            ManagementOperationKind.Configure => "后端核对已停止状态与当前配置版本；冲突时不覆盖。",
            ManagementOperationKind.Backup => "须先停止服务器；备份结果必须包含三项完整性证据。",
            ManagementOperationKind.Restore => RestoreGuidance,
            ManagementOperationKind.Takeover => "确认原主已断电或全部业务网络已隔离后，提升本机备库并自动恢复业务。沿用原考生身份、答案和提交标识；恢复前旧主必须保持隔离。",
            ManagementOperationKind.Emergency => "健康主节点会自动以本机可靠落盘继续服务，不需要单独启用。",
            ManagementOperationKind.Rebuild => "停止目标服务器服务，从当前主机重新建立备库和资源。旧数据保留在隔离目录，禁止自动回写到新主机。",
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
    private void ApplyStatus(ManagementStatus value, bool verified, long? epoch, long? observationSequence = null)
    {
        if (_connection is null || !RememberNodeStatus(_connection.Target, value, observationSequence)) return;
        ApplyAutomaticBackupStatus(value.AutomaticBackup);
        ServerStatusText = ProcessStatusText(value);
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
            "business-readiness" => "业务运行状态",
            "certificate" => "服务证书", "storage" => "业务目录", "backup-tools" => "备份恢复工具", "local-snapshot" => "所选备份",
            "database-node-epoch" => "活动节点状态", "node-certificate-private-key" => "节点证书与私钥",
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
        HandleUnauthorizedNode(connection); return false;
    }
    private CancellationTokenSource BeginRead()
    {
        _reads?.Cancel(); _reads = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _readBusy = true; UpdateCommands(); return _reads;
    }
    private void EndRead(CancellationTokenSource source)
    { if (!ReferenceEquals(_reads, source)) return; _reads = null; _readBusy = false; UpdateCommands(); }
    public void CancelWaiting()
    {
        StopWaiting();
        Message = _pending is null ? "已取消本次等待。" : "已取消等待；原请求保留，服务器操作不会因此撤销。";
    }
    private void StopWaiting()
    {
        _reads?.Cancel(); _restoreProgress?.Cancel(); _routingCancellation?.Cancel();
        if (_restoreWriteInFlight is not null) RestoreText = RestoreStatus(_restoreOperation, false);
    }
    private IManagementConnection[] ResetSession(bool dispose = true)
    {
        _generation++; _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new(); _reads?.Cancel();
        _backupMonitoring?.Cancel();
        _polling?.Cancel(); _availabilityPolling?.Cancel(); _restoreProgress?.Cancel(); _restoreWriteInFlight = null; _restoreOperation = null; RestoreText = string.Empty;
        var released = ReleasePeerConnections(dispose);
        _connection = null; IsSignedIn = false; IsBusy = false;
        _readBusy = false; _pending = null; _operation = null; _missingOriginal = false; CanResumeRestore = false;
        _configurationVersion = null; Checks.Clear(); Logs.Clear(); MarkUnknown(); NotifyPending();
        OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(ConfigurationVersionText));
        return released;
    }
    private void MarkUnknown() { ServerStatusText = "服务器程序状态未确认"; MarkProtectionUnknown(); }
    private void StartPolling()
    {
        StartHighAvailabilityPolling();
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
                if (!_readBusy && !IsBusy && !polling.IsCancellationRequested && _pending is { } pending)
                    await QueryRequestAsync(pending.RequestId);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_polling, polling)) _polling = null; polling.Dispose(); }
    }
    private void LoadSettings()
    {
        try { if (_settings?.Load() is { } target) RememberConfiguredTarget(target); }
        catch { Message = "管理连接配置不可读取，请联系系统部署人员核对部署文件。"; }
    }

    public void ImportConnectionSettings(string path)
    {
        if (!CanConfigureConnection) return;
        try
        {
            var target = ManagementEndpointSettings.LoadFile(path);
            if (_settings is null) throw new InvalidOperationException("未配置连接记录目录。");
            _settings.Save(target);
            RememberConfiguredTarget(target);
            LoadProtectionWarning();
            Message = "连接配置已导入并保存。";
            if (_businessSession is not null && ReconnectCommand.CanExecute(null)) ReconnectCommand.Execute(null);
        }
        catch { Message = "连接配置导入失败。请使用部署人员提供的有效服务器管理连接文件；原连接配置已保留。"; }
    }

    private void SaveConnectionSettings()
    {
        try
        {
            if (!CanConfigureConnection || !HasConnectionConfiguration || _settings is null) return;
            _settings.Save(CurrentTarget());
            LoadProtectionWarning();
            Message = "连接设置已保存，下次打开时自动使用。";
            if (_businessSession is not null && ReconnectCommand.CanExecute(null)) ReconnectCommand.Execute(null);
        }
        catch { Message = "连接设置未能保存，请联系系统部署人员检查本机配置目录。"; }
    }

    private void ConnectionSettingsChanged()
    {
        RestartBackupMonitoring();
        UpdateDisplayedNodes();
        if (!IsSignedIn) { _lastRequestId = string.Empty; RequestIdToQuery = string.Empty; OperationText = string.Empty; }
        OnPropertyChanged(nameof(HasConnectionConfiguration)); OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(TargetText)); OnPropertyChanged(nameof(CanLogin));
        ReconnectCommand?.RaiseCanExecuteChanged();
        SaveConnectionCommand?.RaiseCanExecuteChanged();
    }
    public void ReportError(Exception? error = null) => OnError(error ?? new InvalidOperationException());
    private void OnError(Exception error) => Message = error is ArgumentException ? "输入无效，请核对目标身份、路径、端口及配置版本。" :
        error is UnauthorizedAccessException ? "管理认证失败，请核对已启用的考务账号。" : "管理操作未确认，请核对连接、证书及服务器部署。";
    private void NotifyPending() { OnPropertyChanged(nameof(Pending)); OnPropertyChanged(nameof(PendingText)); UpdateCommands(); }
    private void UpdateCommands()
    {
        foreach (var command in new[] { RefreshCommand, ReconnectCommand, LogoutCommand, StartCommand, StopCommand, ChecksCommand, ConfigureCommand,
                     BackupCommand, RestoreCommand, TakeoverCommand, EmergencyCommand, RebuildCommand, ReprotectCommand,
                     QueryCommand, ResumeRestoreCommand, RetryOriginalCommand }) command?.RaiseCanExecuteChanged();
        CancelWaitCommand?.RaiseCanExecuteChanged();
        SaveConnectionCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanEnterManagementCredentials));
        OnPropertyChanged(nameof(CanConfigureConnection)); OnPropertyChanged(nameof(ConnectionStatus));
    }
    private static string SafeError(HttpStatusCode status) => status switch
    { HttpStatusCode.Conflict => "配置版本或请求内容冲突。表单已保留，请核对并重新确认", HttpStatusCode.Forbidden => "操作权限或操作者不一致",
        HttpStatusCode.NotFound => "原操作尚未找到，不能据此判定没有执行", HttpStatusCode.BadRequest => "后端拒绝输入，请核对表单",
        _ => "后端依赖不可用或操作失败，不能判定成功" };
    private static string YesNo(bool value) => value ? "已确认" : "未完成";
    private static string KindText(ManagementOperationKind kind) => kind switch { ManagementOperationKind.Start => "启动", ManagementOperationKind.Stop => "停止",
        ManagementOperationKind.Configure => "保存配置", ManagementOperationKind.Checks => "状态检测", ManagementOperationKind.Backup => "备份", ManagementOperationKind.Restore => "恢复",
        ManagementOperationKind.Takeover => "接管", ManagementOperationKind.Emergency => "进入单机应急", ManagementOperationKind.Rebuild => "重建旧节点", ManagementOperationKind.Reprotect => "恢复同步保护", _ => "未知操作" };
    private static string PhaseText(ManagementOperationPhase phase) => phase switch { ManagementOperationPhase.Completed => "完成", ManagementOperationPhase.Failed => "失败",
        ManagementOperationPhase.Accepted => "已受理", ManagementOperationPhase.Staged => "仅隔离暂存", _ => "待核对" };
    public void Dispose() { if (_disposed) return; _backupMonitoring?.Cancel(); Deactivate(); ResetSession(); _disposed = true; _lifetime.Dispose(); }
}
