namespace Exam.Contracts.ServiceManagement;

public static class ManagementTimeouts
{
    public static TimeSpan Read => TimeSpan.FromSeconds(150);
    public static TimeSpan Write => TimeSpan.FromMinutes(155);
    public static TimeSpan RebuildCopy => TimeSpan.FromMinutes(120);
    public static TimeSpan HighAvailabilityOperation => TimeSpan.FromMinutes(150);
    public static TimeSpan ReplicationInactivity => TimeSpan.FromMinutes(10);
}
