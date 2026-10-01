using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Activation;

namespace Exam.Admin.ViewModels;

public sealed class ActivationRoomViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private string _newGroupName = string.Empty;
    private string _selectedGroupName = string.Empty;
    private RoomGroupDraft? _selectedGroup;
    private string _activationRequestId = string.Empty;
    private string _approvalRoomGroupId = string.Empty;
    private string _queryDeviceId = string.Empty;
    private string _status = "设备提出申请后将自动出现在待审批列表；选中一行后点击审批即可批准。";
    private ActivationPendingRow? _selectedPendingRequest;
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;

    public ActivationRoomViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += (_, _) =>
        {
            PendingRequests.Clear();
            Devices.Clear();
            ActivationRequestId = string.Empty;
            ApprovalRoomGroupId = string.Empty;
            QueryDeviceId = string.Empty;
            Status = "已切换服务器，之前的查询结果和审批输入已清除。";
            StopPolling();
        };
        AddGroupCommand = new RelayCommand(AddGroup);
        RenameGroupCommand = new RelayCommand(RenameGroup, () => SelectedGroup is not null);
        RemoveGroupCommand = new RelayCommand(RemoveGroup, () => SelectedGroup is not null);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, onError: SetError);
        QueryDeviceCommand = new AsyncRelayCommand(QueryDeviceAsync, onError: SetError);
        RefreshPendingCommand = new AsyncRelayCommand(RefreshPendingAsync, onError: SetError);
    }

    public ObservableCollection<ActivationPendingRow> PendingRequests { get; } = [];
    public ObservableCollection<RoomGroupDraft> Groups { get; } = [];
    public ObservableCollection<ActivationDeviceRow> Devices { get; } = [];

    public string NewGroupName { get => _newGroupName; set => SetProperty(ref _newGroupName, value); }
    public string SelectedGroupName { get => _selectedGroupName; set => SetProperty(ref _selectedGroupName, value); }

    public RoomGroupDraft? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                SelectedGroupName = value?.Name ?? string.Empty;
                RemoveGroupCommand.RaiseCanExecuteChanged();
                RenameGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>选中待审批列表中某行时，自动填入激活申请编号。</summary>
    public ActivationPendingRow? SelectedPendingRequest
    {
        get => _selectedPendingRequest;
        set
        {
            if (SetProperty(ref _selectedPendingRequest, value) && value is not null)
            {
                ActivationRequestId = value.ActivationRequestId;
            }
        }
    }

    public string ActivationRequestId { get => _activationRequestId; set => SetProperty(ref _activationRequestId, value); }
    public string ApprovalRoomGroupId { get => _approvalRoomGroupId; set => SetProperty(ref _approvalRoomGroupId, value); }
    public string QueryDeviceId { get => _queryDeviceId; set => SetProperty(ref _queryDeviceId, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public RelayCommand AddGroupCommand { get; }
    public RelayCommand RenameGroupCommand { get; }
    public RelayCommand RemoveGroupCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand QueryDeviceCommand { get; }
    public AsyncRelayCommand RefreshPendingCommand { get; }

    /// <summary>切换到激活页时调用，启动 30 秒轮询并立即刷新一次。</summary>
    public void StartPolling()
    {
        if (_pollCts is not null) return;
        _pollCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCts.Token);
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        // 立即刷新一次。
        await SafeRefreshPendingAsync(cancellationToken);

        _pollTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await _pollTimer.WaitForNextTickAsync(cancellationToken))
            {
                await SafeRefreshPendingAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SafeRefreshPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _api.ListPendingActivationRequestsAsync(cancellationToken);
            SyncPendingList(list);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 轮询失败不打扰用户，静默忽略；手动刷新时才显示错误。
        }
    }

    private async Task RefreshPendingAsync()
    {
        var list = await _api.ListPendingActivationRequestsAsync();
        SyncPendingList(list);
        Status = $"已刷新，当前 {PendingRequests.Count} 条待审批申请。";
    }

    /// <summary>以申请编号为键做增量同步，避免选中项因全量替换而丢失。</summary>
    private void SyncPendingList(IReadOnlyList<ActivationRequestResult> incoming)
    {
        var incomingIds = incoming.Select(r => r.ActivationRequestId).ToHashSet(StringComparer.Ordinal);

        // 移除已不在 pending 列表中的行。
        for (var i = PendingRequests.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(PendingRequests[i].ActivationRequestId))
                PendingRequests.RemoveAt(i);
        }

        // 追加新出现的申请，保持按申请时间顺序。
        var existingIds = PendingRequests.Select(r => r.ActivationRequestId).ToHashSet(StringComparer.Ordinal);
        foreach (var r in incoming)
        {
            if (!existingIds.Contains(r.ActivationRequestId))
                PendingRequests.Add(new ActivationPendingRow(
                    r.ActivationRequestId,
                    r.DeviceId,
                    r.RequestedAt.ToLocalTime().ToString("MM-dd HH:mm:ss")));
        }

        // 若选中行已从 pending 中消失（已被批准），清空选中。
        if (_selectedPendingRequest is not null &&
            !incomingIds.Contains(_selectedPendingRequest.ActivationRequestId))
        {
            SelectedPendingRequest = null;
        }
    }

    private void AddGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0)
        {
            Status = "分组名称不能为空。";
            return;
        }
        if (Groups.Any(group => StringComparer.Ordinal.Equals(group.Name, name)))
        {
            Status = "分组名称不能重复。";
            return;
        }

        var group = new RoomGroupDraft(Guid.NewGuid().ToString("N"), name);
        Groups.Add(group);
        SelectedGroup = group;
        NewGroupName = string.Empty;
        Status = "已添加分组（仅在本机，尚未保存到服务器）。";
    }

    private void RemoveGroup()
    {
        if (SelectedGroup is null) return;
        if (Devices.Any(device => StringComparer.Ordinal.Equals(device.RoomGroupId, SelectedGroup.RoomGroupId)))
        {
            Status = "该分组中还有设备，不能删除。请先把设备移到其他分组。";
            return;
        }

        Groups.Remove(SelectedGroup);
        SelectedGroup = null;
        Status = "已删除分组（仅在本机）。";
    }

    private void RenameGroup()
    {
        if (SelectedGroup is null) return;
        var name = SelectedGroupName.Trim();
        if (name.Length == 0 || Groups.Any(group => group != SelectedGroup &&
                                                     StringComparer.Ordinal.Equals(group.Name, name)))
        {
            Status = "分组名称不能为空，也不能与已有分组重复。";
            return;
        }

        var oldGroup = SelectedGroup;
        var index = Groups.IndexOf(oldGroup);
        var renamed = oldGroup with { Name = name };
        Groups[index] = renamed;
        SelectedGroup = renamed;
        Status = "已重命名分组（仅在本机）。";
    }

    private async Task ApproveAsync()
    {
        if (string.IsNullOrWhiteSpace(ActivationRequestId))
        {
            Status = "请从待审批列表选中一行，或手动填写激活申请编号。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApprovalRoomGroupId))
        {
            Status = "试场分组暂不能在审批时指定，请清空分组后再审批，设备将先归入未分组。";
            return;
        }

        var result = await _api.ApproveActivationAsync(ActivationRequestId.Trim(), null);
        Upsert(result.DeviceId, result.ClientNumber, StatusText(result.Status), result.RoomGroupId);

        // 批准成功后把该申请从待审批列表中移除，并清空输入框。
        var approved = PendingRequests.FirstOrDefault(r =>
            StringComparer.Ordinal.Equals(r.ActivationRequestId, ActivationRequestId.Trim()));
        if (approved is not null) PendingRequests.Remove(approved);
        if (_selectedPendingRequest is not null &&
            StringComparer.Ordinal.Equals(_selectedPendingRequest.ActivationRequestId, ActivationRequestId.Trim()))
            SelectedPendingRequest = null;
        ActivationRequestId = string.Empty;

        Status = $"已批准，学生端编号为 {result.ClientNumber}。";
    }

    private async Task QueryDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(QueryDeviceId))
        {
            Status = "请填写设备标识。";
            return;
        }

        var result = await _api.FindDeviceAsync(QueryDeviceId.Trim());
        Upsert(result.DeviceId, result.ClientNumber, StatusText(result.Status), result.RoomGroupId);
        Status = $"已找到该设备，学生端编号为 {result.ClientNumber}。当前考生和连接情况请到设备监控页查看。";
    }

    private void Upsert(string deviceId, string clientNumber, string activationStatus, string? roomGroupId)
    {
        var existing = Devices.FirstOrDefault(device => StringComparer.Ordinal.Equals(device.DeviceId, deviceId));
        if (existing is not null) Devices.Remove(existing);
        Devices.Add(new ActivationDeviceRow(deviceId, clientNumber, activationStatus, roomGroupId ?? "", "—", "—", "—"));
    }

    private void SetError(Exception exception) => Status = $"操作失败：{FriendlyErrors.Describe(exception)}";

    private static string StatusText(ActivationStatus status) => status switch
    {
        ActivationStatus.Pending => "待审批",
        ActivationStatus.Active => "已激活",
        ActivationStatus.Revoked => "已取消",
        ActivationStatus.Rejected => "已拒绝",
        _ => "未知",
    };

    public void Dispose()
    {
        StopPolling();
    }
}

public sealed record RoomGroupDraft(string RoomGroupId, string Name);

public sealed record ActivationPendingRow(
    string ActivationRequestId,
    string DeviceId,
    string RequestedAtText);

public sealed record ActivationDeviceRow(
    string DeviceId,
    string ClientNumber,
    string ActivationStatus,
    string RoomGroupId,
    string Candidate,
    string ConnectionStatus,
    string LastSeen);
