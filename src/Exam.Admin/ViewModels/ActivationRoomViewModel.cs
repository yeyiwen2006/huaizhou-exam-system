using System.Collections.ObjectModel;
using System.IO;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;

namespace Exam.Admin.ViewModels;

public sealed class ActivationRoomViewModel : BindableBase, IDisposable
{
    private readonly AdminApiClient _api;
    private string _newGroupName = string.Empty, _selectedGroupName = string.Empty;
    private string _activationRequestId = string.Empty, _approvalRoomGroupId = string.Empty, _queryDeviceId = string.Empty;
    private string? _filterRoomGroupId;
    private string _appliedDirectoryQuery = string.Empty;
    private string? _appliedDirectoryFilter;
    private string _moveToRoomGroupId = string.Empty;
    private bool _rebuildingChoices;
    private string _status = "读取服务器试场和设备目录，查看已连接的学生端和当前考生。";
    private string _directoryStatus = "尚未读取服务器设备目录。";
    private RoomGroupDraft? _selectedGroup;
    private ActivationPendingRow? _selectedPendingRequest;
    private ActivationDeviceRow? _selectedDevice;
    private bool _busy, _reading, _backgroundReading, _groupsLoaded, _disposed;
    private long _context, _directoryOperation, _pendingOperation, _historyOperation;
    private CancellationTokenSource? _pollCts;

