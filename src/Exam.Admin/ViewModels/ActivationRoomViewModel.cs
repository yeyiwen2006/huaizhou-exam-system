using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;

namespace Exam.Admin.ViewModels;

public sealed class ActivationRoomViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _newGroupName = string.Empty;
    private string _selectedGroupName = string.Empty;
    private RoomGroupDraft? _selectedGroup;
    private string _activationRequestId = string.Empty;
    private string _approvalRoomGroupId = string.Empty;
    private string _queryDeviceId = string.Empty;
    private string _status = "激活审批和单设备查询使用正式 API；房间组持久化/移动接口尚未提供。";

    public ActivationRoomViewModel(AdminApiClient api)
    {
        _api = api;
        AddGroupCommand = new RelayCommand(AddGroup);
        RenameGroupCommand = new RelayCommand(RenameGroup, () => SelectedGroup is not null);
        RemoveGroupCommand = new RelayCommand(RemoveGroup, () => SelectedGroup is not null);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, onError: SetError);
        QueryDeviceCommand = new AsyncRelayCommand(QueryDeviceAsync, onError: SetError);
    }

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
    public string ActivationRequestId { get => _activationRequestId; set => SetProperty(ref _activationRequestId, value); }
    public string ApprovalRoomGroupId { get => _approvalRoomGroupId; set => SetProperty(ref _approvalRoomGroupId, value); }
    public string QueryDeviceId { get => _queryDeviceId; set => SetProperty(ref _queryDeviceId, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public RelayCommand AddGroupCommand { get; }
    public RelayCommand RenameGroupCommand { get; }
    public RelayCommand RemoveGroupCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand QueryDeviceCommand { get; }

    private void AddGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0)
        {
            Status = "房间组名称不能为空。";
            return;
        }
        if (Groups.Any(group => StringComparer.Ordinal.Equals(group.Name, name)))
        {
            Status = "房间组名称不能重复。";
            return;
        }

        var group = new RoomGroupDraft(Guid.NewGuid().ToString("N"), name);
        Groups.Add(group);
        SelectedGroup = group;
        NewGroupName = string.Empty;
        Status = "房间组已加入本地草稿；等待 A 提供正式 CRUD API 后才能持久化。";
    }

    private void RemoveGroup()
    {
        if (SelectedGroup is null)
        {
            return;
        }
        if (Devices.Any(device => StringComparer.Ordinal.Equals(device.RoomGroupId, SelectedGroup.RoomGroupId)))
        {
            Status = "该组已有设备，不能删除。请先通过正式移动接口清空；该接口当前尚未提供。";
            return;
        }

        Groups.Remove(SelectedGroup);
        SelectedGroup = null;
        Status = "空房间组已从本地草稿删除。";
    }

    private void RenameGroup()
    {
        if (SelectedGroup is null)
        {
            return;
        }
        var name = SelectedGroupName.Trim();
        if (name.Length == 0 || Groups.Any(group => group != SelectedGroup &&
                                                     StringComparer.Ordinal.Equals(group.Name, name)))
        {
            Status = "房间组名称不能为空，也不能与已有组重复。";
            return;
        }

        var oldGroup = SelectedGroup;
        var index = Groups.IndexOf(oldGroup);
        var renamed = oldGroup with { Name = name };
        Groups[index] = renamed;
        SelectedGroup = renamed;
        Status = "房间组已在本地草稿中改名；正式持久化仍等待 A 的房间组 API。";
    }

    private async Task ApproveAsync()
    {
        if (string.IsNullOrWhiteSpace(ActivationRequestId))
        {
            Status = "请填写激活申请 ID。";
            return;
        }

        var result = await _api.ApproveActivationAsync(ActivationRequestId.Trim(), ApprovalRoomGroupId);
        Upsert(result.DeviceId, result.ClientNumber, result.Status.ToString(), result.RoomGroupId);
        Status = $"审批成功：设备 {result.DeviceId}，客户端编号 {result.ClientNumber}。编号由 A02 服务生成。";
    }

    private async Task QueryDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(QueryDeviceId))
        {
            Status = "请填写设备 ID。";
            return;
        }

        var result = await _api.FindDeviceAsync(QueryDeviceId.Trim());
        Upsert(result.DeviceId, result.ClientNumber, result.Status.ToString(), result.RoomGroupId);
        Status = $"已加载设备 {result.DeviceId}。候选人和在线状态字段当前接口未提供。";
    }

    private void Upsert(string deviceId, string clientNumber, string activationStatus, string? roomGroupId)
    {
        var existing = Devices.FirstOrDefault(device => StringComparer.Ordinal.Equals(device.DeviceId, deviceId));
        if (existing is not null)
        {
            Devices.Remove(existing);
        }
        Devices.Add(new ActivationDeviceRow(deviceId, clientNumber, activationStatus, roomGroupId ?? "", "接口未提供", "接口未提供", "接口未提供"));
    }

    private void SetError(Exception exception) => Status = $"操作失败：{exception.Message}";
}

public sealed record RoomGroupDraft(string RoomGroupId, string Name);

public sealed record ActivationDeviceRow(
    string DeviceId,
    string ClientNumber,
    string ActivationStatus,
    string RoomGroupId,
    string Candidate,
    string ConnectionStatus,
    string LastSeen);
