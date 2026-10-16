using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 考试监控。参试列表、字段、回收人数与未回收名单全部来自服务器一次事务内的权威聚合快照
/// （<c>GET /exams/{examId}/monitoring</c>），不再由考务端拼接多个来源或使用占位值。
/// 收齐记录仍按历史补取轮询，用于页面重开与断线恢复。
/// </summary>
public sealed class MonitoringViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private readonly TimeSpan _collectionPollInterval;
    private readonly TimeSpan _monitoringRefreshInterval;
    private CancellationTokenSource? _collectionPolling;
    private CancellationTokenSource? _monitoringRefresh;
    /// <summary>状态栏仍是占位说明（尚未读到数据），首次成功读取后换成计数汇总。</summary>
    private bool _statusIsPlaceholder = true;
    private bool _isPageActive;
    private bool _connectionReady;
    private bool _disposed;
    private readonly Dictionary<string, long> _collectionCursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _seenCollectionEventVersions = new(StringComparer.Ordinal);
    // Keep reminder history across exam switches and reconnects, but isolate different logical servers.
    private readonly Dictionary<(string ServerId, string ExamId), CollectionEventHistory> _remindedCollectionEvents = [];
    private string _examId = string.Empty;
    private long _contextVersion;
    private long _loadVersion;
    private long _collectionRequestVersion;
    private long _decisionRequestVersion;
    private long _resendRequestVersion;
    private long _decisionInputVersion;
    private long _resendInputVersion;
    private MonitoringRow? _selectedRow;
    private bool _applyingSnapshot;
    private int _actualCount;
    private int _collectedCount;
    private DateTimeOffset? _asOf;
    private ExamLifecycle _lifecycle = ExamLifecycle.Scheduled;
    public event Action<CollectionCompletedNotification>? CollectionCompleted;

    /// <summary>操作者选中的行；自动刷新重建行对象时由 <see cref="SelectRow"/> 安静地换回同一名考生。</summary>
    public MonitoringRow? SelectedRow
    {
        get => _selectedRow;
        set { if (!_applyingSnapshot) SelectRow(value, fillOperatorInputs: true); }
    }
    public MonitoringRow? SelectedTableRow
    {
        get => _selectedRow is { } row && MatchesFilter(row) ? row : null;
        set { if (!_applyingSnapshot && (value is not null || SelectedTableRow is not null)) SelectedRow = value; }
    }
    public MonitoringRow? SelectedUncollectedRow
    {
        get => _selectedRow is { } row && UncollectedRows.Contains(row) ? row : null;
        set { if (!_applyingSnapshot && (value is not null || SelectedUncollectedRow is not null)) SelectedRow = value; }
    }

    /// <summary>
    /// 切换选中行。只有操作者主动选中（<paramref name="fillOperatorInputs"/>）才改写裁决与重发输入，
    /// 并使早先发出的请求按既有规则失效；自动刷新重建行时只换实例，不动操作者已填写的内容，
    /// 否则每 0.75 秒一次的刷新会把选中和正在进行的操作抹掉。
    /// </summary>
    private void SelectRow(MonitoringRow? row, bool fillOperatorInputs)
    {
        var previous = _selectedRow;
        // Selection belongs to an item instance, not record value equality.
        if (ReferenceEquals(previous, row)) return;
        _selectedRow = row;
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(SelectedTableRow));
        OnPropertyChanged(nameof(SelectedUncollectedRow));
        var selectedEventId = _selectedCommand?.Command.EventId;
        var selectedConflictDeviceId = _selectedConflictDevice?.DeviceId;
        SelectedCommands.Clear();
        SelectedConflictDevices.Clear();
        if (row is null)
        {
            // 该考生已经不在本次读取的参试列表里：裁决与重发的输入必须一起清掉，
            // 否则界面上没有可选项、命令却仍然“已武装”，会向已不可见的考生发出请求。
            ParticipationId = SelectedDeviceId = ExpectedVersionText = string.Empty;
            ResendParticipationId = ResendDeviceId = EntryVersionText = AuthorizationVersionText = string.Empty;
            EventId = string.Empty;
            SetProperty(ref _selectedCommand, null, nameof(SelectedCommand));
            SetProperty(ref _selectedConflictDevice, null, nameof(SelectedConflictDevice));
            return;
        }
        foreach (var command in row.AvailableCommands)
            SelectedCommands.Add(new MonitoringCommandRow(command, item => SelectedCommand = item));
        foreach (var conflict in row.ConflictingDevices)
            SelectedConflictDevices.Add(new ConflictDeviceRow(conflict, item => SelectedConflictDevice = item));
        if (!fillOperatorInputs)
        {
            // 自动刷新只换了行对象：把操作者已选中的指令与对照设备按标识选回新实例，
            // 否则下拉框与对照表会变空，而 EventId/SelectedDeviceId 仍是“已武装”状态。
            RestoreSelection(selectedEventId, selectedConflictDeviceId);
            if (previous is not null && (previous.Version != row.Version || previous.AuthorizationVersion != row.AuthorizationVersion ||
                previous.EntryVersion != row.EntryVersion || previous.AuthorizedDeviceId != row.AuthorizedDeviceId))
            {
                // 授权或业务版本改变后须重新核对设备，不能把旧选择套到新的授权上。
                SelectedDeviceId = ExpectedVersionText = EventId = string.Empty;
                SetProperty(ref _selectedConflictDevice, null, nameof(SelectedConflictDevice));
                SetProperty(ref _selectedCommand, null, nameof(SelectedCommand));
                foreach (var item in SelectedCommands) item.IsSelected = false;
                foreach (var item in SelectedConflictDevices) item.IsSelected = false;
            }
            return;
        }
        _resendInputVersion++;
        ParticipationId = row.ParticipationId;
        SelectedDeviceId = row.AuthorizedDeviceId;
        ExpectedVersionText = row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ResendParticipationId = row.ParticipationId;
        ResendDeviceId = row.AuthorizedDeviceId;
        EntryVersionText = row.EntryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AuthorizationVersionText = row.AuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EventId = string.Empty;
        SelectedCommand = SelectedCommands.FirstOrDefault(command => command.CanResend) ?? SelectedCommands.FirstOrDefault();
    }

    /// <summary>安静地选回指令与对照设备：不写 EventId/SelectedDeviceId，也不使在途请求失效。</summary>
    private void RestoreSelection(string? eventId, string? conflictDeviceId)
    {
        var command = eventId is null
            ? null
            : SelectedCommands.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Command.EventId, eventId));
        if (!ReferenceEquals(_selectedCommand, command))
        {
            SetProperty(ref _selectedCommand, command, nameof(SelectedCommand));
            foreach (var item in SelectedCommands) item.IsSelected = ReferenceEquals(item, command);
        }
        var device = conflictDeviceId is null
            ? null
            : SelectedConflictDevices.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.DeviceId, conflictDeviceId));
        if (!ReferenceEquals(_selectedConflictDevice, device))
        {
            SetProperty(ref _selectedConflictDevice, device, nameof(SelectedConflictDevice));
            foreach (var item in SelectedConflictDevices) item.IsSelected = ReferenceEquals(item, device);
        }
        if (eventId is not null && command?.CanResend != true) EventId = string.Empty;
        if (conflictDeviceId is not null && device is null) SelectedDeviceId = string.Empty;
    }

    private void ResetContext()
    {
        StopCollectionPolling();
        StopMonitoringRefresh();
        _contextVersion++;
        _decisionRequestVersion++;
        _resendRequestVersion++;
        Rows.Clear();
        UncollectedRows.Clear();
        SelectedCommands.Clear();
        SelectedConflictDevices.Clear();
        CollectionEvents.Clear();
        _collectionCursors.Clear();
        _seenCollectionEventVersions.Clear();
        // 上一场考试的筛选条件对新考试没有意义，且试场可能根本不存在。
        ClearFilters();
        _actualCount = 0;
        _collectedCount = 0;
        _asOf = null;
        _lifecycle = ExamLifecycle.Scheduled;
        SelectedRow = null;
        ParticipationId = SelectedDeviceId = ExpectedVersionText = DecisionReason = string.Empty;
        EventId = ResendParticipationId = ResendDeviceId = EntryVersionText = AuthorizationVersionText = string.Empty;
        OnPropertyChanged(nameof(FilteredRows));
        OnPropertyChanged(nameof(RoomGroupOptions));
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(AsOfText));
        Status = "考试或服务器已切换，请点击“刷新”。";
        _statusIsPlaceholder = true;
        RefreshNote = string.Empty;
        CollectionStatus = "打开本页时会查询收齐记录；连接期间每 1 秒自动补取，离开本页也会继续提醒。";
    }
    private string _filterText = string.Empty;
    private string _roomGroupFilter = string.Empty;
    private string _clientNumberFilter = string.Empty;
    private string _nameFilter = string.Empty;
    private string _classFilter = string.Empty;
    private string _admissionNoFilter = string.Empty;
    private bool _onlyUncollected;
    private string _participationId = string.Empty;
    private string _selectedDeviceId = string.Empty;
    private string _expectedVersionText = string.Empty;
    private string _decisionReason = string.Empty;
    private string _eventId = string.Empty;
    private string _resendParticipationId = string.Empty;
    private string _resendDeviceId = string.Empty;
    private string _entryVersionText = string.Empty;
    private string _authorizationVersionText = string.Empty;
    private string _status = "选择考试后点击“刷新”，查看实际参加考试考生的在线、作答和回收状态。";
    private string _refreshNote = string.Empty;
    private string _collectionStatus = "尚未查询收齐记录。";
    private MonitoringCommandRow? _selectedCommand;
    private ConflictDeviceRow? _selectedConflictDevice;

    public MonitoringViewModel(AdminApiClient api, TimeSpan? collectionPollInterval = null,
        TimeSpan? monitoringRefreshInterval = null)
    {
        _api = api;
        _collectionPollInterval = collectionPollInterval ?? TimeSpan.FromSeconds(1);
        if (_collectionPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(collectionPollInterval));
        // 权威状态自动刷新：需求要求正常联网时监测滞后不超过 2 秒；刷新周期可注入，便于回归与替换实现。
        _monitoringRefreshInterval = monitoringRefreshInterval ?? TimeSpan.FromMilliseconds(750);
        if (_monitoringRefreshInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(monitoringRefreshInterval));
        _api.ConfigurationChanged += OnConfigurationChanged;
        LoadCommand = new AsyncRelayCommand(LoadAsync, onError: SetError);
        DecideDeviceCommand = new AsyncRelayCommand(DecideDeviceAsync, onError: SetError);
        ResendCommand = new AsyncRelayCommand(ResendAsync, onError: SetError);
        RequestReuploadCommand = new AsyncRelayCommand(RequestReuploadAsync, onError: SetError);
        RefreshCollectionEventsCommand = new AsyncRelayCommand(() => RefreshCollectionEventsAsync(), onError: SetCollectionError);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public ObservableCollection<MonitoringRow> Rows { get; } = [];
    public ObservableCollection<MonitoringRow> UncollectedRows { get; } = [];
    public ObservableCollection<MonitoringCommandRow> SelectedCommands { get; } = [];
    public ObservableCollection<ConflictDeviceRow> SelectedConflictDevices { get; } = [];
    public ObservableCollection<CollectionCompletedNotification> CollectionEvents { get; } = [];

    /// <summary>按现场使用的信息筛选：试场、学生端编号、姓名、班级、准考证号，可组合。</summary>
    public IEnumerable<MonitoringRow> FilteredRows => Rows.Where(MatchesFilter);
    private void RefreshFilteredRows()
    {
        if (_selectedRow is { } row && !MatchesFilter(row) && !UncollectedRows.Contains(row)) SelectedRow = null;
        OnPropertyChanged(nameof(FilteredRows));
        OnPropertyChanged(nameof(SelectedTableRow));
    }

    public IReadOnlyList<string> RoomGroupOptions =>
        Rows.Select(row => row.RoomGroup).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public string CountsText => $"实际参加考试 {_actualCount} 人；已回收 {_collectedCount} 份；未回收 {_actualCount - _collectedCount} 份。";

    public string AsOfText => _asOf is { } asOf
        ? $"状态读取时间 {asOf:yyyy-MM-dd HH:mm:ss}（考试状态：{LifecycleText}）。"
        : string.Empty;

    private string LifecycleText => _lifecycle switch
    {
        ExamLifecycle.Scheduled => "未开始",
        ExamLifecycle.Running => "进行中",
        ExamLifecycle.Ended => "已结束",
        _ => "未知"
    };

    public bool IsPageActive
    {
        get => _isPageActive;
        set { if (SetProperty(ref _isPageActive, value)) UpdateMonitoringRefresh(); }
    }

    public bool ConnectionReady
    {
        get => _connectionReady;
        set { if (SetProperty(ref _connectionReady, value)) UpdatePolling(); }
    }

    public string ExamId
    {
        get => _examId;
        set
        {
            if (SetProperty(ref _examId, value))
            {
                ResetContext();
                UpdatePolling();
            }
        }
    }

    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value ?? string.Empty)) RefreshFilteredRows(); }
    }
    public string RoomGroupFilter
    {
        get => _roomGroupFilter;
        set { if (SetProperty(ref _roomGroupFilter, value ?? string.Empty)) RefreshFilteredRows(); }
    }
    public string ClientNumberFilter
    {
        get => _clientNumberFilter;
        set { if (SetProperty(ref _clientNumberFilter, value ?? string.Empty)) RefreshFilteredRows(); }
    }
    public string NameFilter
    {
        get => _nameFilter;
        set { if (SetProperty(ref _nameFilter, value ?? string.Empty)) RefreshFilteredRows(); }
    }
    public string ClassFilter
    {
        get => _classFilter;
        set { if (SetProperty(ref _classFilter, value ?? string.Empty)) RefreshFilteredRows(); }
    }
    public string AdmissionNoFilter
    {
        get => _admissionNoFilter;
        set { if (SetProperty(ref _admissionNoFilter, value ?? string.Empty)) RefreshFilteredRows(); }
    }
    /// <summary>只看未回收：对应回收统计中的未回收名单。</summary>
    public bool OnlyUncollected
    {
        get => _onlyUncollected;
        set { if (SetProperty(ref _onlyUncollected, value)) RefreshFilteredRows(); }
    }
    public string ParticipationId { get => _participationId; set { if (SetProperty(ref _participationId, value)) _decisionInputVersion++; } }
    public string SelectedDeviceId { get => _selectedDeviceId; set { if (SetProperty(ref _selectedDeviceId, value)) _decisionInputVersion++; } }
    public string ExpectedVersionText { get => _expectedVersionText; set { if (SetProperty(ref _expectedVersionText, value)) _decisionInputVersion++; } }
    public string DecisionReason { get => _decisionReason; set { if (SetProperty(ref _decisionReason, value)) _decisionInputVersion++; } }
    public string EventId { get => _eventId; set { if (SetProperty(ref _eventId, value)) _resendInputVersion++; } }
    public string ResendParticipationId { get => _resendParticipationId; set { if (SetProperty(ref _resendParticipationId, value)) _resendInputVersion++; } }
    public string ResendDeviceId { get => _resendDeviceId; set { if (SetProperty(ref _resendDeviceId, value)) _resendInputVersion++; } }
    public string EntryVersionText { get => _entryVersionText; set { if (SetProperty(ref _entryVersionText, value)) _resendInputVersion++; } }
    public string AuthorizationVersionText { get => _authorizationVersionText; set { if (SetProperty(ref _authorizationVersionText, value)) _resendInputVersion++; } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>
    /// 数据新鲜度提示（自动刷新失败、被拒绝的快照保留上一次结果等），与 <see cref="Status"/> 分开：
    /// 状态栏只放考务的操作反馈与手动刷新汇总，自动刷新不得覆盖它。
    /// </summary>
    public string RefreshNote { get => _refreshNote; private set => SetProperty(ref _refreshNote, value); }
    public string CollectionStatus { get => _collectionStatus; private set => SetProperty(ref _collectionStatus, value); }

    /// <summary>选中考生后可重发的权威指令；重发不再需要手填事件标识。</summary>
    public MonitoringCommandRow? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (!SetProperty(ref _selectedCommand, value)) return;
            if (value is not null)
            {
                EventId = value.Command.EventId;
                ResendParticipationId = _selectedRow?.ParticipationId ?? string.Empty;
                ResendDeviceId = _selectedRow?.AuthorizedDeviceId ?? string.Empty;
                EntryVersionText = _selectedRow?.EntryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? EntryVersionText;
                AuthorizationVersionText = _selectedRow?.AuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? AuthorizationVersionText;
            }
            foreach (var command in SelectedCommands) command.IsSelected = ReferenceEquals(command, value);
        }
    }

    /// <summary>冲突对照中选中的设备：选中即填入“保留的设备”，核对后再确认。</summary>
    public ConflictDeviceRow? SelectedConflictDevice
    {
        get => _selectedConflictDevice;
        set
        {
            if (!SetProperty(ref _selectedConflictDevice, value)) return;
            if (value is not null)
            {
                SelectedDeviceId = value.DeviceId;
                ExpectedVersionText = _selectedRow?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
            foreach (var device in SelectedConflictDevices) device.IsSelected = ReferenceEquals(device, value);
        }
    }

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand DecideDeviceCommand { get; }
    public AsyncRelayCommand ResendCommand { get; }
    public AsyncRelayCommand RequestReuploadCommand { get; }
    public AsyncRelayCommand RefreshCollectionEventsCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>清除全部筛选条件：试场、编号、姓名、班级、准考证号和未回收过滤。</summary>
    private void ClearFilters()
    {
        RoomGroupFilter = string.Empty;
        ClientNumberFilter = string.Empty;
        NameFilter = string.Empty;
        ClassFilter = string.Empty;
        AdmissionNoFilter = string.Empty;
        FilterText = string.Empty;
        OnlyUncollected = false;
    }

    public async Task RefreshCollectionEventsAsync(CancellationToken cancellationToken = default)
    {
        var examId = ExamId.Trim();
        if (examId.Length == 0) { CollectionStatus = "请先选择考试。"; return; }
        var context = _contextVersion;
        var operation = ++_collectionRequestVersion;
        var cursor = _collectionCursors.GetValueOrDefault(examId);
        var added = 0;
        var notifications = new List<CollectionCompletedNotification>();
        // 保留上次结果，避免每秒轮询时在“查询中”和结果之间反复切换。
        try
        {
            for (var page = 0; page < 10; page++)
            {
                var previousCursor = cursor;
                var events = await _api.ListCollectionEventsAsync(examId, cursor, cancellationToken);
                if (context != _contextVersion || operation != _collectionRequestVersion) return;
                var serverId = _api.ServerId ?? throw new InvalidOperationException("收齐事件响应缺少服务器身份。");
                var reminderKey = (serverId, examId);
                if (!_remindedCollectionEvents.TryGetValue(reminderKey, out var reminded))
                {
                    reminded = new CollectionEventHistory();
                    _remindedCollectionEvents.Add(reminderKey, reminded);
                }
                // Validate the whole page before advancing the cursor or displaying any row.
                if (events.Any(item => item is null || !StringComparer.Ordinal.Equals(item.ExamId, examId) ||
                    string.IsNullOrWhiteSpace(item.EventId) || item.RosterVersion < 1 ||
                    item.ParticipantCount <= 0 || item.CompletedAt == default) ||
                    events.GroupBy(item => item.RosterVersion).Any(group => group.Distinct().Count() != 1) ||
                    events.GroupBy(item => item.EventId, StringComparer.Ordinal).Any(group => group.Distinct().Count() != 1) ||
                    events.Any(item => _seenCollectionEventVersions.TryGetValue(item.EventId, out var seenVersion) &&
                        seenVersion != item.RosterVersion) ||
                    events.Any(reminded.Conflicts))
                    throw new InvalidOperationException("收齐事件的考试、标识或名单版本无效，未应用该页结果。");
                foreach (var item in events.Distinct().OrderBy(item => item.RosterVersion))
                {
                    if (item.RosterVersion <= cursor) continue;
                    if (_seenCollectionEventVersions.TryAdd(item.EventId, item.RosterVersion))
                    {
                        CollectionEvents.Add(item);
                        if (reminded.Remember(item)) notifications.Add(item);
                        added++;
                        if (CollectionEvents.Count > 500)
                        {
                            CollectionEvents.RemoveAt(0);
                        }
                    }
                    cursor = item.RosterVersion;
                    _collectionCursors[examId] = cursor;
                }
                if (events.Count < 100)
                {
                    CollectionStatus = $"新增 {added} 条收齐记录（页面保留最近 500 条）。收齐记录只说明当时的名单已全部交卷，之后有考生加入时会重新统计。";
                    return;
                }
                if (cursor == previousCursor)
                {
                    throw new InvalidOperationException("收齐事件分页未推进；请核对服务器返回的名单版本。");
                }
            }
            CollectionStatus = $"新增 {added} 条收齐记录，还有更多记录，稍后会自动继续读取。";
        }
        catch when (context != _contextVersion || operation != _collectionRequestVersion)
        {
            // A -> B -> A switches are different contexts, even when the text is the same.
        }
        finally
        {
            if (context == _contextVersion && operation == _collectionRequestVersion)
                foreach (var item in notifications) CollectionCompleted?.Invoke(item);
        }
    }

    private sealed class CollectionEventHistory
    {
        private readonly Dictionary<string, CollectionCompletedNotification> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _idByVersion = [];

        public bool Conflicts(CollectionCompletedNotification item) =>
            (_byId.TryGetValue(item.EventId, out var previous) && previous != item) ||
            (_idByVersion.TryGetValue(item.RosterVersion, out var id) &&
                !StringComparer.Ordinal.Equals(id, item.EventId));

        public bool Remember(CollectionCompletedNotification item)
        {
            if (!_byId.TryAdd(item.EventId, item)) return false;
            _idByVersion.Add(item.RosterVersion, item.EventId);
            return true;
        }
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        ConnectionReady = false;
        ResetContext();
    }

    private void UpdatePolling()
    {
        UpdateCollectionPolling();
        UpdateMonitoringRefresh();
    }

    /// <summary>只在监测页处于前台且连接就绪时自动刷新权威状态。</summary>
    private void UpdateMonitoringRefresh()
    {
        StopMonitoringRefresh();
        if (_disposed || !IsPageActive || !ConnectionReady || string.IsNullOrWhiteSpace(ExamId)) return;
        var source = new CancellationTokenSource();
        _monitoringRefresh = source;
        _ = PollMonitoringAsync(source);
    }

    private void StopMonitoringRefresh()
    {
        var source = _monitoringRefresh;
        _monitoringRefresh = null;
        source?.Cancel();
    }

    private async Task PollMonitoringAsync(CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(_monitoringRefreshInterval);
            do
            {
                try
                {
                    // 进入页面立即读一次，随后按周期刷新；失败保留上一次可信结果。
                    await RefreshFromSnapshotAsync(source.Token);
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (!source.IsCancellationRequested && ReferenceEquals(_monitoringRefresh, source))
                        ReportAutoRefreshFailure(exception);
                }
            }
            while (await timer.WaitForNextTickAsync(source.Token));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // 离开页面、切换考试或关闭窗口会停止这条循环。
        }
        finally
        {
            if (ReferenceEquals(_monitoringRefresh, source)) _monitoringRefresh = null;
            source.Dispose();
        }
    }

    /// <summary>
    /// 自动刷新：失败时保留上一次可信结果并标注数据可能过期，不清空列表、不覆盖考务的操作反馈。
    /// 手动“刷新”仍按 A 的既有语义处理（传输失败清空列表）。
    /// </summary>
    private async Task RefreshFromSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPageActive || !ConnectionReady) return;
        var examId = ExamId.Trim();
        if (examId.Length == 0) return;
        var context = _contextVersion;
        var snapshot = await _api.GetMonitoringSnapshotAsync(examId, cancellationToken);
        if (context != _contextVersion || !IsPageActive) return;
        Apply(snapshot);
        RefreshNote = string.Empty;
        AdoptPlaceholderStatus();
    }

    /// <summary>读到数据后把“请点击刷新”这类占位说明换成计数汇总（只做一次，不覆盖操作反馈）。</summary>
    private void AdoptPlaceholderStatus()
    {
        if (!_statusIsPlaceholder) return;
        _statusIsPlaceholder = false;
        Status = Summarize();
    }

    private void ReportAutoRefreshFailure(Exception exception)
    {
        // 只写数据新鲜度提示：状态栏留给考务的操作反馈，自动刷新不覆盖它。
        RefreshNote = _asOf is { } asOf
            ? $"自动刷新失败：{FriendlyErrors.Describe(exception)}当前显示的是 {asOf:HH:mm:ss} 读取的状态，请核对网络后重试。"
            : $"自动刷新失败：{FriendlyErrors.Describe(exception)}";
    }

    /// <summary>
    /// 收齐记录轮询：只要连接就绪且已选考试就运行，**不依赖监测页是否在前台**——
    /// 需求要求“全部答卷持久保存后 GUI 弹出”，考务在任意页面都必须收到提醒；
    /// 权威状态的实时刷新仍只在监测页前台进行（见 <see cref="UpdateMonitoringRefresh"/>）。
    /// </summary>
    private void UpdateCollectionPolling()
    {
        StopCollectionPolling();
        if (_disposed || !ConnectionReady || string.IsNullOrWhiteSpace(ExamId)) return;
        var source = new CancellationTokenSource();
        _collectionPolling = source;
        _ = PollCollectionEventsAsync(source);
    }

    private void StopCollectionPolling()
    {
        var source = _collectionPolling;
        _collectionPolling = null;
        _collectionRequestVersion++;
        source?.Cancel();
    }

    private async Task PollCollectionEventsAsync(CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(_collectionPollInterval);
            do
            {
                try
                {
                    await RefreshCollectionEventsAsync(source.Token);
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (!source.IsCancellationRequested && ReferenceEquals(_collectionPolling, source))
                        SetCollectionError(exception);
                }
            }
            while (await timer.WaitForNextTickAsync(source.Token));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Leaving the page, changing exams/connections and closing the window stop this loop.
        }
        finally
        {
            if (ReferenceEquals(_collectionPolling, source)) _collectionPolling = null;
            source.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCollectionPolling();
        StopMonitoringRefresh();
        _api.ConfigurationChanged -= OnConfigurationChanged;
    }

    public async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请先选择考试。";
            return;
        }

        var examId = ExamId.Trim();
        var context = _contextVersion;
        var operation = ++_loadVersion;
        Status = "正在读取考试监测状态…";
        MonitoringSnapshot snapshot;
        try
        {
            snapshot = await _api.GetMonitoringSnapshotAsync(examId);
        }
        catch (AdminApiException exception) when (exception.Code == "INVALID_RESPONSE")
        {
            // 快照被边界校验拒绝：保留上一次可信结果，不显示半份状态。
            // 说明写进新鲜度提示，这样命令的错误处理不会把它覆盖掉。
            if (context == _contextVersion && operation == _loadVersion)
                RefreshNote = $"监测状态读取失败：{FriendlyErrors.Describe(exception)}已保留上一次结果，可稍后重试。";
            throw;
        }
        catch
        {
            if (context == _contextVersion && operation == _loadVersion)
            {
                ClearLoadedRows();
                throw;
            }
            return;
        }
        if (context != _contextVersion || operation != _loadVersion)
        {
            return;
        }
        Apply(snapshot);
        RefreshNote = string.Empty;
        _statusIsPlaceholder = false;
        Status = Summarize();
    }

    private void ClearLoadedRows()
    {
        RefreshNote = string.Empty;
        Rows.Clear();
        UncollectedRows.Clear();
        _actualCount = 0;
        _collectedCount = 0;
        _asOf = null;
        SelectedRow = null;
        OnPropertyChanged(nameof(FilteredRows));
        OnPropertyChanged(nameof(RoomGroupOptions));
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(AsOfText));
    }

    private readonly object _snapshotSync = new();

    private void Apply(MonitoringSnapshot snapshot)
    {
        // Manual and periodic responses may complete on different threads in a non-UI host.
        lock (_snapshotSync) ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        // 手动读取与周期读取可能并行；较早开始的响应不得把新状态覆盖回去。
        if (_asOf is { } current && snapshot.AsOf < current) return;
        _applyingSnapshot = true;
        try
        {
        var selectedId = _selectedRow?.ParticipationId;
        var previousRows = Rows.ToDictionary(row => row.ParticipationId, StringComparer.Ordinal);
        Rows.Clear();
        UncollectedRows.Clear();
        var byId = new Dictionary<string, MonitoringRow>(StringComparer.Ordinal);
        foreach (var participant in snapshot.Participants)
        {
            var row = MonitoringRow.From(participant);
            if (previousRows.TryGetValue(row.ParticipationId, out var previousRow) && previousRow.HasSameContent(row))
                row = previousRow;
            Rows.Add(row);
            byId[row.ParticipationId] = row;
        }
        // 未回收名单与参试列表是同一批行对象：在任一表格中选中都作用于同一名考生。
        foreach (var participant in snapshot.Uncollected)
        {
            if (byId.TryGetValue(participant.ParticipationId, out var row)) UncollectedRows.Add(row);
        }
        _actualCount = snapshot.ActualCount;
        _collectedCount = snapshot.CollectedCount;
        _asOf = snapshot.AsOf;
        _lifecycle = snapshot.Lifecycle;
        // 自动刷新会重建行对象：安静地选回同一名考生，不覆盖操作者正在填写的裁决与重发输入。
        SelectRow(selectedId is not null && byId.TryGetValue(selectedId, out var restored) ? restored : null,
            fillOperatorInputs: false);
        OnPropertyChanged(nameof(FilteredRows));
        OnPropertyChanged(nameof(RoomGroupOptions));
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(AsOfText));
        }
        finally { _applyingSnapshot = false; }
        OnPropertyChanged(nameof(SelectedTableRow));
        OnPropertyChanged(nameof(SelectedUncollectedRow));
    }

    /// <summary>本次读取的汇总文案；只由手动刷新写入状态栏，自动刷新不覆盖考务的操作反馈。</summary>
    private string Summarize() => _actualCount == 0
        ? "本场考试还没有实际参加考试考生。"
        : $"共 {_actualCount} 名实际参加考试考生；已回收 {_collectedCount} 份；未回收 {_actualCount - _collectedCount} 份。";

    public async Task DecideDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ParticipationId) || string.IsNullOrWhiteSpace(SelectedDeviceId) ||
            !long.TryParse(ExpectedVersionText, out var expectedVersion) || expectedVersion < 0)
        {
            Status = "请先在上表选中考生，再在冲突对照中选择要保留的设备。";
            return;
        }

        var participationId = ParticipationId.Trim();
        var selectedDeviceId = SelectedDeviceId.Trim();
        var reason = DecisionReason?.Trim() ?? string.Empty;
        var operatorId = _api.OperatorId;
        var context = _contextVersion;
        var operation = ++_decisionRequestVersion;
        var inputVersion = _decisionInputVersion;
        DeviceDecisionResult result;
        try
        {
            result = await _api.DecideDeviceAsync(participationId, selectedDeviceId, expectedVersion, reason);
        }
        catch when (context != _contextVersion || operation != _decisionRequestVersion || inputVersion != _decisionInputVersion)
        {
            return;
        }
        if (context != _contextVersion || operation != _decisionRequestVersion || inputVersion != _decisionInputVersion) return;
        if (!StringComparer.Ordinal.Equals(result.ParticipationId, participationId) ||
            !StringComparer.Ordinal.Equals(result.AuthorizedDeviceId, selectedDeviceId) ||
            string.IsNullOrWhiteSpace(result.RevokedDeviceId) || result.AuthorizationVersion < 1 ||
            result.Version <= expectedVersion || result.DecidedAt == default ||
            StringComparer.Ordinal.Equals(result.AuthorizedDeviceId, result.RevokedDeviceId) ||
            !StringComparer.Ordinal.Equals(result.OperatorId, operatorId) ||
            !StringComparer.Ordinal.Equals(result.Reason, reason))
            throw new InvalidOperationException("设备裁决回执与当前请求不匹配，已拒绝应用。");
        ExpectedVersionText = result.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        inputVersion = _decisionInputVersion;
        var retainedNumber = SelectedRow?.ConflictingDevices.FirstOrDefault(device => device.DeviceId == result.AuthorizedDeviceId)?.ClientNumber;
        var success = $"已保留设备{(retainedNumber is null ? string.Empty : " " + retainedNumber)}继续答题，另一设备已停止答题。";
        if (result.Reason.Length > 0) success += $"备注：{result.Reason}";
        try
        {
            await LoadAsync();
            if (context == _contextVersion && operation == _decisionRequestVersion && inputVersion == _decisionInputVersion)
                Status = success;
        }
        catch (Exception exception)
        {
            if (context == _contextVersion && operation == _decisionRequestVersion && inputVersion == _decisionInputVersion)
                Status = $"{success}但列表刷新失败：{FriendlyErrors.Describe(exception)}请点击“刷新”，不要重复操作。";
        }
    }

    /// <summary>未回收名单的补传入口：重发该考生最近一条仍可重发的结束指令（原答卷补传）。</summary>
    public async Task RequestReuploadAsync()
    {
        var row = SelectedRow;
        if (row is null)
        {
            Status = "请先在未回收名单中选中要补传的考生。";
            return;
        }
        var command = row.AvailableCommands.FirstOrDefault(item => item.EventType == CommandEventType.End && item.CanResend);
        if (command is null)
        {
            // 说出真实原因：答卷已回收同样会让结束指令不可重发，别把考务引到“指令状态”上去找。
            if (row.CollectionState == "已回收")
            {
                Status = "该考生的答卷已经回收，无需请求补传。";
                return;
            }
            var known = row.AvailableCommands.FirstOrDefault(item => item.EventType == CommandEventType.End);
            Status = known is null
                ? "该考生还没有结束指令，无法请求补传。"
                : "该结束指令已确认或已被拒绝，不能重复请求补传。";
            return;
        }
        EventId = command.EventId;
        ResendParticipationId = row.ParticipationId;
        ResendDeviceId = row.AuthorizedDeviceId;
        EntryVersionText = row.EntryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AuthorizationVersionText = row.AuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ResendAsync();
    }

    public async Task ResendAsync()
    {
        if (_selectedRow is { } row && (!row.AvailableCommands.Any(c => c.EventId == EventId && c.CanResend) ||
            row.ParticipationId != ResendParticipationId || row.AuthorizedDeviceId != ResendDeviceId))
        {
            Status = "所选指令或设备状态已变化，请重新选择可重发的指令。";
            return;
        }
        if (string.IsNullOrWhiteSpace(ExamId) || string.IsNullOrWhiteSpace(EventId) || string.IsNullOrWhiteSpace(ResendParticipationId) ||
            string.IsNullOrWhiteSpace(ResendDeviceId) || !long.TryParse(EntryVersionText, out var entryVersion) || entryVersion < 0 ||
            !long.TryParse(AuthorizationVersionText, out var authorizationVersion) || authorizationVersion < 0)
        {
            Status = "请先在上表选中考生，再选择要重发的指令。";
            return;
        }

        var examId = ExamId.Trim();
        var eventId = EventId.Trim();
        var participationId = ResendParticipationId.Trim();
        var deviceId = ResendDeviceId.Trim();
        var context = _contextVersion;
        var operation = ++_resendRequestVersion;
        var inputVersion = _resendInputVersion;
        CommandEventView result;
        try
        {
            result = await _api.ResendCommandAsync(eventId, participationId, entryVersion, deviceId, authorizationVersion);
        }
        catch when (context != _contextVersion || operation != _resendRequestVersion || inputVersion != _resendInputVersion)
        {
            return;
        }
        if (context != _contextVersion || operation != _resendRequestVersion || inputVersion != _resendInputVersion) return;
        if (!StringComparer.Ordinal.Equals(result.EventId, eventId) ||
            !StringComparer.Ordinal.Equals(result.ExamId, examId) ||
            !StringComparer.Ordinal.Equals(result.ParticipationId, participationId) || result.EntryVersion != entryVersion ||
            !StringComparer.Ordinal.Equals(result.TargetDeviceId, deviceId) ||
            result.TargetAuthorizationVersion != authorizationVersion || result.DeliveryAttempts < 1)
            throw new InvalidOperationException("补发回执与当前考试或请求不匹配，已拒绝应用。");
        Status = $"已重发指令，等待学生端确认（累计发送 {result.DeliveryAttempts} 次）。";
    }

    private bool MatchesFilter(MonitoringRow row)
    {
        // “未回收”与回收统计、未回收名单同口径：已回收＝已有持久化回执，因此“回收中”也属于未回收。
        if (OnlyUncollected && row.CollectionState == "已回收")
        {
            return false;
        }
        var roomGroup = RoomGroupFilter.Trim();
        if (roomGroup.Length != 0 && !StringComparer.Ordinal.Equals(row.RoomGroup, roomGroup))
        {
            return false;
        }
        return Matches(row.ClientNumber, ClientNumberFilter) && Matches(row.Name, NameFilter) &&
               Matches(row.ClassText, ClassFilter) && Matches(row.AdmissionNo, AdmissionNoFilter) &&
               MatchesAny(row, FilterText);

        static bool Matches(string value, string filter)
        {
            // 只输了空白的筛选按“未筛选”处理，不能悄悄匹配全部仍然算作“已筛选”。
            var trimmed = filter.Trim();
            return trimmed.Length == 0 || value.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool MatchesAny(MonitoringRow row, string filter)
    {
        var trimmed = filter.Trim();
        return trimmed.Length == 0 ||
            new[] { row.Name, row.ClassText, row.AdmissionNo, row.ClientNumber, row.RoomGroup }
                .Any(value => value.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private void SetError(Exception exception) => Status = $"操作失败：{FriendlyErrors.Describe(exception)}";

    private void SetCollectionError(Exception exception) =>
        CollectionStatus = $"收齐记录查询失败：{FriendlyErrors.Describe(exception)}";
}

/// <summary>监测列表的一行；字段全部来自服务器权威快照，业务状态与连接状态分开显示。</summary>
public sealed record MonitoringRow(
    string ParticipationId,
    string CandidateId,
    string BusinessState,
    string AuthorizedDeviceId,
    long EntryVersion,
    long AuthorizationVersion,
    long Version,
    string CollectionState,
    string FocusSummary,
    string PaperReviewState,
    string Name = "",
    string ClassText = "",
    string AdmissionNo = "",
    string ClientNumber = "",
    string RoomGroup = "未确定试场",
    string ConnectionState = "未登录",
    string Page = "未报告",
    string QuestionAnswer = "—",
    string PaperState = "—",
    string CommandSummary = "暂无指令",
    string LastCommunicationText = "无通信记录",
    string ReceivedText = "—",
    IReadOnlyList<MonitoringCommand>? Commands = null,
    IReadOnlyList<ConflictDevice>? ConflictDevices = null)
{
    public IReadOnlyList<MonitoringCommand> AvailableCommands => Commands ?? [];
    public IReadOnlyList<ConflictDevice> ConflictingDevices => ConflictDevices ?? [];

    internal bool HasSameContent(MonitoringRow other) =>
        (this with { Commands = other.Commands, ConflictDevices = other.ConflictDevices }) == other &&
        AvailableCommands.SequenceEqual(other.AvailableCommands) &&
        ConflictingDevices.SequenceEqual(other.ConflictingDevices);

    public static MonitoringRow From(MonitoringParticipant participant) => new(
        participant.ParticipationId,
        participant.CandidateId,
        MonitoringDisplay.BusinessState(participant.BusinessState),
        participant.AuthorizedDeviceId,
        participant.EntryVersion,
        participant.AuthorizationVersion,
        participant.ParticipationVersion,
        participant.CollectionState,
        MonitoringDisplay.FocusSummary(participant.FocusLossCount, participant.FocusLossMilliseconds),
        MonitoringDisplay.PaperReviewState(participant.RevisionStatus),
        participant.Name,
        participant.ClassText,
        participant.AdmissionNo,
        participant.ClientNumber,
        participant.RoomGroupId is null || string.IsNullOrWhiteSpace(participant.RoomGroupName) ? "未确定试场" : participant.RoomGroupName,
        MonitoringDisplay.ConnectionState(participant.ConnectionState),
        string.IsNullOrWhiteSpace(participant.Page) ? "未报告" : participant.Page,
        MonitoringDisplay.CurrentAnswer(participant.CurrentQuestionId, participant.Answers, participant.CurrentQuestionNo, participant.CurrentOptionIds),
        MonitoringDisplay.PaperState(participant.InstalledPaperVersion, participant.TargetPaperVersion, participant.DownloadState),
        MonitoringDisplay.CommandSummary(participant.Commands),
        MonitoringDisplay.LastCommunication(participant.LastCommunicationAt),
        MonitoringDisplay.ReceivedAt(participant.ReceivedAt),
        participant.Commands,
        participant.ConflictDevices);
}

/// <summary>可重发的权威指令，供界面按名称选择，不再手填事件标识。</summary>
public sealed class MonitoringCommandRow(MonitoringCommand command, Action<MonitoringCommandRow> select) : BindableBase
{
    private bool _isSelected;
    public MonitoringCommand Command { get; } = command;
    public Action<MonitoringCommandRow> Select { get; } = select;
    public bool CanResend => Command.CanResend;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string DisplayText => $"{MonitoringDisplay.CommandType(Command.EventType)}" +
        (Command.AcknowledgedAt is { } acknowledged
            ? $"（{Command.Confirmation}，{acknowledged:HH:mm:ss}）"
            : $"（{Command.Confirmation}）") +
        (Command.CanResend ? "｜可重发" : "｜不可重发");
}

/// <summary>冲突考生的两台设备对照信息，用于选择保留哪一台。</summary>
public sealed class ConflictDeviceRow(ConflictDevice device, Action<ConflictDeviceRow> select) : BindableBase
{
    private bool _isSelected;
    public ConflictDevice Device { get; } = device;
    public Action<ConflictDeviceRow> Select { get; } = select;
    public string DeviceId => Device.DeviceId;
    public string ClientNumber => Device.ClientNumber;
    public string RoomGroupName => Device.RoomGroupName == "未分组" ? "未确定试场" : Device.RoomGroupName;
    public string ConnectionState => MonitoringDisplay.ConnectionState(Device.ConnectionState);
    public string AuthorizationText => Device.IsAuthorized ? "当前授权" : "等待裁决";
    public string LastCommunicationText => MonitoringDisplay.LastCommunication(Device.LastCommunicationAt);
    public string LastSyncedText => Device.LastSyncedAt is { } at ? MonitoringDisplay.LastCommunication(at) : "无保存记录";
    public string ProgressText => Device.LastSyncedAt is null ? "无已同步答案｜" + Device.Page : $"已保存 {Device.SavedAnswerCount} 题｜{Device.Page}";
    public string DiagnosticText => $"答案存储版本：{Device.AnswerVersion}";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