    public ActivationRoomViewModel(AdminApiClient api)
    {
        _api = api;
        _api.ConfigurationChanged += OnConnectionChanged;
        AddGroupCommand = new AsyncRelayCommand(AddGroupAsync, () => CanEditDirectory);
        RenameGroupCommand = new AsyncRelayCommand(RenameGroupAsync, () => CanEditDirectory && SelectedGroup is { Version: > 0 });
        RemoveGroupCommand = new AsyncRelayCommand(RemoveGroupAsync, () => CanEditDirectory && SelectedGroup is { Version: > 0 });
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => !_busy && !_disposed);
        QueryDeviceCommand = new AsyncRelayCommand(QueryDeviceAsync, () => CanReadDirectory);
        RefreshDirectoryCommand = new AsyncRelayCommand(RefreshDirectoryAsync, () => CanReadDirectory);
        RefreshPendingCommand = new AsyncRelayCommand(RefreshPendingAsync, () => Ready);
        MoveDevicesCommand = new AsyncRelayCommand(MoveSelectedDevicesAsync, () => CanEditDirectory);
        DeviceHistoryCommand = new AsyncRelayCommand(LoadSelectedHistoryAsync, () => Ready && SelectedDevice is not null);
        SelectAllDevicesCommand = new RelayCommand(() => { foreach (var row in Devices) row.IsSelected = true; }, () => CanEditDirectory);
        ClearDeviceSelectionCommand = new RelayCommand(() => { foreach (var row in Devices) row.IsSelected = false; });
        RebuildChoices();
    }

    public ObservableCollection<ActivationPendingRow> PendingRequests { get; } = [];
    public ObservableCollection<RoomGroupDraft> Groups { get; } = [];
    public ObservableCollection<RoomGroupChoice> GroupChoices { get; } = [];
    public ObservableCollection<RoomGroupChoice> FilterChoices { get; } = [];
    public ObservableCollection<ActivationDeviceRow> Devices { get; } = [];
    public ObservableCollection<DeviceRoomHistoryRow> DeviceHistory { get; } = [];
    private bool Ready => !_disposed && !_busy && _api.IsConfigured;
    private bool CanReadDirectory => Ready && (!_reading || _backgroundReading);
    public bool CanEditDirectory => CanReadDirectory && _groupsLoaded;
    public string NewGroupName { get => _newGroupName; set => SetProperty(ref _newGroupName, value); }
    public string SelectedGroupName { get => _selectedGroupName; set => SetProperty(ref _selectedGroupName, value); }
    public string ActivationRequestId { get => _activationRequestId; set => SetProperty(ref _activationRequestId, value ?? string.Empty); }
    public string ApprovalRoomGroupId
    {
        get => _approvalRoomGroupId;
        set { if (SetProperty(ref _approvalRoomGroupId, value ?? string.Empty)) OnPropertyChanged(nameof(SelectedApprovalChoice)); }
    }
    public RoomGroupChoice? SelectedApprovalChoice
    {
        get => GroupChoices.FirstOrDefault(x => x.RoomGroupId == ApprovalRoomGroupId);
        set { if (!_rebuildingChoices && value is not null) ApprovalRoomGroupId = value.RoomGroupId ?? string.Empty; }
    }
    // Kept as a binding-compatible property; its input is now a public client number or a human-readable search, never a DeviceId.
    public string QueryDeviceId { get => _queryDeviceId; set => SetProperty(ref _queryDeviceId, value ?? string.Empty); }
    public string? FilterRoomGroupId
    {
        get => _filterRoomGroupId;
        set { if (SetProperty(ref _filterRoomGroupId, value)) OnPropertyChanged(nameof(SelectedFilterChoice)); }
    }
    public RoomGroupChoice? SelectedFilterChoice
    {
        get => FilterChoices.FirstOrDefault(x => x.RoomGroupId == FilterRoomGroupId);
        set { if (!_rebuildingChoices && value is not null) FilterRoomGroupId = value.RoomGroupId; }
    }
    public string MoveToRoomGroupId
    {
        get => _moveToRoomGroupId;
        set { if (SetProperty(ref _moveToRoomGroupId, value ?? string.Empty)) OnPropertyChanged(nameof(SelectedMoveChoice)); }
    }
    public RoomGroupChoice? SelectedMoveChoice
    {
        get => GroupChoices.FirstOrDefault(x => x.RoomGroupId == MoveToRoomGroupId);
        set { if (!_rebuildingChoices && value is not null) MoveToRoomGroupId = value.RoomGroupId ?? string.Empty; }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string DirectoryStatus { get => _directoryStatus; private set => SetProperty(ref _directoryStatus, value); }

    public RoomGroupDraft? SelectedGroup
    {
        get => _selectedGroup;
        set { if (SetProperty(ref _selectedGroup, value)) { SelectedGroupName = value?.Name ?? string.Empty; NotifyCommands(); } }
    }
    public ActivationPendingRow? SelectedPendingRequest
    {
        get => _selectedPendingRequest;
        set
        {
            if (SetProperty(ref _selectedPendingRequest, value))
            { ActivationRequestId = value?.ActivationRequestId ?? string.Empty; OnPropertyChanged(nameof(PendingSelectionText)); }
        }
    }
    public string PendingSelectionText => SelectedPendingRequest is { } row
        ? $"已选中 {row.RequestedAtText} 的申请（识别码 {row.VerificationCode}）" : "请在上方选择待审批申请。";
    public ActivationDeviceRow? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            var changedIdentity = value?.DeviceId != _selectedDevice?.DeviceId;
            if (SetProperty(ref _selectedDevice, value))
            {
                if (changedIdentity) { _historyOperation++; DeviceHistory.Clear(); }
                NotifyCommands();
            }
        }
    }
    public AsyncRelayCommand AddGroupCommand { get; }
    public AsyncRelayCommand RenameGroupCommand { get; }
    public AsyncRelayCommand RemoveGroupCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand QueryDeviceCommand { get; }
    public AsyncRelayCommand RefreshDirectoryCommand { get; }
    public AsyncRelayCommand RefreshPendingCommand { get; }
    public AsyncRelayCommand MoveDevicesCommand { get; }
    public AsyncRelayCommand DeviceHistoryCommand { get; }
    public RelayCommand SelectAllDevicesCommand { get; }
    public RelayCommand ClearDeviceSelectionCommand { get; }

    public void StartPolling()
    {
        if (_pollCts is not null || !Ready) return;
        var source = new CancellationTokenSource();
        _pollCts = source;
        _ = PollAsync(source);
    }
    public void StopPolling()
    {
        var source = _pollCts;
        _pollCts = null;
        source?.Cancel();
    }
    private async Task PollAsync(CancellationTokenSource source)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                if (source.IsCancellationRequested) break;
                if (!_busy && !_reading) await RefreshDirectoryCoreAsync(source.Token, silent: true);
            }
            while (await timer.WaitForNextTickAsync(source.Token));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pollCts, source)) _pollCts = null;
            source.Dispose();
        }
    }

    public Task RefreshPendingAsync() => RefreshPendingCoreAsync(CancellationToken.None, silent: false);
    private async Task RefreshPendingCoreAsync(CancellationToken cancellationToken, bool silent)
    {
        if (!Ready) return;
        var context = _context;
        var operation = ++_pendingOperation;
        try
        {
            var list = await _api.ListPendingActivationRequestsAsync(cancellationToken);
            if (context != _context || operation != _pendingOperation || cancellationToken.IsCancellationRequested) return;
            var ids = list.Select(x => x.ActivationRequestId).ToHashSet(StringComparer.Ordinal);
            if (ids.Count != list.Count) throw new InvalidDataException("服务器返回了重复的激活申请。");
            for (var i = PendingRequests.Count - 1; i >= 0; i--) if (!ids.Contains(PendingRequests[i].ActivationRequestId)) PendingRequests.RemoveAt(i);
            var existing = PendingRequests.Select(x => x.ActivationRequestId).ToHashSet(StringComparer.Ordinal);
            foreach (var row in list.Where(x => !existing.Contains(x.ActivationRequestId)))
                PendingRequests.Add(new(row.ActivationRequestId, row.DeviceId, row.RequestedAt.ToLocalTime().ToString("MM-dd HH:mm:ss")));
            if (SelectedPendingRequest is not null && !ids.Contains(SelectedPendingRequest.ActivationRequestId)) SelectedPendingRequest = null;
            if (!silent) Status = $"已读取 {PendingRequests.Count} 条待审批申请。";
        }
        catch when (context != _context || operation != _pendingOperation || cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { if (!silent) SetError(error); }
    }

    public Task RefreshDirectoryAsync()
    {
        if (!CanReadDirectory) return Task.CompletedTask;
        QueryDeviceId = _appliedDirectoryQuery = string.Empty;
        FilterRoomGroupId = _appliedDirectoryFilter = null;
        return RefreshDirectoryCoreAsync(CancellationToken.None, silent: false);
    }
    public Task QueryDeviceAsync() => RefreshDirectoryCoreAsync(CancellationToken.None, silent: false, queryRequested: true);
    private async Task RefreshDirectoryCoreAsync(CancellationToken cancellationToken, bool silent, bool queryRequested = false)
    {
        if (!Ready || (_reading && (silent || !_backgroundReading))) return;
        var context = _context;
        var operation = ++_directoryOperation;
        var filter = queryRequested ? FilterRoomGroupId : _appliedDirectoryFilter;
        var query = queryRequested ? QueryDeviceId.Trim() : _appliedDirectoryQuery;
        _reading = true;
        _backgroundReading = silent;
        NotifyCommands();
        try
        {
            var groups = await _api.ListRoomGroupsAsync(cancellationToken);
            if (context != _context || operation != _directoryOperation || cancellationToken.IsCancellationRequested) return;
            if (groups.Any(x => x is null || x.Version < 1 || string.IsNullOrWhiteSpace(x.GroupId) || string.IsNullOrWhiteSpace(x.Name)) ||
                groups.Select(x => x.GroupId).Distinct(StringComparer.Ordinal).Count() != groups.Count)
                throw new InvalidDataException("服务器试场列表信息无效。");
            var incoming = new List<DeviceDirectoryEntry>();
            var offset = 0;
            DateTimeOffset asOf = default;
            for (var page = 0; page < 100; page++)
            {
                var result = await _api.GetDevicesPageAsync(filter, query, offset, cancellationToken);
                if (context != _context || operation != _directoryOperation || cancellationToken.IsCancellationRequested) return;
                if (result.Items is null || result.Items.Any(x => x is null || x.Version < 1 || string.IsNullOrWhiteSpace(x.ClientNumber)))
                    throw new InvalidDataException("服务器设备目录包含无效记录。");
                incoming.AddRange(result.Items);
                asOf = result.AsOf;
                if (result.NextOffset is null) break;
                if (result.NextOffset <= offset || page == 99) throw new InvalidDataException("设备目录分页未正常结束，请缩小筛选范围。");
                offset = result.NextOffset.Value;
            }
            if (incoming.Select(x => x.DeviceId).Distinct(StringComparer.Ordinal).Count() != incoming.Count ||
                incoming.Select(x => x.ClientNumber).Distinct(StringComparer.Ordinal).Count() != incoming.Count)
                throw new InvalidDataException("设备目录出现重复身份，未应用该次查询。");
            if (query.Length is 11 or 12 && query.All(char.IsAsciiDigit)) incoming = incoming.Where(x => x.ClientNumber == query).ToList();
            if (queryRequested && (filter != FilterRoomGroupId || query != QueryDeviceId.Trim()))
            { DirectoryStatus = "查询条件已变化，请重新查询；未应用旧条件的结果。"; return; }
            _appliedDirectoryQuery = query;
            _appliedDirectoryFilter = filter;
            ApplyGroups(groups);
            var existing = Devices.ToDictionary(row => row.DeviceId, StringComparer.Ordinal);
            var rows = incoming.Select(item =>
            {
                if (!existing.TryGetValue(item.DeviceId, out var row)) return FromDevice(item, false);
                row.UpdateFrom(FromDevice(item, row.IsSelected));
                return row;
            }).ToArray();
            SynchronizeRows(Devices, rows);
            if (SelectedDevice is not null && !Devices.Contains(SelectedDevice)) SelectedDevice = null;
            _groupsLoaded = true;
            DirectoryStatus = $"服务器目录：{Devices.Count} 台设备；最后读取 {asOf.ToLocalTime():yyyy-MM-dd HH:mm:ss}。";
            if (!silent) Status = Devices.Count == 0 ? "没有符合当前编号、姓名或试场条件的设备。" : "已读取服务器试场和设备目录。";
        }
        catch when (context != _context || operation != _directoryOperation || cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            _groupsLoaded = false;
            DirectoryStatus = "目录未能刷新，旧结果仅供核对，重新读取成功前不能修改试场或转组。";
            if (!silent) SetError(error);
        }
        finally { if (context == _context && operation == _directoryOperation) { _reading = _backgroundReading = false; NotifyCommands(); } }
    }

    public async Task AddGroupAsync()
    {
        if (!CanEditDirectory) return;
        var name = NewGroupName.Trim();
        if (name.Length == 0) { Status = "分组名称不能为空。"; return; }
        await MutateAsync(() => _api.CreateRoomGroupAsync(name), result =>
        {
            ApplyGroup(result);
            SelectedGroup = Groups.Single(x => x.RoomGroupId == result.GroupId);
            if (NewGroupName.Trim() == name) NewGroupName = string.Empty;
        }, "试场已保存到服务器。");
    }
    public async Task RenameGroupAsync()
    {
        if (!CanEditDirectory || SelectedGroup is not { Version: > 0 } group) return;
        var name = SelectedGroupName.Trim();
        if (name.Length == 0) { Status = "分组名称不能为空。"; return; }
        await MutateAsync(() => _api.RenameRoomGroupAsync(group.RoomGroupId, group.Version, name), result =>
        {
            if (result.GroupId != group.RoomGroupId) throw new InvalidDataException("改名回执不属于所选试场。");
            ApplyGroup(result);
            foreach (var device in Devices.Where(x => x.RoomGroupId == result.GroupId)) device.RoomGroupName = result.Name;
        }, "试场名称已保存到服务器。");
    }
    public async Task RemoveGroupAsync()
    {
        if (!CanEditDirectory || SelectedGroup is not { Version: > 0 } group) return;
        await MutateAsync(() => _api.DeleteRoomGroupAsync(group.RoomGroupId, group.Version), result =>
        {
            if (!result.Deleted || result.GroupId != group.RoomGroupId) throw new InvalidDataException("无法确认试场删除结果。");
            var item = Groups.FirstOrDefault(x => x.RoomGroupId == result.GroupId);
            if (item is not null) Groups.Remove(item);
            SelectedGroup = null;
            RebuildChoices();
        }, "服务器已确认空组并删除；非空组不会被删除。");
    }

    public async Task ApproveAsync()
    {
        if (_disposed || _busy) return;
        var requestId = ActivationRequestId.Trim();
        var groupId = string.IsNullOrWhiteSpace(ApprovalRoomGroupId) ? null : ApprovalRoomGroupId.Trim();
        if (requestId.Length == 0) { Status = "请在待审批列表选择一条申请。"; return; }
        if (groupId is not null && (!_groupsLoaded || !Groups.Any(x => x.RoomGroupId == groupId && x.Version > 0)))
        { Status = "试场尚未从服务器读取或已失效，请刷新目录后重新选择；未发送审批。"; return; }
        if (!Ready) { Status = "请先登录考务账号。"; return; }
        await MutateAsync(() => _api.ApproveActivationAsync(requestId, groupId), result =>
        {
            var previous = Devices.FirstOrDefault(x => x.DeviceId == result.DeviceId);
            if (previous is not null) Devices.Remove(previous);
            Devices.Add(new(result.DeviceId, result.ClientNumber, ActivationText(result.Status), result.RoomGroupId ?? "",
                "等待状态上报", "尚未通信", "尚未通信")
            { RoomGroupName = Groups.FirstOrDefault(x => x.RoomGroupId == result.RoomGroupId)?.Name ?? (result.RoomGroupId is null ? "未确定试场" : "等待目录刷新") });
            var pending = PendingRequests.FirstOrDefault(x => x.ActivationRequestId == requestId);
            if (pending is not null) PendingRequests.Remove(pending);
            if (SelectedPendingRequest?.ActivationRequestId == requestId) SelectedPendingRequest = null;
            if (ActivationRequestId.Trim() == requestId) ActivationRequestId = string.Empty;
        }, "已批准，设备激活和公开编号已保存；目录刷新后可查看当前状态并转组。");
    }

    public async Task MoveSelectedDevicesAsync()
    {
        if (!CanEditDirectory) return;
        var rows = Devices.Where(x => x.IsSelected).ToArray();
        if (rows.Length == 0 && SelectedDevice is not null) rows = [SelectedDevice];
        if (rows.Length is < 1 or > 2000) { Status = "请勾选 1 至 2000 台设备后再转组。"; return; }
        if (rows.Any(x => x.Version < 1)) { Status = "设备尚未取得服务器版本，请重新读取目录。"; return; }
        var target = string.IsNullOrWhiteSpace(MoveToRoomGroupId) ? null : MoveToRoomGroupId;
        if (target is not null && !Groups.Any(x => x.RoomGroupId == target)) { Status = "目标试场已失效，请刷新后重选。"; return; }
        var context = _context;
        var moved = await MutateAsync(() => _api.MoveDevicesAsync(target,
            rows.Select(x => new DeviceMoveTarget(x.ClientNumber, x.Version)).ToArray(), "考务调整试场"), _ => { _groupsLoaded = false; },
            "服务器已整批保存转组，正在重新读取版本与归属。");
        if (moved && context == _context)
        {
            await RefreshDirectoryCoreAsync(CancellationToken.None, silent: false);
            if (context == _context && _groupsLoaded) Status = $"已完成 {rows.Length} 台设备的转组，编号、考生和成绩不变。";
        }
    }

    public async Task LoadSelectedHistoryAsync()
    {
        if (!Ready || SelectedDevice is null) return;
        var context = _context;
        var operation = ++_historyOperation;
        var number = SelectedDevice.ClientNumber;
        try
        {
            var history = await _api.GetDeviceRoomHistoryAsync(number);
            if (context != _context || operation != _historyOperation || SelectedDevice?.ClientNumber != number) return;
            if (history.Any(x => x.ClientNumber != number)) throw new InvalidDataException("设备变更试场历史不属于选中设备。");
            DeviceHistory.Clear();
            foreach (var row in history) DeviceHistory.Add(new(row.ChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                row.FromGroupId is null ? "未确定试场" : row.FromGroupName,
                row.ToGroupId is null ? "未确定试场" : row.ToGroupName, row.OperatorId, row.Reason));
            Status = $"已读取学生端 {number} 最近 {history.Count} 条设备变更试场历史。";
        }
        catch when (context != _context || operation != _historyOperation) { }
        catch (Exception error) { SetError(error); }
    }

    private async Task<bool> MutateAsync<T>(Func<Task<T>> action, Action<T> apply, string success)
    {
        if (!Ready) return false;
        var context = _context;
        _busy = true;
        _directoryOperation++;
        _pendingOperation++;
        _reading = _backgroundReading = false;
        NotifyCommands();
        try
        {
            var result = await action();
            if (context != _context) return false;
            apply(result);
            Status = success;
            return true;
        }
        catch when (context != _context) { return false; }
        catch (Exception error) { SetError(error); return false; }
        finally { if (context == _context) { _busy = false; NotifyCommands(); } }
    }

    private void ApplyGroups(IReadOnlyList<RoomGroup> groups)
    {
        var editedName = SelectedGroupName;
        var previousName = SelectedGroup?.Name;
        var existing = Groups.ToDictionary(row => row.RoomGroupId, StringComparer.Ordinal);
        var rows = groups.Select(group =>
        {
            if (!existing.TryGetValue(group.GroupId, out var row))
                return new RoomGroupDraft(group.GroupId, group.Name, group.Version, group.DeviceCount);
            row.UpdateFrom(group);
            return row;
        }).ToArray();
        SynchronizeRows(Groups, rows);
        if (SelectedGroup is not null && !Groups.Contains(SelectedGroup)) SelectedGroup = null;
        else if (SelectedGroup is not null && editedName == previousName) SelectedGroupName = SelectedGroup.Name;
        RebuildChoices();
    }
    private void ApplyGroup(RoomGroup group)
    {
        if (group.Version < 1 || string.IsNullOrWhiteSpace(group.GroupId)) throw new InvalidDataException("试场回执无效。");
        var previous = Groups.FirstOrDefault(x => x.RoomGroupId == group.GroupId);
        if (previous is null) Groups.Add(new(group.GroupId, group.Name, group.Version, group.DeviceCount));
        else previous.UpdateFrom(group);
        if (SelectedGroup?.RoomGroupId == group.GroupId) SelectedGroupName = group.Name;
        RebuildChoices();
    }
    private void RebuildChoices()
    {
        var approval = ApprovalRoomGroupId;
        var move = MoveToRoomGroupId;
        var filter = FilterRoomGroupId;
        _rebuildingChoices = true;
        try
        {
            SynchronizeChoices(GroupChoices, [new("", "未确定试场"), .. Groups.Select(group => new RoomGroupChoice(group.RoomGroupId, group.Name))]);
            SynchronizeChoices(FilterChoices, [new(null, "全部试场"), new("", "未确定试场"), .. Groups.Select(group => new RoomGroupChoice(group.RoomGroupId, group.Name))]);
            ApprovalRoomGroupId = Groups.Any(x => x.RoomGroupId == approval) ? approval : "";
            MoveToRoomGroupId = Groups.Any(x => x.RoomGroupId == move) ? move : "";
            FilterRoomGroupId = filter is null || filter == "" || Groups.Any(x => x.RoomGroupId == filter) ? filter : null;
        }
        finally { _rebuildingChoices = false; }
        // 全部试场的编号是 null；以选项对象恢复选择，避免被 WPF 当作“未选择”。
        OnPropertyChanged(nameof(SelectedFilterChoice));
        OnPropertyChanged(nameof(SelectedApprovalChoice));
        OnPropertyChanged(nameof(SelectedMoveChoice));
        if (_appliedDirectoryFilter is not null && _appliedDirectoryFilter != "" &&
            !Groups.Any(x => x.RoomGroupId == _appliedDirectoryFilter)) _appliedDirectoryFilter = null;
    }
    private static void SynchronizeChoices(ObservableCollection<RoomGroupChoice> choices, IReadOnlyList<RoomGroupChoice> incoming)
    {
        var rows = incoming.Select(choice =>
        {
            var existing = choices.FirstOrDefault(row => row.RoomGroupId == choice.RoomGroupId);
            if (existing is null) return choice;
            existing.Name = choice.Name;
            return existing;
        }).ToArray();
        SynchronizeRows(choices, rows);
    }
    private static void SynchronizeRows<T>(ObservableCollection<T> rows, IReadOnlyList<T> incoming) where T : class
    {
        var retained = incoming.ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!retained.Contains(rows[index])) rows.RemoveAt(index);
        for (var index = 0; index < incoming.Count; index++)
        {
            if (index < rows.Count && ReferenceEquals(rows[index], incoming[index])) continue;
            var previous = rows.IndexOf(incoming[index]);
            if (previous < 0) rows.Insert(index, incoming[index]);
            else rows.Move(previous, index);
        }
    }
    private static ActivationDeviceRow FromDevice(DeviceDirectoryEntry device, bool selected) =>
        new(device.DeviceId, device.ClientNumber, ActivationText(device.ActivationStatus), device.RoomGroupId ?? "",
            device.CandidateName ?? "未登录", ConnectionText(device.ConnectionState), device.LastCommunicationAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "尚未通信")
        { RoomGroupName = device.RoomGroupId is null ? "未确定试场" : device.RoomGroupName, ClassText = device.ClassText ?? "—", AdmissionNo = device.AdmissionNo ?? "—",
            ExamName = device.ExamName ?? "尚未选择考试", Page = device.Page, Version = device.Version, IsSelected = selected };
    internal static string ConnectionText(DeviceConnectionState state) => state switch
    { DeviceConnectionState.Online => "在线", DeviceConnectionState.Offline => "离线", DeviceConnectionState.Recovering => "恢复中", _ => "尚未通信" };
    private static string ActivationText(ActivationStatus state) => state switch
    { ActivationStatus.Pending => "待审批", ActivationStatus.Active => "已激活", ActivationStatus.Revoked => "已取消", ActivationStatus.Rejected => "已拒绝", _ => "未知" };
    private void SetError(Exception error)
    {
        if (error is AdminApiException { StatusCode: System.Net.HttpStatusCode.Conflict })
        {
            _groupsLoaded = false;
            DirectoryStatus = "服务器状态与当前目录不一致，请重新读取后再执行分组或转组。";
            NotifyCommands();
        }
        Status = "操作失败：" + FriendlyErrors.Describe(error) + "；未确认成功时请重试同一操作或重新读取核对。";
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEditDirectory));
        AddGroupCommand?.RaiseCanExecuteChanged(); RenameGroupCommand?.RaiseCanExecuteChanged(); RemoveGroupCommand?.RaiseCanExecuteChanged();
        QueryDeviceCommand?.RaiseCanExecuteChanged(); RefreshDirectoryCommand?.RaiseCanExecuteChanged(); RefreshPendingCommand?.RaiseCanExecuteChanged();
        ApproveCommand?.RaiseCanExecuteChanged(); MoveDevicesCommand?.RaiseCanExecuteChanged(); DeviceHistoryCommand?.RaiseCanExecuteChanged();
        SelectAllDevicesCommand?.RaiseCanExecuteChanged(); ClearDeviceSelectionCommand?.RaiseCanExecuteChanged();
    }
    private void OnConnectionChanged(object? sender, EventArgs args)
    {
        _appliedDirectoryQuery = string.Empty;
        _appliedDirectoryFilter = null;
        StopPolling(); _context++; _directoryOperation++; _pendingOperation++; _historyOperation++;
        _busy = _reading = _backgroundReading = _groupsLoaded = false;
        PendingRequests.Clear(); Devices.Clear(); Groups.Clear(); DeviceHistory.Clear();
        SelectedPendingRequest = null; SelectedDevice = null; SelectedGroup = null;
        ActivationRequestId = ApprovalRoomGroupId = QueryDeviceId = NewGroupName = SelectedGroupName = MoveToRoomGroupId = string.Empty;
        FilterRoomGroupId = null; RebuildChoices();
        DirectoryStatus = "尚未读取当前服务器的设备目录。";
        Status = "连接已更换，旧试场、设备、查询结果和操作输入已清除。";
        NotifyCommands();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _api.ConfigurationChanged -= OnConnectionChanged;
        OnConnectionChanged(null, EventArgs.Empty);
    }
}

