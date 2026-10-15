using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;

namespace Exam.AdminUnitTests;

public sealed class ServerRoomViewModelTests
{
    [Theory]
    [InlineData(null, "全部试场")]
    [InlineData("", "未确定试场")]
    [InlineData("room-a", "合成试场A")]
    public async Task RoomFilterSelectionSurvivesRepeatedDirectoryQueries(string? groupId, string label)
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedFilterChoice = test.Model.FilterChoices.Single(choice => choice.RoomGroupId == groupId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await test.Model.QueryDeviceAsync();
            Assert.Equal(groupId, test.Model.FilterRoomGroupId);
            Assert.Equal(label, test.Model.SelectedFilterChoice!.Name);
            Assert.Contains(test.Model.SelectedFilterChoice, test.Model.FilterChoices);
        }
    }

    [Fact]
    public async Task GroupWritesUseServerIdsAndVersions_DeletionChecksAllServerDevices()
    {
        using var test = new RoomServer();
        test.Devices.Clear(); // This query can be empty even though another page/room has an assigned device.
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedGroup = Assert.Single(test.Model.Groups);
        var id = test.Model.SelectedGroup.RoomGroupId;
        test.Model.SelectedGroupName = "改名后的合成试场";
        await test.Model.RenameGroupAsync();
        Assert.Equal(id, Assert.Single(test.Model.Groups).RoomGroupId);
        Assert.Equal(2, test.Model.SelectedGroup!.Version);
        Assert.Equal("改名后的合成试场", test.Model.SelectedGroup.Name);
        await test.Model.RemoveGroupAsync();
        Assert.Single(test.Model.Groups);
        Assert.Contains("不能删除", test.Model.Status, StringComparison.Ordinal);
        Assert.Contains(test.Requests, x => x.Method == "DELETE" && x.Path == "/api/v1/rooms/room-a");
        test.Groups[0] = test.Groups[0] with { DeviceCount = 0 };
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedGroup = Assert.Single(test.Model.Groups);
        await test.Model.RemoveGroupAsync();
        Assert.Empty(test.Model.Groups);
        Assert.Empty(test.Groups);
    }

    [Fact]
    public async Task CreateWaitsForServerReceiptAndRejectsDuplicateNames()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        test.Model.NewGroupName = "第二合成试场";
        await test.Model.AddGroupAsync();
        Assert.Equal(2, test.Model.Groups.Count);
        Assert.Equal("server-created-room", test.Model.SelectedGroup!.RoomGroupId);
        Assert.Equal(1, test.Model.SelectedGroup.Version);
        test.Model.NewGroupName = "第二合成试场";
        await test.Model.AddGroupAsync();
        Assert.Equal(2, test.Model.Groups.Count);
        Assert.StartsWith("操作失败", test.Model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateGroupWriteAfterConnectionResetDoesNotReturnOldServerData()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        var pending = NewResponse();
        test.Intercept = (request, _) => request.Method == HttpMethod.Post ? pending.Task : Task.FromResult<HttpResponseMessage?>(null);
        test.Model.NewGroupName = "旧服务器新试场";
        var writing = test.Model.AddGroupAsync();
        test.Api.Configure("https://another-synthetic-server.invalid/", "synthetic-second-session", "synthetic-operator");
        Assert.Empty(test.Model.Groups);
        pending.SetResult(Ok(new RoomGroup("late-old-group", "旧服务器新试场", 1, 0)));
        await writing;
        Assert.Empty(test.Model.Groups);
        Assert.Empty(test.Model.Devices);
        Assert.Empty(test.Model.NewGroupName);
        Assert.False(test.Model.CanEditDirectory);
    }

    [Fact]
    public async Task LateDirectoryReadAfterDisconnectCannotRestoreGroupsOrCandidates()
    {
        using var test = new RoomServer();
        var pending = NewResponse();
        test.Intercept = (request, _) => request.RequestUri!.AbsolutePath == "/api/v1/devices" ? pending.Task : Task.FromResult<HttpResponseMessage?>(null);
        var loading = test.Model.RefreshDirectoryAsync();
        test.Api.Disconnect();
        pending.SetResult(Ok(new DeviceDirectoryPage(test.Devices.ToArray(), null, RoomServer.Now)));
        await loading;
        Assert.Empty(test.Model.Groups);
        Assert.Empty(test.Model.Devices);
        Assert.False(test.Model.CanEditDirectory);
    }

    [Fact]
    public async Task ReloadDoesNotOverwriteAnUnsubmittedRoomRename()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedGroup = Assert.Single(test.Model.Groups);
        test.Model.SelectedGroupName = "正在输入、尚未提交的名字";
        await test.Model.RefreshDirectoryAsync();
        Assert.Equal("正在输入、尚未提交的名字", test.Model.SelectedGroupName);
        Assert.Equal("合成试场A", test.Model.SelectedGroup!.Name);
    }

    [Fact]
    public async Task DirectoryContainsRealCandidateStatusAndPublicNumber_NotPlaceholderOrInternalSearch()
    {
        using var test = new RoomServer();
        test.Model.QueryDeviceId = "20261002001";
        await test.Model.QueryDeviceAsync();
        var device = Assert.Single(test.Model.Devices);
        Assert.Equal("合成考生甲", device.Candidate);
        Assert.Equal("001", device.ClassText);
        Assert.Equal("000001", device.AdmissionNo);
        Assert.Equal("合成考试", device.ExamName);
        Assert.Equal("合成试场A", device.RoomGroupName);
        Assert.Equal("在线", device.ConnectionStatus);
        Assert.Equal("正在答题", device.Page);
        Assert.Contains(test.Requests, x => x.Path == "/api/v1/devices" && x.Query.Contains("query=20261002001", StringComparison.Ordinal));
        Assert.DoesNotContain(test.Requests, x => x.Path.StartsWith("/api/v1/activation/devices/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("20261002002")]
    [InlineData("202610020002")]
    public async Task PublicNumberLookupDoesNotSelectAPartialOrOtherNumber(string number)
    {
        using var test = new RoomServer();
        test.Devices[0] = test.Devices[0] with { ClientNumber = number.Length == 11 ? number + "0" : number[..11] };
        test.Devices.Add(test.Devices[0] with { DeviceId = "device-b", ClientNumber = number });
        test.Model.QueryDeviceId = number;
        await test.Model.QueryDeviceAsync();
        Assert.Equal(number, Assert.Single(test.Model.Devices).ClientNumber);
    }

    [Fact]
    public async Task DeviceDirectoryLoadsAllPagesAndDoesNotSilentlyDropLaterDevices()
    {
        using var test = new RoomServer();
        test.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath != "/api/v1/devices" ? null
            : request.RequestUri.Query.Contains("offset=1", StringComparison.Ordinal)
                ? Ok(new DeviceDirectoryPage([test.Devices[0] with { DeviceId = "device-b", ClientNumber = "20261002002" }], null, RoomServer.Now))
                : Ok(new DeviceDirectoryPage(test.Devices.ToArray(), 1, RoomServer.Now)));
        await test.Model.RefreshDirectoryAsync();
        Assert.Equal(2, test.Model.Devices.Count);
        Assert.Contains(test.Model.Devices, x => x.ClientNumber == "20261002002");
    }

    [Fact]
    public async Task ChangedQueryBeforeResponseDiscardsOldSearchResult()
    {
        using var test = new RoomServer();
        var pending = NewResponse();
        test.Intercept = (request, _) => request.RequestUri!.AbsolutePath == "/api/v1/devices" ? pending.Task : Task.FromResult<HttpResponseMessage?>(null);
        test.Model.QueryDeviceId = "旧条件";
        var reading = test.Model.QueryDeviceAsync();
        test.Model.QueryDeviceId = "新条件";
        pending.SetResult(Ok(new DeviceDirectoryPage(test.Devices.ToArray(), null, RoomServer.Now)));
        await reading;
        Assert.Empty(test.Model.Devices);
        Assert.Contains("条件已变化", test.Model.DirectoryStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningDirectoryIgnoresConditionsThatHaveNotBeenQueried()
    {
        using var test = new RoomServer();
        test.Devices.Add(test.Devices[0] with { DeviceId = "device-b", ClientNumber = "20261002002", RoomGroupId = null });
        test.Model.QueryDeviceId = "尚未点击查询";
        test.Model.FilterRoomGroupId = "room-a";
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Model.Devices.CollectionChanged += (_, _) =>
        {
            if (test.Model.Devices.Count == 2) loaded.TrySetResult();
        };
        test.Model.StartPolling();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        test.Model.StopPolling();
        Assert.Equal(2, test.Model.Devices.Count);
        var request = Assert.Single(test.Requests, request => request.Path == "/api/v1/devices");
        Assert.DoesNotContain("query=", request.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("roomGroupId=", request.Query, StringComparison.Ordinal);
        Assert.Equal("尚未点击查询", test.Model.QueryDeviceId);
    }

    [Fact]
    public async Task AutomaticRefreshKeepsAppliedQueryWhileSearchInputsAreBeingEdited()
    {
        using var test = new RoomServer();
        test.Model.QueryDeviceId = "合成考生";
        test.Model.FilterRoomGroupId = "room-a";
        await test.Model.QueryDeviceAsync();
        test.Requests.Clear();
        test.Model.QueryDeviceId = "尚未查询的新条件";
        test.Model.FilterRoomGroupId = null;
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/devices") requested.TrySetResult();
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        test.Model.StartPolling();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        test.Model.StopPolling();

        var request = Assert.Single(test.Requests, request => request.Path == "/api/v1/devices");
        Assert.Contains("query=" + Uri.EscapeDataString("合成考生"), request.Query, StringComparison.Ordinal);
        Assert.Contains("roomGroupId=room-a", request.Query, StringComparison.Ordinal);
        Assert.Equal("尚未查询的新条件", test.Model.QueryDeviceId);
        Assert.Null(test.Model.FilterRoomGroupId);
    }

    [Fact]
    public async Task ReloadClearsSearchAndRoomFilter_AndLoadsTheWholePaginatedDirectory()
    {
        using var test = new RoomServer();
        var second = test.Devices[0] with { DeviceId = "device-b", ClientNumber = "20261002002", RoomGroupId = null };
        test.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/v1/devices") return Task.FromResult<HttpResponseMessage?>(null);
            var query = request.RequestUri.Query;
            return Task.FromResult<HttpResponseMessage?>(Ok(
                query.Contains("query=", StringComparison.Ordinal)
                    ? new DeviceDirectoryPage(test.Devices.ToArray(), null, RoomServer.Now)
                    : query.Contains("offset=1", StringComparison.Ordinal)
                        ? new DeviceDirectoryPage([second], null, RoomServer.Now)
                        : new DeviceDirectoryPage(test.Devices.ToArray(), 1, RoomServer.Now)));
        };
        test.Model.QueryDeviceId = "20261002001";
        test.Model.FilterRoomGroupId = "room-a";
        await test.Model.QueryDeviceAsync();
        Assert.Single(test.Model.Devices);
        test.Requests.Clear();

        await test.Model.RefreshDirectoryAsync();

        Assert.Empty(test.Model.QueryDeviceId);
        Assert.Null(test.Model.FilterRoomGroupId);
        Assert.Equal(new[] { "20261002001", "20261002002" }, test.Model.Devices.Select(row => row.ClientNumber));
        var reads = test.Requests.Where(request => request.Path == "/api/v1/devices").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, request =>
        {
            Assert.DoesNotContain("query=", request.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("roomGroupId=", request.Query, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LoadedAuthoritativeGroupCanBeUsedWhenApprovingActivation()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        test.Model.ActivationRequestId = "synthetic-activation-request";
        test.Model.ApprovalRoomGroupId = "room-a";
        await test.Model.ApproveAsync();
        var sent = Assert.Single(test.Requests, x => x.Path == "/api/v1/activation/approve");
        var body = JsonSerializer.Deserialize<ApproveActivationRequest>(sent.Body, JsonDefaults.Create())!;
        Assert.Equal("room-a", body.RoomGroupId);
        Assert.Equal("synthetic-activation-request", body.ActivationRequestId);
        Assert.Contains(test.Model.Devices, x => x.ClientNumber == "20261002003" && x.RoomGroupId == "room-a");
        Assert.Empty(test.Model.ActivationRequestId);
    }

    [Fact]
    public async Task ApprovalFailureOrLateReceiptDoesNotClearAnotherSelectedRequest()
    {
        using var test = new RoomServer();
        var pending = NewResponse();
        test.Intercept = (request, _) => request.Method == HttpMethod.Post ? pending.Task : Task.FromResult<HttpResponseMessage?>(null);
        test.Model.ActivationRequestId = "request-first";
        var approving = test.Model.ApproveAsync();
        test.Model.ActivationRequestId = "request-second";
        pending.SetResult(Ok(new ActivationResult("device-new", "20261002003", new DateOnly(2026, 10, 2), ActivationStatus.Active, null, 1, 1)));
        await approving;
        Assert.Equal("request-second", test.Model.ActivationRequestId);
        Assert.Single(test.Model.Devices);
    }

    [Fact]
    public async Task BatchMoveUsesPublicNumbersAndLoadedVersions_RefreshesAuthoritativeResults()
    {
        using var test = new RoomServer();
        test.Groups.Add(new("room-b", "合成试场B", 1, 0));
        test.Devices.Add(test.Devices[0] with { DeviceId = "device-b", ClientNumber = "20261002002", Version = 4 });
        await test.Model.RefreshDirectoryAsync();
        foreach (var device in test.Model.Devices) device.IsSelected = true;
        test.Model.MoveToRoomGroupId = "room-b";
        await test.Model.MoveSelectedDevicesAsync();
        var sent = Assert.Single(test.Requests, x => x.Path == "/api/v1/rooms/move-devices");
        var body = JsonSerializer.Deserialize<MoveDevicesRequest>(sent.Body, JsonDefaults.Create())!;
        Assert.Equal("考务调整试场", body.Reason);
        Assert.Equal(new[] { "20261002001", "20261002002" }, body.Devices.Select(x => x.ClientNumber));
        Assert.Equal(new long[] { 1, 4 }, body.Devices.Select(x => x.ExpectedVersion));
        Assert.All(test.Model.Devices, x => Assert.Equal("合成试场B", x.RoomGroupName));
        Assert.All(test.Model.Devices, x => Assert.Equal("合成考生甲", x.Candidate));
        Assert.Equal(new long[] { 2, 5 }, test.Model.Devices.Select(x => x.Version));
        Assert.Contains("已完成 2 台", test.Model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedBatchMoveNeverChangesVisibleAssignmentsOptimistically()
    {
        using var test = new RoomServer();
        test.Groups.Add(new("room-b", "合成试场B", 1, 0));
        test.RejectMove = true;
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedDevice = Assert.Single(test.Model.Devices);
        test.Model.MoveToRoomGroupId = "room-b";
        await test.Model.MoveSelectedDevicesAsync();
        Assert.Equal("room-a", Assert.Single(test.Model.Devices).RoomGroupId);
        Assert.StartsWith("操作失败", test.Model.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveUsesCheckedDevicesOrTheSingleHighlightedDeviceWhenNoneAreChecked(bool checkFirstDevice)
    {
        using var test = new RoomServer();
        test.Groups.Add(new("room-b", "合成试场B", 1, 0));
        test.Devices[0] = test.Devices[0] with { ClientNumber = "202610080001", RoomGroupId = null, RoomGroupName = "未分组" };
        test.Devices.Add(test.Devices[0] with { DeviceId = "device-b", ClientNumber = "202610080002" });
        await test.Model.RefreshDirectoryAsync();
        test.Model.Devices[0].IsSelected = checkFirstDevice;
        test.Model.SelectedDevice = test.Model.Devices[1];
        test.Model.SelectedMoveChoice = test.Model.GroupChoices.Single(choice => choice.RoomGroupId == "room-b");

        await test.Model.MoveSelectedDevicesAsync();

        var sent = Assert.Single(test.Requests, request => request.Path == "/api/v1/rooms/move-devices");
        var body = JsonSerializer.Deserialize<MoveDevicesRequest>(sent.Body, JsonDefaults.Create())!;
        var movedNumber = checkFirstDevice ? "202610080001" : "202610080002";
        Assert.Equal(movedNumber, Assert.Single(body.Devices).ClientNumber);
        Assert.Equal("room-b", body.RoomGroupId);
        Assert.Equal("合成试场B", test.Model.Devices.Single(row => row.ClientNumber == movedNumber).RoomGroupName);
        Assert.Equal("未确定试场", test.Model.Devices.Single(row => row.ClientNumber != movedNumber).RoomGroupName);
    }

    [Fact]
    public async Task HistoryUsesSelectedPublicNumberAndDropsLateHistoryForAnotherDevice()
    {
        using var test = new RoomServer();
        test.Devices.Add(test.Devices[0] with { DeviceId = "device-b", ClientNumber = "20261002002" });
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedDevice = test.Model.Devices[0];
        var pending = NewResponse();
        test.Intercept = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/room-history", StringComparison.Ordinal)
            ? pending.Task : Task.FromResult<HttpResponseMessage?>(null);
        var loading = test.Model.LoadSelectedHistoryAsync();
        test.Model.SelectedDevice = test.Model.Devices[1];
        pending.SetResult(Ok(new[] { new DeviceRoomHistory("20261002001", null, "未分组", "room-a", "合成试场A", "synthetic-operator", "合成原因", RoomServer.Now) }));
        await loading;
        Assert.Empty(test.Model.DeviceHistory);
        Assert.Contains(test.Requests, x => x.Path == "/api/v1/devices/20261002001/room-history");
    }

    [Fact]
    public async Task ConnectionResetClearsAllGroupsSelectionsAndIdentityInputs()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedDevice = test.Model.Devices[0];
        test.Model.SelectedGroup = test.Model.Groups[0];
        test.Model.QueryDeviceId = "000001";
        test.Api.Disconnect();
        Assert.Empty(test.Model.Groups);
        Assert.Empty(test.Model.Devices);
        Assert.Empty(test.Model.DeviceHistory);
        Assert.Empty(test.Model.QueryDeviceId);
        Assert.Null(test.Model.SelectedDevice);
        Assert.Null(test.Model.SelectedGroup);
        Assert.False(test.Model.CanEditDirectory);
    }

    [Fact]
    public async Task RefreshPreservesRowsChecksHistoryAndEditedGroupNameWhileUpdatingValues()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        var row = Assert.Single(test.Model.Devices);
        var group = Assert.Single(test.Model.Groups);
        row.IsSelected = true;
        test.Model.SelectedDevice = row;
        test.Model.SelectedGroup = group;
        test.Model.SelectedGroupName = "尚未保存的新名称";
        test.Model.SelectedMoveChoice = test.Model.GroupChoices.Single(choice => choice.RoomGroupId == group.RoomGroupId);
        var choice = test.Model.SelectedMoveChoice;
        test.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/room-history", StringComparison.Ordinal)
            ? Ok(new[] { new DeviceRoomHistory(row.ClientNumber, null, "未分组", group.RoomGroupId, group.Name, "synthetic-operator", "合成转组", RoomServer.Now) }) : null);
        await test.Model.LoadSelectedHistoryAsync();
        var history = Assert.Single(test.Model.DeviceHistory);
        test.Devices[0] = test.Devices[0] with { ConnectionState = DeviceConnectionState.Offline, Page = "已退出", Version = 2 };
        test.Groups[0] = test.Groups[0] with { Name = "服务器更新的试场名", DeviceCount = 2, Version = 2 };

        await test.Model.QueryDeviceAsync();

        Assert.Same(row, Assert.Single(test.Model.Devices));
        Assert.Same(row, test.Model.SelectedDevice);
        Assert.True(row.IsSelected);
        Assert.Equal("离线", row.ConnectionStatus);
        Assert.Equal("已退出", row.Page);
        Assert.Equal(2, row.Version);
        Assert.Same(history, Assert.Single(test.Model.DeviceHistory));
        Assert.Same(group, test.Model.SelectedGroup);
        Assert.Equal(2, group.DeviceCount);
        Assert.Equal("尚未保存的新名称", test.Model.SelectedGroupName);
        Assert.Same(choice, test.Model.SelectedMoveChoice);
        Assert.Equal("服务器更新的试场名", choice!.Name);
    }

    [Fact]
    public async Task UnchangedRefreshDoesNotRebuildCollectionsOrRows()
    {
        using var test = new RoomServer();
        await test.Model.RefreshDirectoryAsync();
        var changes = new List<string>();
        test.Model.Devices.CollectionChanged += (_, _) => changes.Add("devices");
        test.Model.Groups.CollectionChanged += (_, _) => changes.Add("groups");
        test.Model.GroupChoices.CollectionChanged += (_, _) => changes.Add("choices");
        test.Model.FilterChoices.CollectionChanged += (_, _) => changes.Add("filters");
        test.Model.Devices[0].PropertyChanged += (_, _) => changes.Add("device properties");
        test.Model.Groups[0].PropertyChanged += (_, _) => changes.Add("group properties");

        await test.Model.QueryDeviceAsync();
        await test.Model.QueryDeviceAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task RefreshClearsHistoryOnlyWhenSelectedDeviceLeavesTheResults()
    {
        using var test = new RoomServer();
        test.Devices.Add(test.Devices[0] with { DeviceId = "device-b", ClientNumber = "202610080002" });
        await test.Model.RefreshDirectoryAsync();
        var selected = test.Model.Devices[1];
        test.Model.SelectedDevice = selected;
        test.Model.DeviceHistory.Add(new("10-08", "未分组", "合成试场A", "synthetic-operator", "合成转组"));
        test.Devices.RemoveAt(0);
        await test.Model.QueryDeviceAsync();
        Assert.Same(selected, test.Model.SelectedDevice);
        Assert.Single(test.Model.DeviceHistory);
        test.Devices.Clear();
        await test.Model.QueryDeviceAsync();
        Assert.Null(test.Model.SelectedDevice);
        Assert.Empty(test.Model.DeviceHistory);
    }

    [Fact]
    public async Task BackgroundRefreshKeepsControlsAvailableDuringAMove()
    {
        using var test = new RoomServer();
        test.Groups.Add(new("room-b", "合成试场B", 1, 0));
        await test.Model.RefreshDirectoryAsync();
        test.Model.SelectedDevice = test.Model.Devices[0];
        test.Model.MoveToRoomGroupId = "room-b";
        var prior = test.Devices.ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = NewResponse();
        test.Intercept = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/v1/devices") return Task.FromResult<HttpResponseMessage?>(null);
            started.TrySetResult();
            return pending.Task;
        };
        test.Model.StartPolling();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(test.Model.CanEditDirectory);
        Assert.True(test.Model.MoveDevicesCommand.CanExecute(null));
        Assert.True(test.Model.QueryDeviceCommand.CanExecute(null));
        Assert.True(test.Model.RefreshDirectoryCommand.CanExecute(null));
        test.Intercept = null;
        await test.Model.MoveSelectedDevicesAsync();
        Assert.Equal("room-b", Assert.Single(test.Model.Devices).RoomGroupId);
        test.Model.StopPolling();
        pending.SetResult(Ok(new DeviceDirectoryPage(prior, null, RoomServer.Now)));
        await test.Model.QueryDeviceAsync();
        Assert.Equal("room-b", Assert.Single(test.Model.Devices).RoomGroupId);
    }

    private static TaskCompletionSource<HttpResponseMessage?> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Ok<T>(T result) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "room-test", result, null), options: JsonDefaults.Create()) };
    private static HttpResponseMessage Error(string message) => new(HttpStatusCode.Conflict)
    { Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "room-test", null,
        new ApiError("VERSION_CONFLICT", message)), options: JsonDefaults.Create()) };

    private sealed record Sent(string Method, string Path, string Query, string Body);
    private sealed class RoomServer : IDisposable
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(8));
        public List<RoomGroup> Groups { get; } = [new("room-a", "合成试场A", 1, 1)];
        public List<DeviceDirectoryEntry> Devices { get; } = [new("device-a", "20261002001", "room-a", "合成试场A", ActivationStatus.Active,
            DeviceConnectionState.Online, "合成考生甲", "001", "000001", "exam-a", "合成考试", "正在答题", "question-a", Now, 1)];
        public List<Sent> Requests { get; } = [];
        public Func<HttpRequestMessage, string, Task<HttpResponseMessage?>>? Intercept { get; set; }
        public bool RejectMove { get; set; }
        public AdminApiClient Api { get; }
        public ActivationRoomViewModel Model { get; }
        public RoomServer()
        {
            Api = new AdminApiClient(new Handler(RespondAsync));
            Api.Configure("https://synthetic-server.invalid/", "synthetic-session", "synthetic-operator");
            Model = new ActivationRoomViewModel(Api);
        }
        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            Requests.Add(new(request.Method.Method, path, request.RequestUri.Query, body));
            var intercepted = Intercept is null ? null : await Intercept(request, body);
            if (intercepted is not null) return intercepted;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/api/v1/rooms") return Ok(Groups.ToArray());
                if (path == "/api/v1/devices") return Ok(new DeviceDirectoryPage(Devices.ToArray(), null, Now));
                if (path.EndsWith("/room-history", StringComparison.Ordinal)) return Ok(Array.Empty<DeviceRoomHistory>());
                return Ok(Array.Empty<ActivationRequestResult>());
            }
            if (path == "/api/v1/activation/approve")
            {
                var input = JsonSerializer.Deserialize<ApproveActivationRequest>(body, JsonDefaults.Create())!;
                return Ok(new ActivationResult("device-new", "20261002003", new DateOnly(2026, 10, 2), ActivationStatus.Active, input.RoomGroupId, 1, 1));
            }
            if (path == "/api/v1/rooms" && request.Method == HttpMethod.Post)
            {
                var input = JsonSerializer.Deserialize<CreateRoomGroupRequest>(body, JsonDefaults.Create())!;
                if (Groups.Any(x => x.Name == input.Name)) return Error("试场名称重复。");
                var result = new RoomGroup("server-created-room", input.Name, 1, 0);
                Groups.Add(result); return Ok(result);
            }
            if (path == "/api/v1/rooms/move-devices")
            {
                if (RejectMove) return Error("其中一台设备版本已变化，整批未转组。");
                var input = JsonSerializer.Deserialize<MoveDevicesRequest>(body, JsonDefaults.Create())!;
                foreach (var target in input.Devices)
                {
                    var index = Devices.FindIndex(x => x.ClientNumber == target.ClientNumber);
                    Assert.Equal(target.ExpectedVersion, Devices[index].Version);
                    Devices[index] = Devices[index] with { RoomGroupId = input.RoomGroupId,
                        RoomGroupName = Groups.FirstOrDefault(x => x.GroupId == input.RoomGroupId)?.Name ?? "未分组", Version = Devices[index].Version + 1 };
                }
                return Ok(new MoveDevicesResult(input.Devices.Count, input.Devices.Select(x => x.ClientNumber).ToArray()));
            }
            var id = path.Split('/').Last();
            var indexOfGroup = Groups.FindIndex(x => x.GroupId == id);
            if (request.Method == HttpMethod.Put)
            {
                var input = JsonSerializer.Deserialize<RenameRoomGroupRequest>(body, JsonDefaults.Create())!;
                Assert.Equal(input.ExpectedVersion, Groups[indexOfGroup].Version);
                Groups[indexOfGroup] = Groups[indexOfGroup] with { Name = input.Name, Version = input.ExpectedVersion + 1 };
                return Ok(Groups[indexOfGroup]);
            }
            if (request.Method == HttpMethod.Delete)
            {
                var input = JsonSerializer.Deserialize<DeleteRoomGroupRequest>(body, JsonDefaults.Create())!;
                Assert.Equal(input.ExpectedVersion, Groups[indexOfGroup].Version);
                if (Groups[indexOfGroup].DeviceCount > 0) return Error("试场中还有设备，不能删除。");
                Groups.RemoveAt(indexOfGroup); return Ok(new DeletedRoomGroup(id, true));
            }
            throw new InvalidOperationException("Unexpected synthetic request.");
        }
        private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
        public void Dispose() { Model.Dispose(); Api.Dispose(); }
    }
}
