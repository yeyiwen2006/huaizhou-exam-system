using Exam.Admin.Services;
using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed class ActivationRoomViewModelTests
{
    [Fact]
    public void LocalGroupDraft_RenamePreservesId_AndRejectsNonemptyDeletion()
    {
        using var api = new AdminApiClient();
        var viewModel = new ActivationRoomViewModel(api) { NewGroupName = "合成试场一" };
        viewModel.AddGroupCommand.Execute(null);
        var group = Assert.Single(viewModel.Groups);

        viewModel.SelectedGroupName = "合成试场二";
        viewModel.RenameGroupCommand.Execute(null);
        Assert.Equal(group.RoomGroupId, Assert.Single(viewModel.Groups).RoomGroupId);
        Assert.Equal("合成试场二", viewModel.SelectedGroup!.Name);

        viewModel.Devices.Add(new ActivationDeviceRow("device-1", "20260923001", "Active",
            group.RoomGroupId, "接口未提供", "接口未提供", "接口未提供"));
        viewModel.RemoveGroupCommand.Execute(null);
        Assert.Single(viewModel.Groups);
        Assert.Contains("不能删除", viewModel.Status, StringComparison.Ordinal);

        viewModel.Devices.Clear();
        viewModel.RemoveGroupCommand.Execute(null);
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public void LocalGroupDraft_RejectsDuplicateNames()
    {
        using var api = new AdminApiClient();
        var viewModel = new ActivationRoomViewModel(api) { NewGroupName = "合成试场" };
        viewModel.AddGroupCommand.Execute(null);
        viewModel.NewGroupName = "合成试场";

        viewModel.AddGroupCommand.Execute(null);

        Assert.Single(viewModel.Groups);
    }
}
