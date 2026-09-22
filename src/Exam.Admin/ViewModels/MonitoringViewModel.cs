using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.Admin.ViewModels;

public sealed class MonitoringViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private readonly Dictionary<string, long> _collectionCursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenCollectionEventIds = new(StringComparer.Ordinal);
    private string _examId = string.Empty;
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
                Rows.Clear();
                CollectionEvents.Clear();
                _collectionCursors.Clear();
                _seenCollectionEventIds.Clear();
                OnPropertyChanged(nameof(FilteredRows));
                Status = "考试 ID 已变更；请重新从服务器刷新参与状态。";
                CollectionStatus = "考试 ID 已变更；请重新查询该考试的收齐事件。";
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
    public string ParticipationId { get => _participationId; set => SetProperty(ref _participationId, value); }
    public string SelectedDeviceId { get => _selectedDeviceId; set => SetProperty(ref _selectedDeviceId, value); }
    public string ExpectedVersionText { get => _expectedVersionText; set => SetProperty(ref _expectedVersionText, value); }
    public string DecisionReason { get => _decisionReason; set => SetProperty(ref _decisionReason, value); }
    public string EventId { get => _eventId; set => SetProperty(ref _eventId, value); }
    public string ResendParticipationId { get => _resendParticipationId; set => SetProperty(ref _resendParticipationId, value); }
    public string ResendDeviceId { get => _resendDeviceId; set => SetProperty(ref _resendDeviceId, value); }
    public string EntryVersionText { get => _entryVersionText; set => SetProperty(ref _entryVersionText, value); }
    public string AuthorizationVersionText { get => _authorizationVersionText; set => SetProperty(ref _authorizationVersionText, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CollectionStatus { get => _collectionStatus; private set => SetProperty(ref _collectionStatus, value); }

    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand DecideDeviceCommand { get; }
    public AsyncRelayCommand ResendCommand { get; }
    public AsyncRelayCommand RefreshCollectionEventsCommand { get; }

    public async Task RefreshCollectionEventsAsync()
    {
        var examId = ExamId.Trim();
        if (examId.Length == 0)
        {
            CollectionStatus = "请先填写考试 ID。";
            return;
        }

        var cursor = _collectionCursors.GetValueOrDefault(examId);
        var added = 0;
        for (var page = 0; page < 10; page++)
        {
            var previousCursor = cursor;
            var events = await _api.ListCollectionEventsAsync(examId, cursor);
            if (!StringComparer.Ordinal.Equals(examId, ExamId.Trim()))
            {
                return;
            }
            foreach (var item in events.OrderBy(item => item.RosterVersion))
            {
                if (item.RosterVersion <= cursor)
                {
                    continue;
                }
                if (_seenCollectionEventIds.Add(item.EventId))
                {
                    CollectionEvents.Add(item);
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
                CollectionStatus = $"本次读取 {added} 条收齐事件；已处理至名单版本 {cursor}。事件不代表成绩已开放。";
                return;
            }
            if (cursor == previousCursor)
            {
                CollectionStatus = "收齐事件分页未推进，已停止自动补取；请检查服务端返回的名单版本。";
                return;
            }
        }
        CollectionStatus = $"本次读取 {added} 条收齐事件；仍可能有后续分页，请再次查询。";
    }

    public async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = "请填写考试 ID。";
            return;
        }

        var examId = ExamId.Trim();
        IReadOnlyList<Exam.Contracts.Participation.ParticipationResult> results;
        try
        {
            results = await _api.ListActualParticipationsAsync(examId);
        }
        catch
        {
            if (StringComparer.Ordinal.Equals(examId, ExamId.Trim()))
            {
                Rows.Clear();
                OnPropertyChanged(nameof(FilteredRows));
            }
            throw;
        }
        if (!StringComparer.Ordinal.Equals(examId, ExamId.Trim()))
        {
            return;
        }
        Rows.Clear();
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

    private async Task DecideDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ParticipationId) || string.IsNullOrWhiteSpace(SelectedDeviceId) ||
            string.IsNullOrWhiteSpace(DecisionReason) || !long.TryParse(ExpectedVersionText, out var expectedVersion) || expectedVersion < 0)
        {
            Status = "设备判定需要参与 ID、选择设备、非负预期版本和原因。";
            return;
        }

        var result = await _api.DecideDeviceAsync(
            ParticipationId.Trim(), SelectedDeviceId.Trim(), expectedVersion, DecisionReason.Trim());
        Status = $"设备判定成功：授权设备 {result.AuthorizedDeviceId}，authorizationVersion={result.AuthorizationVersion}。";
        await LoadAsync();
    }

    private async Task ResendAsync()
    {
        if (string.IsNullOrWhiteSpace(EventId) || string.IsNullOrWhiteSpace(ResendParticipationId) ||
            string.IsNullOrWhiteSpace(ResendDeviceId) || !long.TryParse(EntryVersionText, out var entryVersion) || entryVersion < 0 ||
            !long.TryParse(AuthorizationVersionText, out var authorizationVersion) || authorizationVersion < 0)
        {
            Status = "补发需要事件、参与、设备 ID，以及两个非负版本号。";
            return;
        }

        var result = await _api.ResendCommandAsync(
            EventId.Trim(), ResendParticipationId.Trim(), entryVersion, ResendDeviceId.Trim(), authorizationVersion);
        Status = $"补发请求成功：事件 {result.EventId}，累计投递 {result.DeliveryAttempts} 次。";
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
