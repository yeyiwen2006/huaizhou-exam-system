using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.Admin.ViewModels;

public sealed class MonitoringViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private readonly TimeSpan _collectionPollInterval;
    private CancellationTokenSource? _collectionPolling;
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
    public event Action<CollectionCompletedNotification>? CollectionCompleted;

    public MonitoringRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value) && value is not null)
            {
                ParticipationId = value.ParticipationId;
                SelectedDeviceId = value.AuthorizedDeviceId;
                ExpectedVersionText = value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ResendParticipationId = value.ParticipationId;
                ResendDeviceId = value.AuthorizedDeviceId;
                EntryVersionText = value.EntryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AuthorizationVersionText = value.AuthorizationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    private void ResetContext()
    {
        StopCollectionPolling();
        _contextVersion++;
        _decisionRequestVersion++;
        _resendRequestVersion++;
        Rows.Clear();
        CollectionEvents.Clear();
        _collectionCursors.Clear();
        _seenCollectionEventVersions.Clear();
        SelectedRow = null;
        ParticipationId = SelectedDeviceId = ExpectedVersionText = DecisionReason = string.Empty;
        EventId = ResendParticipationId = ResendDeviceId = EntryVersionText = AuthorizationVersionText = string.Empty;
        OnPropertyChanged(nameof(FilteredRows));
        Status = "考试或服务器已切换，请点击“刷新”。";
        CollectionStatus = "打开本页时会自动查询收齐记录，也可以点击“查询收齐记录”。";
    }
    private string _filterText = string.Empty;
    private bool _onlySubmitted;
    private string _participationId = string.Empty;
    private string _selectedDeviceId = string.Empty;
    private string _expectedVersionText = string.Empty;
    private string _decisionReason = string.Empty;
    private string _eventId = string.Empty;
    private string _resendParticipationId = string.Empty;
    private string _resendDeviceId = string.Empty;
    private string _entryVersionText = string.Empty;
    private string _authorizationVersionText = string.Empty;
    private string _status = "填写考试编号后点击“刷新”，查看实际参试考生。";
    private string _collectionStatus = "尚未查询收齐记录。";

    public MonitoringViewModel(AdminApiClient api, TimeSpan? collectionPollInterval = null)
    {
        _api = api;
        _collectionPollInterval = collectionPollInterval ?? TimeSpan.FromSeconds(20);
        if (_collectionPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(collectionPollInterval));
        _api.ConfigurationChanged += OnConfigurationChanged;
        LoadCommand = new AsyncRelayCommand(LoadAsync, onError: SetError);
        DecideDeviceCommand = new AsyncRelayCommand(DecideDeviceAsync, onError: SetError);
        ResendCommand = new AsyncRelayCommand(ResendAsync, onError: SetError);
        RefreshCollectionEventsCommand = new AsyncRelayCommand(() => RefreshCollectionEventsAsync(), onError: SetCollectionError);
    }

    public ObservableCollection<MonitoringRow> Rows { get; } = [];
    public ObservableCollection<CollectionCompletedNotification> CollectionEvents { get; } = [];
    public IEnumerable<MonitoringRow> FilteredRows => Rows.Where(MatchesFilter);

    public bool IsPageActive
    {
        get => _isPageActive;
        set { if (SetProperty(ref _isPageActive, value)) UpdateCollectionPolling(); }
    }

    public bool ConnectionReady
    {
        get => _connectionReady;
        set { if (SetProperty(ref _connectionReady, value)) UpdateCollectionPolling(); }
    }

    public string ExamId
    {
        get => _examId;
        set
        {
            if (SetProperty(ref _examId, value))
            {
                ResetContext();
                UpdateCollectionPolling();
            }
        }
    }
    public string FilterText
    {
        get => _filterText;
        set { if (SetProperty(ref _filterText, value)) OnPropertyChanged(nameof(FilteredRows)); }
    }
    public bool OnlySubmitted
    {
        get => _onlySubmitted;
        set { if (SetProperty(ref _onlySubmitted, value)) OnPropertyChanged(nameof(FilteredRows)); }
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
    public string CollectionStatus { get => _collectionStatus; private set => SetProperty(ref _collectionStatus, value); }

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand DecideDeviceCommand { get; }
    public AsyncRelayCommand ResendCommand { get; }
    public AsyncRelayCommand RefreshCollectionEventsCommand { get; }

    public async Task RefreshCollectionEventsAsync(CancellationToken cancellationToken = default)
    {
        var examId = ExamId.Trim();
        if (examId.Length == 0) { CollectionStatus = "请先填写考试编号。"; return; }
        var context = _contextVersion;
        var operation = ++_collectionRequestVersion;
        var cursor = _collectionCursors.GetValueOrDefault(examId);
        var added = 0;
        CollectionCompletedNotification? newest = null;
        CollectionStatus = "正在查询收齐记录…";
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
                        if (reminded.Remember(item)) newest = item;
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
            if (newest is not null && context == _contextVersion && operation == _collectionRequestVersion)
                CollectionCompleted?.Invoke(newest);
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

    private void UpdateCollectionPolling()
    {
        StopCollectionPolling();
        if (_disposed || !IsPageActive || !ConnectionReady || string.IsNullOrWhiteSpace(ExamId)) return;
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
        _api.ConfigurationChanged -= OnConfigurationChanged;
    }

    public async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请填写考试编号。";
            return;
        }

        var examId = ExamId.Trim();
        var context = _contextVersion;
        var operation = ++_loadVersion;
        Status = "正在读取参试考生…";
        IReadOnlyList<Exam.Contracts.Participation.ParticipationResult> results;
        try
        {
            results = await _api.ListActualParticipationsAsync(examId);
        }
        catch
        {
            if (context == _contextVersion && operation == _loadVersion)
            {
                Rows.Clear();
                OnPropertyChanged(nameof(FilteredRows));
                throw;
            }
            return;
        }
        if (context != _contextVersion || operation != _loadVersion)
        {
            return;
        }
        ValidateParticipations(results, examId);
        Rows.Clear();
        SelectedRow = null;

        // B06 失焦与复核监测：与参试列表合并展示；查询失败不阻止主列表，显示占位并提示。
        var (focusResult, focusError) = await LoadFocusSummariesAsync(examId, context, operation);
        var focusByParticipation = focusResult ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var reviewByParticipation = await LoadReviewStatesAsync(examId, context, operation) ?? [];
        foreach (var item in results)
        {
            focusByParticipation.TryGetValue(item.ParticipationId, out var focusSummary);
            reviewByParticipation.TryGetValue(item.ParticipationId, out var reviewState);
            Rows.Add(new MonitoringRow(
                item.ParticipationId,
                item.CandidateId,
                ToChinese(item.BusinessState),
                item.AuthorizedDeviceId,
                item.EntryVersion,
                item.AuthorizationVersion,
                item.Version,
                item.BusinessState == ParticipationBusinessState.Submitted ? "已提交" : "未提交",
                focusSummary ?? "暂无记录",
                reviewState ?? "—"));
        }
        OnPropertyChanged(nameof(FilteredRows));
        Status = focusError is null
            ? $"共 {Rows.Count} 名实际参试考生。"
            : $"共 {Rows.Count} 名实际参试考生；{focusError}";
    }

    /// <summary>失焦监测汇总；网络或权限异常时返回错误说明，不阻塞参试列表。</summary>
    private async Task<(Dictionary<string, string>? Map, string? Error)> LoadFocusSummariesAsync(
        string examId, long context, long operation)
    {
        try
        {
            var summaries = await _api.ListFocusSummariesAsync(examId);
            if (context != _contextVersion || operation != _loadVersion) return (null, null);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var summary in summaries)
            {
                if (summary is null || !StringComparer.Ordinal.Equals(summary.ExamId, examId) ||
                    string.IsNullOrWhiteSpace(summary.ParticipationId) || summary.EventCount < 0 ||
                    summary.TotalDurationMilliseconds < 0) continue;
                map[summary.ParticipationId] = summary.EventCount == 0 ? "0 次" :
                    $"{summary.EventCount} 次 / 共 {FormatDuration(summary.TotalDurationMilliseconds)}";
            }
            return (map, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, $"失焦监测查询失败：{FriendlyErrors.Describe(exception)}");
        }
    }

    /// <summary>修订复核状态；考生尚未进入修订流程时显示“—”。</summary>
    private async Task<Dictionary<string, string>?> LoadReviewStatesAsync(string examId, long context, long operation)
    {
        try
        {
            var snapshot = await _api.GetExamAsync(examId);
            if (context != _contextVersion || operation != _loadVersion) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (snapshot.Participants is not null)
            {
                foreach (var (participantId, status) in snapshot.Participants)
                {
                    if (status is null || string.IsNullOrWhiteSpace(participantId)) continue;
                    map[participantId] = status.PaperUpdateState switch
                    {
                        Exam.Contracts.Versioning.PaperUpdateState.ReviewRequired => "未复核",
                        Exam.Contracts.Versioning.PaperUpdateState.Updated => "已复核",
                        Exam.Contracts.Versioning.PaperUpdateState.DownloadFailed => "修订未安装",
                        _ => "—",
                    };
                }
            }
            return map;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 复核状态属于增强信息，失败不阻塞监测列表。
            return null;
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours} 时 {time.Minutes} 分 {time.Seconds} 秒"
            : time.TotalMinutes >= 1
                ? $"{(int)time.TotalMinutes} 分 {time.Seconds} 秒"
                : $"{time.Seconds} 秒";
    }

    public async Task DecideDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ParticipationId) || string.IsNullOrWhiteSpace(SelectedDeviceId) ||
            string.IsNullOrWhiteSpace(DecisionReason) || !long.TryParse(ExpectedVersionText, out var expectedVersion) || expectedVersion < 0)
        {
            Status = "请先在上表选中考生，再填写要保留的设备和原因。";
            return;
        }

        var participationId = ParticipationId.Trim();
        var selectedDeviceId = SelectedDeviceId.Trim();
        var reason = DecisionReason.Trim();
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
        var success = $"已保留设备 {result.AuthorizedDeviceId} 继续答题，设备 {result.RevokedDeviceId} 已停止答题（操作人 {result.OperatorId}，原因：{result.Reason}）。";
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

    public async Task ResendAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId) || string.IsNullOrWhiteSpace(EventId) || string.IsNullOrWhiteSpace(ResendParticipationId) ||
            string.IsNullOrWhiteSpace(ResendDeviceId) || !long.TryParse(EntryVersionText, out var entryVersion) || entryVersion < 0 ||
            !long.TryParse(AuthorizationVersionText, out var authorizationVersion) || authorizationVersion < 0)
        {
            Status = "请先在上表选中考生，并填写要重发的指令编号。";
            return;
        }

        var examId = ExamId.Trim();
        var eventId = EventId.Trim();
        var participationId = ResendParticipationId.Trim();
        var deviceId = ResendDeviceId.Trim();
        var context = _contextVersion;
        var operation = ++_resendRequestVersion;
        var inputVersion = _resendInputVersion;
        Exam.Contracts.Commands.CommandEventView result;
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
        Status = $"已重发指令（累计发送 {result.DeliveryAttempts} 次）。";
    }

    private static void ValidateParticipations(
        IReadOnlyList<Exam.Contracts.Participation.ParticipationResult> results,
        string examId)
    {
        if (results.Any(item => item is null || !StringComparer.Ordinal.Equals(item.ExamId, examId) ||
            string.IsNullOrWhiteSpace(item.ParticipationId) || string.IsNullOrWhiteSpace(item.CandidateId) ||
            string.IsNullOrWhiteSpace(item.AuthorizedDeviceId) || item.EntryVersion < 1 ||
            item.AuthorizationVersion < 1 || item.Version < 1 || !Enum.IsDefined(item.BusinessState) ||
            item.BusinessState == ParticipationBusinessState.NotParticipating ||
            item.Lifecycle is { } lifecycle && !Enum.IsDefined(lifecycle)) ||
            results.Select(item => item.ParticipationId).Distinct(StringComparer.Ordinal).Count() != results.Count ||
            results.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count() != results.Count)
            throw new InvalidOperationException("参与记录的考试、标识、版本或状态无效，已拒绝显示。");
    }

    private bool MatchesFilter(MonitoringRow row)
    {
        if (OnlySubmitted && row.CollectionState != "已提交")
        {
            return false;
        }
        var filter = FilterText.Trim();
        return filter.Length == 0 || row.CandidateId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.ParticipationId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               row.AuthorizedDeviceId.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToChinese(ParticipationBusinessState state) => state switch
    {
        ParticipationBusinessState.NotParticipating => "未参加",
        ParticipationBusinessState.Waiting => "候考",
        ParticipationBusinessState.Answering => "作答中",
        ParticipationBusinessState.Submitted => "已交卷",
        _ => state.ToString()
    };

    private void SetError(Exception exception) => Status = $"操作失败：{FriendlyErrors.Describe(exception)}";

    private void SetCollectionError(Exception exception) =>
        CollectionStatus = $"收齐记录查询失败：{FriendlyErrors.Describe(exception)}";
}

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
    string PaperReviewState);
