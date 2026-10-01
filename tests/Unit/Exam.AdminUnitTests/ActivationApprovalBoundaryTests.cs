using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;

namespace Exam.AdminUnitTests;

public sealed class ActivationApprovalBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task BlankGroup_UsesOfficialApprovalWithNullGroup(string? groupId)
    {
        using var handler = new ApprovalHandler();
        using var api = CreateApi(handler);
        var viewModel = new ActivationRoomViewModel(api)
        {
            ActivationRequestId = "  synthetic-request  ",
            ApprovalRoomGroupId = groupId!
        };

        await ExecuteAsync(viewModel);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("synthetic-request", handler.Request!.ActivationRequestId);
        Assert.Null(handler.Request.RoomGroupId);
        Assert.False(string.IsNullOrWhiteSpace(handler.Request.RequestId));
        var device = Assert.Single(viewModel.Devices);
        Assert.Equal("synthetic-device", device.DeviceId);
        Assert.Equal("20260928001", device.ClientNumber);
        Assert.Equal(string.Empty, device.RoomGroupId);
        Assert.StartsWith("已批准", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unverified-group")]
    [InlineData("  unverified-group  ")]
    public async Task UnverifiedGroup_WithConfiguredConnection_NeverSendsRequest(string groupId)
    {
        using var handler = new ApprovalHandler();
        using var api = CreateApi(handler);
        var viewModel = new ActivationRoomViewModel(api)
        {
            ActivationRequestId = "synthetic-request",
            ApprovalRoomGroupId = groupId
        };

        await ExecuteAsync(viewModel);

        Assert.Equal(0, handler.Calls);
        Assert.Empty(viewModel.Devices);
        Assert.Contains("试场分组暂不能在审批时指定", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalGroupDraft_IsNotAnAuthoritativeGroup()
    {
        using var handler = new ApprovalHandler();
        using var api = CreateApi(handler);
        var viewModel = new ActivationRoomViewModel(api)
        {
            ActivationRequestId = "synthetic-request",
            NewGroupName = "合成试场"
        };
        viewModel.AddGroupCommand.Execute(null);
        viewModel.ApprovalRoomGroupId = Assert.Single(viewModel.Groups).RoomGroupId;

        await ExecuteAsync(viewModel);

        Assert.Equal(0, handler.Calls);
        Assert.Empty(viewModel.Devices);
    }

    [Fact]
    public async Task RepeatedApproval_PreservesAuthoritativeExistingDeviceGroup()
    {
        using var handler = new ApprovalHandler { ReturnedGroup = "server-existing-group" };
        using var api = CreateApi(handler);
        var viewModel = new ActivationRoomViewModel(api) { ActivationRequestId = "synthetic-request" };

        await ExecuteAsync(viewModel);
        Assert.Empty(viewModel.ActivationRequestId);
        // 审批成功会清空输入框；模拟再次查询并重放同一申请，不能把空输入当作重复审批。
        viewModel.ActivationRequestId = "synthetic-request";
        await ExecuteAsync(viewModel);

        Assert.Equal(2, handler.Calls);
        Assert.Null(handler.Request!.RoomGroupId);
        Assert.Equal("server-existing-group", Assert.Single(viewModel.Devices).RoomGroupId);
    }

    [Fact]
    public async Task RejectedApproval_DoesNotAddDeviceOrReportSuccess()
    {
        using var handler = new ApprovalHandler { Reject = true };
        using var api = CreateApi(handler);
        var viewModel = new ActivationRoomViewModel(api) { ActivationRequestId = "synthetic-request" };

        await ExecuteAsync(viewModel);

        Assert.Equal(1, handler.Calls);
        Assert.Empty(viewModel.Devices);
        Assert.StartsWith("操作失败", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("已批准", viewModel.Status, StringComparison.Ordinal);
    }

    private static AdminApiClient CreateApi(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    private static async Task ExecuteAsync(ActivationRoomViewModel viewModel)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, EventArgs args)
        {
            if (viewModel.ApproveCommand.CanExecute(null)) completed.TrySetResult();
        }
        viewModel.ApproveCommand.CanExecuteChanged += OnCompleted;
        try
        {
            viewModel.ApproveCommand.Execute(null);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            viewModel.ApproveCommand.CanExecuteChanged -= OnCompleted;
        }
    }

    private sealed class ApprovalHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public ApproveActivationRequest? Request { get; private set; }
        public string? ReturnedGroup { get; init; }
        public bool Reject { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/activation/approve", request.RequestUri!.AbsolutePath);
            Request = await request.Content!.ReadFromJsonAsync<ApproveActivationRequest>(
                JsonDefaults.Create(), cancellationToken);
            return new HttpResponseMessage(Reject ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<ActivationResult>(
                    "synthetic-server", "synthetic-node", 1, "test",
                    Reject ? null : new ActivationResult("synthetic-device", "20260928001",
                        new DateOnly(2026, 9, 28), ActivationStatus.Active, ReturnedGroup, 1, 1),
                    Reject ? new ApiError("FORBIDDEN", "Synthetic rejection") : null),
                    options: JsonDefaults.Create())
            };
        }
    }
}
