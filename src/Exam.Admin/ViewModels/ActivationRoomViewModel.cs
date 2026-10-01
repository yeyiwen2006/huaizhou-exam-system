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
    private string _status = "可审批学生端激活申请并按设备编号查询；考场分组目前只能在本机整理。";

    public ActivationRoomViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += (_, _) =>
        {
            Devices.Clear();
            ActivationRequestId = string.Empty;
            ApprovalRoomGroupId = string.Empty;
            QueryDeviceId = string.Empty;
            Status = "已切换服务器，之前的查询结果和审批输入已清除。";
        };
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
        if (SelectedGroup is null)
        {
            return;
        }
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
        if (SelectedGroup is null)
        {
            return;
        }
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
            Status = "请填写激活申请 ID。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ApprovalRoomGroupId))
        {
            Status = "考场分组暂不能在审批时指定，请清空分组后再审批，设备将先归入未分组。";
            return;
        }

        var result = await _api.ApproveActivationAsync(ActivationRequestId.Trim(), null);
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
