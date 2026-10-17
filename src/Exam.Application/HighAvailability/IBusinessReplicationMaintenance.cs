namespace Exam.Application.HighAvailability;

public interface IBusinessReplicationMaintenance
{
    Task MaintainReplicationAsync(CancellationToken token);
}
