using Exam.Admin.Services;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed class ActivationRoomViewModelTests
{
    [Fact]
    public void DisconnectedDirectoryCannotPretendToSaveLocalGroups()
    {
        using var api = new AdminApiClient();
        using var viewModel = new ActivationRoomViewModel(api) { NewGroupName = "合成试场" };
        Assert.False(viewModel.AddGroupCommand.CanExecute(null));
        viewModel.AddGroupCommand.Execute(null);
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public void ConnectionResetClearsEvenUnverifiedGroupDrafts()
    {
        using var api = new AdminApiClient();
        using var viewModel = new ActivationRoomViewModel(api);
        viewModel.Groups.Add(new RoomGroupDraft("synthetic-draft", "合成旧试场"));
        viewModel.NewGroupName = "旧服务器输入";
        api.Disconnect();
        Assert.Empty(viewModel.Groups);
        Assert.Empty(viewModel.NewGroupName);
    }

    [Theory]
    [InlineData("unverified-group")]
    [InlineData("  unverified-group  ")]
    public async Task ApprovalRejectsUnverifiedGroupBeforeAnyRequest(string groupId)
    {
        using var api = new AdminApiClient();
        using var viewModel = new ActivationRoomViewModel(api)
        { ActivationRequestId = "synthetic-request", ApprovalRoomGroupId = groupId };
        await viewModel.ApproveAsync();
        Assert.Contains("尚未从服务器读取", viewModel.Status, StringComparison.Ordinal);
        Assert.Empty(viewModel.Devices);
    }
}
