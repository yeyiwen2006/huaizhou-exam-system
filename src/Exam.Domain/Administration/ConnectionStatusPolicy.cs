using Exam.Contracts.Administration;

namespace Exam.Domain.Administration;

public static class ConnectionStatusPolicy
{
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(135);
    public static DeviceConnectionState Evaluate(DateTimeOffset now,DateTimeOffset? lastSeen,
        DateTimeOffset? disconnected,bool recovering)
    {
        if(lastSeen is null)return DeviceConnectionState.Unknown;
        if(disconnected is not null || now-lastSeen.Value>=OfflineAfter)return DeviceConnectionState.Offline;
        return recovering?DeviceConnectionState.Recovering:DeviceConnectionState.Online;
    }
}
