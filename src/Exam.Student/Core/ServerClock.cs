using Exam.Contracts.Commands;

namespace Exam.Student.Core;

/// <summary>Advances the last authenticated server time using a monotonic timestamp.</summary>
public sealed class ServerClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset? _serverTime;
    private long _timestamp;

    public bool IsSynchronized { get { lock (_gate) { return _serverTime is not null; } } }
    public ServerNodeEnvelope? Server { get; private set; }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _serverTime is { } value
                    ? value + _time.GetElapsedTime(_timestamp, _time.GetTimestamp())
                    : throw new InvalidOperationException("服务器时间尚未同步。");
            }
        }
    }

    public async Task SynchronizeAsync(IStudentApi api, CancellationToken ct = default)
    {
        var started = _time.GetTimestamp();
        var envelope = await api.GetServerTimeAsync(ct);
        var snapshot = HttpStudentApi.RequireResult(envelope);
        if (snapshot.Server is null || snapshot.Server.ServerId != envelope.ServerId ||
            snapshot.Server.NodeId != envelope.NodeId || snapshot.Server.ServerEpoch != envelope.ServerEpoch)
        {
            throw new InvalidOperationException("校时响应中的服务器身份不一致。");
        }
        var received = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(started, received);
        if (elapsed > TimeSpan.FromSeconds(10))
        {
            throw new InvalidOperationException("校时响应超时，保留上一次可靠时间。");
        }
        lock (_gate)
        {
            _serverTime = snapshot.ServerTime + elapsed / 2;
            _timestamp = received;
            Server = snapshot.Server;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _serverTime = null;
            Server = null;
        }
    }

}
