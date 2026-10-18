namespace Exam.Admin.ServiceManagement;

public sealed class ManagementSessionSnapshot
{
    internal ManagementSessionSnapshot(ManagementTarget target, string username, string credential)
    { Target = target; Username = username; Credential = credential; }
    public ManagementTarget Target { get; }
    public string Username { get; }
    internal string Credential { get; }
}
