using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Commands;
using Exam.Contracts.Versioning;

namespace Exam.Admin.ViewModels;

public sealed partial class MonitoringViewModel
{
    private CancellationTokenSource? _snapshotPolling;
    private DateTimeOffset _snapshotAsOf;
    private string _snapshotStatus = "尚未读取实时状态。";
    private string _roomFilter = "全部试场";
    private ConflictDevice? _selectedConflictDevice;
    private bool _applyingSnapshot;
    private CreateCommandEventRequest? _pendingUpload;
    private long _pendingUploadContext = -1;
    private ExamLifecycle _snapshotLifecycle;

    public string SnapshotStatus { get => _snapshotStatus; private set => SetProperty(ref _snapshotStatus, value); }
    public ObservableCollection<string> RoomFilters { get; } = ["全部试场"];
    public string RoomFilter
    {
        get => _roomFilter;
        set { if (SetProperty(ref _roomFilter, value ?? "全部试场")) OnPropertyChanged(nameof(FilteredRows)); }
    }
    public MonitoringParticipant? SelectedDetails => SelectedRow?.Details;
    public IEnumerable<ConflictDeviceView> ConflictDevices => SelectedDetails?.ConflictDevices.Select(device =>
        new ConflictDeviceView(device, device.ClientNumber, device.RoomGroupName, ConnectionText(device.ConnectionState),
            device.IsAuthorized ? "当前授权" : "申请进入", device.Page,
            device.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无已保存进度",
            $"已保存 {device.SavedAnswerCount} 题答案")) ?? [];
    public IEnumerable<AnswerDetail> AnswerDetails => SelectedDetails?.Answers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new AnswerDetail(pair.Key, pair.Value.Kind switch
        {
            AnswerKind.Empty => "未作答", AnswerKind.Abstain => "不作答",
            _ => string.Join("、", pair.Value.OptionIds)
        })) ?? [];
    public IEnumerable<CommandDetail> CommandDetails => SelectedDetails?.Commands.Select(command =>
        new CommandDetail(command.EventType switch
        {
            CommandEventType.Start => "开始考试", CommandEventType.End => "结束考试",
            CommandEventType.ScheduleChanged => "调整时间", CommandEventType.PaperUpdated => "更新试卷",
            CommandEventType.DeviceDecided => "设备裁决", _ => "其他通知"
        }, command.Confirmation, command.CreatedAt.ToLocalTime(), command.AcknowledgedAt?.ToLocalTime())) ?? [];
    public ConflictDevice? SelectedConflictDevice
    {
        get => _selectedConflictDevice;
        set
        {
            if (SetProperty(ref _selectedConflictDevice, value) && value is not null)
                SelectedDeviceId = value.DeviceId;
        }
    }

    private void UpdateSnapshotPolling()
    {
        StopSnapshotPolling();
        if (_disposed || !IsPageActive || !ConnectionReady || string.IsNullOrWhiteSpace(ExamId)) return;
        var source = new CancellationTokenSource();
        _snapshotPolling = source;
        _ = PollSnapshotsAsync(source);
    }

    private void StopSnapshotPolling()
    {
        var source = _snapshotPolling;
        _snapshotPolling = null;
        _loadVersion++;
        source?.Cancel();
    }

    private async Task PollSnapshotsAsync(CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                try { await LoadSnapshotAsync(source.Token); }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (!source.IsCancellationRequested && ReferenceEquals(_snapshotPolling, source))
                        SnapshotStatus = $"连接中断，显示上次读取的状态：{FriendlyErrors.Describe(error)}";
                }
            } while (await timer.WaitForNextTickAsync(source.Token));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_snapshotPolling, source)) _snapshotPolling = null;
            source.Dispose();
        }
    }

    public async Task LoadSnapshotAsync(CancellationToken ct = default)
    {
        var examId = ExamId.Trim();
        if (examId.Length == 0) { Status = "请先打开一场考试。"; return; }
        var context = _contextVersion;
        var operation = ++_loadVersion;
        var snapshot = await _api.GetMonitoringSnapshotAsync(examId, ct);
        if (ct.IsCancellationRequested || context != _contextVersion || operation != _loadVersion || _disposed) return;
        ValidateSnapshot(snapshot);
        if (snapshot.AsOf < _snapshotAsOf) return;
        _snapshotAsOf = snapshot.AsOf;
        _snapshotLifecycle = snapshot.Lifecycle;
        var selectedId = SelectedRow?.ParticipationId;
        var chosenDevice = SelectedConflictDevice?.DeviceId;
        var rows = snapshot.Participants.Select(ToRow).ToArray();
        // Rebind details but retain the operator's target/reason while newer progress arrives.
        // Changed authorization/version still invalidates an outstanding decision response.
        _applyingSnapshot = true;
        try
        {
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            SelectedRow = rows.SingleOrDefault(row => row.ParticipationId == selectedId);
            SelectedConflictDevice = SelectedDetails?.ConflictDevices.SingleOrDefault(device => device.DeviceId == chosenDevice);
        }
        finally { _applyingSnapshot = false; }
        var rooms = new[] { "全部试场" }.Concat(rows.Select(row => row.RoomGroupName).Distinct().Order(StringComparer.CurrentCulture)).ToArray();
        if (!RoomFilters.SequenceEqual(rooms))
        {
            var selectedRoom = RoomFilter;
            RoomFilters.Clear();
            foreach (var room in rooms) RoomFilters.Add(room);
            RoomFilter = RoomFilters.Contains(selectedRoom) ? selectedRoom : "全部试场";
            OnPropertyChanged(nameof(RoomFilter));
        }
        OnPropertyChanged(nameof(FilteredRows));
        SnapshotStatus = $"状态更新时间：{snapshot.AsOf.ToLocalTime():HH:mm:ss}；本页每秒读取一次。";
        Status = $"实际参试 {snapshot.ActualCount} 人，已回收 {snapshot.CollectedCount} 份，待回收 {snapshot.ActualCount - snapshot.CollectedCount} 份。";
    }

    private static void ValidateSnapshot(MonitoringSnapshot snapshot)
    {
        var rows = snapshot.Participants;
        if (snapshot.AsOf == default || rows.Any(row => row is null) || snapshot.ActualCount != rows.Count ||
            snapshot.CollectedCount != rows.Count(row => row.ReceiptId is not null) ||
            rows.Select(row => row.ParticipationId).Distinct(StringComparer.Ordinal).Count() != rows.Count ||
            rows.Select(row => row.CandidateId).Distinct(StringComparer.Ordinal).Count() != rows.Count ||
            rows.Any(row => string.IsNullOrWhiteSpace(row.ParticipationId) || string.IsNullOrWhiteSpace(row.CandidateId) ||
                string.IsNullOrWhiteSpace(row.AuthorizedDeviceId) || string.IsNullOrWhiteSpace(row.Name) ||
                string.IsNullOrWhiteSpace(row.ClientNumber) || row.EntryVersion < 1 || row.AuthorizationVersion < 1 ||
                row.ParticipationVersion < 1 || row.AnswerVersion < 0 || row.InstalledPaperVersion < 0 ||
                row.TargetPaperVersion < 0 || !Enum.IsDefined(row.BusinessState) || !Enum.IsDefined(row.ConnectionState) ||
                row.BusinessState == ParticipationBusinessState.NotParticipating || row.Answers is null ||
                row.Commands is null || row.ConflictDevices is null ||
                row.Commands.Any(command => string.IsNullOrWhiteSpace(command.EventId) || !Enum.IsDefined(command.EventType)) ||
                row.ConflictDevices.Any(device => string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.ClientNumber)) ||
                row.ConflictDevices.Select(device => device.DeviceId).Distinct(StringComparer.Ordinal).Count() != row.ConflictDevices.Count))
            throw new InvalidOperationException("监控快照的考生、授权、版本或回收人数无效，已拒绝显示。");
    }

    private static MonitoringRow ToRow(MonitoringParticipant item) => new(
        item.ParticipationId, item.CandidateId, ToChinese(item.BusinessState), item.AuthorizedDeviceId,
        item.EntryVersion, item.AuthorizationVersion, item.ParticipationVersion, item.CollectionState,
        $"{item.FocusLossCount} 次 / 共 {FormatDuration(item.FocusLossMilliseconds)}",
        item.RevisionStatus?.PaperUpdateState switch
        {
            PaperUpdateState.ReviewRequired => "未复核", PaperUpdateState.Updated => "已复核",
            PaperUpdateState.DownloadFailed => "修订未安装", _ => "—"
        })
    {
        Name = item.Name, ClassText = item.ClassText, AdmissionNo = item.AdmissionNo,
        ClientNumber = item.ClientNumber, RoomGroupName = item.RoomGroupName,
        ConnectionText = ConnectionText(item.ConnectionState), Page = $"{ToChinese(item.BusinessState)} · {item.Page}",
        CurrentQuestion = item.CurrentQuestionId ?? "—",
        LastCommunicationText = item.LastCommunicationAt?.ToLocalTime().ToString("HH:mm:ss") ?? "未通信",
        PaperStatus = $"{item.InstalledPaperVersion}/{item.TargetPaperVersion} · {item.DownloadState}",
        ConflictText = item.ConflictDevices.Count > 1 ? "待处理" : "无", Details = item
    };

    public static string ConnectionText(DeviceConnectionState state) => state switch
    {
        DeviceConnectionState.Online => "在线", DeviceConnectionState.Offline => "离线",
        DeviceConnectionState.Recovering => "恢复连接中", _ => "未报告"
    };

    public async Task ResendSelectedAsync(CommandEventType eventType)
    {
        var row = SelectedRow;
        var command = row?.Details?.Commands.Where(item => item.EventType == eventType && item.CanResend)
            .OrderByDescending(item => item.StateVersion).FirstOrDefault();
        if (row is null || command is null)
        {
            Status = "请先选中考生；当前没有可重发的有效指令。";
            return;
        }
        EventId = command.EventId;
        await ResendAsync();
    }

    public async Task DecideSelectedDeviceAsync()
    {
        if (SelectedDetails is not { } details || SelectedConflictDevice is not { } selected ||
            details.BusinessState == ParticipationBusinessState.Submitted ||
            !details.ConflictDevices.Any(device => device.DeviceId == selected.DeviceId))
        {
            Status = "请先选择发生冲突的考生，再从设备对照中选择要保留的学生端。";
            return;
        }
        await DecideDeviceAsync();
    }

    public async Task RequestUploadAsync()
    {
        var row = SelectedRow;
        if (row?.Details is not { } details || details.ReceiptId is not null ||
            _snapshotLifecycle != ExamLifecycle.Ended && details.BusinessState != ParticipationBusinessState.Submitted)
        {
            Status = "请选中考试已结束或已交卷但尚未回收的考生；正在作答和已回收的答卷无需补传。";
            return;
        }
        var context = _contextVersion;
        if (_pendingUpload is null || _pendingUploadContext != context ||
            _pendingUpload.ParticipationId != row.ParticipationId || _pendingUpload.EntryVersion != row.EntryVersion ||
            _pendingUpload.TargetDeviceId != row.AuthorizedDeviceId || _pendingUpload.TargetAuthorizationVersion != row.AuthorizationVersion)
        {
            _pendingUploadContext = context;
            _pendingUpload = new CreateCommandEventRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                ExamId, row.ParticipationId, row.EntryVersion, CommandEventType.UploadRequested, row.Version,
                row.AuthorizedDeviceId, row.AuthorizationVersion, null, null);
        }
        var request = _pendingUpload;
        var result = await _api.RequestUploadAsync(request);
        if (context != _contextVersion || SelectedRow?.ParticipationId != row.ParticipationId ||
            SelectedRow?.AuthorizationVersion != row.AuthorizationVersion) return;
        if (result.EventId != request.EventId || result.EventType != CommandEventType.UploadRequested ||
            result.ExamId != request.ExamId || result.ParticipationId != request.ParticipationId ||
            result.EntryVersion != request.EntryVersion || result.TargetDeviceId != request.TargetDeviceId ||
            result.TargetAuthorizationVersion != request.TargetAuthorizationVersion)
            throw new InvalidOperationException("补传请求回执与当前考生或授权不匹配。");
        _pendingUpload = null;
        Status = "补传请求已保存，学生端将重试上传原固定答卷；回收完成以服务器答卷回执为准。";
    }
}

public sealed record ConflictDeviceView(ConflictDevice Device, string ClientNumber, string RoomGroupName,
    string Connection, string Authorization, string Page, string LastSync, string Progress);
public sealed record AnswerDetail(string Question, string Answer);
public sealed record CommandDetail(string Action, string Confirmation, DateTimeOffset SentAt, DateTimeOffset? ConfirmedAt);
