namespace Exam.Admin.ServiceManagement;

public enum AdminWorkspaceRoute { BusinessLogin, BusinessWorkspace, LocalManagement }

public sealed class AdminWorkspaceRouter
{
    private bool _businessConnected;
    public AdminWorkspaceRoute Route { get; private set; } = AdminWorkspaceRoute.BusinessLogin;
    public void BusinessConnectionChanged(bool connected)
    {
        _businessConnected = connected;
        if (Route != AdminWorkspaceRoute.LocalManagement) ReturnToBusiness();
    }
    public void OpenManagement() => Route = AdminWorkspaceRoute.LocalManagement;
    public void ReturnToBusiness() => Route = _businessConnected ? AdminWorkspaceRoute.BusinessWorkspace : AdminWorkspaceRoute.BusinessLogin;
}