public sealed class RoomGroupDraft(string roomGroupId, string name, long version = 0, int deviceCount = 0) : BindableBase
{
    private string _name = name;
    private long _version = version;
    private int _deviceCount = deviceCount;
    public string RoomGroupId { get; } = roomGroupId;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public long Version { get => _version; private set => SetProperty(ref _version, value); }
    public int DeviceCount { get => _deviceCount; private set => SetProperty(ref _deviceCount, value); }
    public void UpdateFrom(RoomGroup group) { Name = group.Name; Version = group.Version; DeviceCount = group.DeviceCount; }
}
public sealed class RoomGroupChoice(string? roomGroupId, string name) : BindableBase
{
    private string _name = name;
    public string? RoomGroupId { get; } = roomGroupId;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
}
public sealed record ActivationPendingRow(string ActivationRequestId, string DeviceId, string RequestedAtText)
{
    public string VerificationCode => ActivationDisplay.RequestCode(ActivationRequestId);
}
public sealed record DeviceRoomHistoryRow(string ChangedAt, string FromGroupName, string ToGroupName, string Operator, string Reason);

public sealed class ActivationDeviceRow(string deviceId, string clientNumber, string activationStatus, string roomGroupId,
    string candidate, string connectionStatus, string lastSeen) : BindableBase
{
    private bool _isSelected;
    private string _roomGroupName = "未确定试场";
    private string _clientNumber = clientNumber, _activationStatus = activationStatus, _roomGroupId = roomGroupId;
    private string _candidate = candidate, _connectionStatus = connectionStatus, _lastSeen = lastSeen;
    private string _classText = "—", _admissionNo = "—", _examName = "尚未选择考试", _page = "等待状态上报";
    private long _version;
    public string DeviceId { get; } = deviceId;
    public string ClientNumber { get => _clientNumber; private set => SetProperty(ref _clientNumber, value); }
    public string ActivationStatus { get => _activationStatus; private set => SetProperty(ref _activationStatus, value); }
    public string RoomGroupId { get => _roomGroupId; private set => SetProperty(ref _roomGroupId, value); }
    public string Candidate { get => _candidate; private set => SetProperty(ref _candidate, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string LastSeen { get => _lastSeen; private set => SetProperty(ref _lastSeen, value); }
    public string RoomGroupName { get => _roomGroupName; set => SetProperty(ref _roomGroupName, value); }
    public string ClassText { get => _classText; set => SetProperty(ref _classText, value); }
    public string AdmissionNo { get => _admissionNo; set => SetProperty(ref _admissionNo, value); }
    public string ExamName { get => _examName; set => SetProperty(ref _examName, value); }
    public string Page { get => _page; set => SetProperty(ref _page, value); }
    public long Version { get => _version; set => SetProperty(ref _version, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public void UpdateFrom(ActivationDeviceRow row)
    {
        ClientNumber = row.ClientNumber; ActivationStatus = row.ActivationStatus; RoomGroupId = row.RoomGroupId;
        Candidate = row.Candidate; ConnectionStatus = row.ConnectionStatus; LastSeen = row.LastSeen;
        RoomGroupName = row.RoomGroupName; ClassText = row.ClassText; AdmissionNo = row.AdmissionNo;
        ExamName = row.ExamName; Page = row.Page; Version = row.Version;
    }
}
