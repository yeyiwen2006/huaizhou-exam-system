using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.Admin.ViewModels;

public sealed class MonitoringViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private readonly Dictionary<string, long> _collectionCursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenCollectionEventIds = new(StringComparer.Ordinal);
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
        _contextVersion++;
        _decisionRequestVersion++;
        _resendRequestVersion++;
        Rows.Clear();
        CollectionEvents.Clear();
        _collectionCursors.Clear();
        _seenCollectionEventIds.Clear();
        SelectedRow = null;
        ParticipationId = SelectedDeviceId = ExpectedVersionText = DecisionReason = string.Empty;
        EventId = ResendParticipationId = ResendDeviceId = EntryVersionText = AuthorizationVersionText = string.Empty;
        OnPropertyChanged(nameof(FilteredRows));
        Status = "考试或连接已变更；请重新从服务器刷新参与状态。";
        CollectionStatus = "请重新查询该考试的收齐事件；历史收齐事件不代表当前名单仍已收齐。";
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
    private string _status = "考生业务状态来自正式参与接口；连接状态、当前题号和交卷采集详情等待 A/B API。";
    private string _collectionStatus = "尚未查询收齐事件；事件只对应其名单版本，不代表成绩已开放。";

    public MonitoringViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += (_, _) => ResetContext();
        LoadCommand = new AsyncRelayCommand(LoadAsync, onError: SetError);
        DecideDeviceCommand = new AsyncRelayCommand(DecideDeviceAsync, onError: SetError);
        ResendCommand = new AsyncRelayCommand(ResendAsync, onError: SetError);
        RefreshCollectionEventsCommand = new AsyncRelayCommand(RefreshCollectionEventsAsync, onError: SetCollectionError);
    }

    public ObservableCollection<MonitoringRow> Rows { get; } = [];
    public ObservableCollection<CollectionCompletedNotification> CollectionEvents { get; } = [];
    public IEnumerable<MonitoringRow> FilteredRows => Rows.Where(MatchesFilter);

    public string ExamId
    {
        get => _examId;
        set
        {
            if (SetProperty(ref _examId, value))
            {
                ResetContext();
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

    public async Task RefreshCollectionEventsAsync()
    {
        var examId = ExamId.Trim();
        if (examId.Length == 0) { CollectionStatus = "请先填写考试 ID。"; return; }
        var context = _contextVersion;
        var operation = ++_collectionRequestVersion;
        var cursor = _collectionCursors.GetValueOrDefault(examId);
        var added = 0;
        CollectionCompletedNotification? newest = null;
        CollectionStatus = "正在读取服务器持久化收齐事件…";
        try
        {
            for (var page = 0; page < 10; page++)
            {
                var previousCursor = cursor;
                var events = await _api.ListCollectionEventsAsync(examId, cursor);
                if (context != _contextVersion || operation != _collectionRequestVersion) return;
                // Validate the whole page before advancing the cursor or displaying any row.
                if (events.Any(item => !StringComparer.Ordinal.Equals(item.ExamId, examId) ||
                    string.IsNullOrWhiteSpace(item.EventId) || item.RosterVersion < 1 || item.ParticipantCount < 0) ||
                    events.Select(item => item.RosterVersion).Distinct().Count() != events.Count ||
                    events.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != events.Count)
                    throw new InvalidOperationException("收齐事件的考试、标识或名单版本无效，未应用该页结果。");
                foreach (var item in events.OrderBy(item => item.RosterVersion))
                {
                    if (item.RosterVersion <= cursor) continue;
                    if (_seenCollectionEventIds.Add(item.EventId))
                    {
                        CollectionEvents.Add(item);
                        newest = item;
                        added++;
                        if (CollectionEvents.Count > 500)
                        {
                            var oldest = CollectionEvents[0];
                            CollectionEvents.RemoveAt(0);
                            _seenCollectionEventIds.Remove(oldest.EventId);
                        }
                    }
                    cursor = item.RosterVersion;
                    _collectionCursors[examId] = cursor;
                }
                if (events.Count < 100)
                {
                    CollectionStatus = $"本次读取 {added} 条收齐事件，至名单版本 {cursor}；历史事件不保证当前名单收齐，也不代表成绩已开放。";
                    return;
                }
                if (cursor == previousCursor)
                {
                    CollectionStatus = "收齐事件分页未推进，已停止自动补取；请核对服务器返回的名单版本。";
                    return;
                }
            }
            CollectionStatus = $"本次读取 {added} 条收齐事件；仍可能有后续分页，请再次查询。";
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

    public async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请填写考试 ID。";
            return;
        }

        var examId = ExamId.Trim();
        var context = _contextVersion;
        var operation = ++_loadVersion;
        Status = "正在读取实际参与记录…";
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
        foreach (var item in results)
        {
            Rows.Add(new MonitoringRow(
                item.ParticipationId,
                item.CandidateId,
                ToChinese(item.BusinessState),
                item.AuthorizedDeviceId,
                item.EntryVersion,
                item.AuthorizationVersion,
                item.Version,
                item.BusinessState == ParticipationBusinessState.Submitted ? "已提交" : "未提交",
                "接口未提供",
                "接口未提供"));
        }
        OnPropertyChanged(nameof(FilteredRows));
        Status = $"已从正式接口加载 {Rows.Count} 条实际参与记录。";
    }

    public async Task DecideDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ParticipationId) || string.IsNullOrWhiteSpace(SelectedDeviceId) ||
            string.IsNullOrWhiteSpace(DecisionReason) || !long.TryParse(ExpectedVersionText, out var expectedVersion) || expectedVersion < 0)
        {
            Status = "设备判定需要参与 ID、选择设备、非负预期版本和原因。";
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
        var success = $"设备裁决已保存：授权设备 {result.AuthorizedDeviceId}，撤销设备 {result.RevokedDeviceId}；操作者 {result.OperatorId}，原因：{result.Reason}。";
        try
        {
            await LoadAsync();
            if (context == _contextVersion && operation == _decisionRequestVersion && inputVersion == _decisionInputVersion)
                Status = success;
        }
        catch (Exception exception)
        {
            if (context == _contextVersion && operation == _decisionRequestVersion && inputVersion == _decisionInputVersion)
                Status = $"{success} 但监测刷新失败：{exception.Message}。请刷新，不要重复裁决。";
        }
    }

    public async Task ResendAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId) || string.IsNullOrWhiteSpace(EventId) || string.IsNullOrWhiteSpace(ResendParticipationId) ||
            string.IsNullOrWhiteSpace(ResendDeviceId) || !long.TryParse(EntryVersionText, out var entryVersion) || entryVersion < 0 ||
            !long.TryParse(AuthorizationVersionText, out var authorizationVersion) || authorizationVersion < 0)
        {
            Status = "补发需要考试、事件、参与、设备 ID，以及两个非负版本号。";
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
        Status = $"补发请求成功：事件 {result.EventId}，累计投递 {result.DeliveryAttempts} 次。";
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

    private void SetError(Exception exception) => Status = $"操作失败：{exception.Message}";

    private void SetCollectionError(Exception exception) =>
        CollectionStatus = exception is AdminApiException apiError
            ? $"收齐事件查询失败（{apiError.Code}）：{apiError.Message}"
            : $"收齐事件查询失败：{exception.Message}";
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
    string ConnectionState,
    string CurrentQuestion);
