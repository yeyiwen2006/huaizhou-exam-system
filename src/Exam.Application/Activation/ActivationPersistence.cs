using Exam.Domain.Activation;

namespace Exam.Application.Activation;

public interface IActivationPersistence
{
    ValueTask<T> ExecuteTransactionAsync<T>(
        Func<IActivationTransaction, T> operation,
        CancellationToken cancellationToken = default);
}

public interface IActivationTransaction
{
    DeviceRegistration? FindDevice(string deviceId);

    DeviceActivationRequest? FindActivationRequest(string activationRequestId);

    DeviceActivationRequest? FindLatestActivationRequestForDevice(string deviceId);

    int GetLastDailySequence(DateOnly date);

    ActivationIdempotencyRecord? FindIdempotencyRecord(string requestId);

    void SaveDevice(DeviceRegistration device);

    void SaveActivationRequest(DeviceActivationRequest request);

    void SetLastDailySequence(DateOnly date, int sequence);

    void SaveIdempotencyRecord(ActivationIdempotencyRecord record);
}

/// <summary>
/// Thread-safe transactional reference adapter. It is intended for tests and local
/// development; production must implement <see cref="IActivationPersistence"/> on
/// durable storage with equivalent uniqueness and transaction boundaries.
/// </summary>
public sealed class InMemoryActivationPersistence : IActivationPersistence
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State _state = new();

    public async ValueTask<T> ExecuteTransactionAsync<T>(
        Func<IActivationTransaction, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var working = _state.Clone();
            var result = operation(new Transaction(working));
            _state = working;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class State
    {
        public Dictionary<string, DeviceRegistration> Devices { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, DeviceActivationRequest> Requests { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<DateOnly, int> DailySequences { get; init; } = [];
        public Dictionary<string, ActivationIdempotencyRecord> Idempotency { get; init; } = new(StringComparer.Ordinal);

        public State Clone() => new()
        {
            Devices = new Dictionary<string, DeviceRegistration>(Devices, StringComparer.Ordinal),
            Requests = new Dictionary<string, DeviceActivationRequest>(Requests, StringComparer.Ordinal),
            DailySequences = new Dictionary<DateOnly, int>(DailySequences),
            Idempotency = new Dictionary<string, ActivationIdempotencyRecord>(Idempotency, StringComparer.Ordinal)
        };
    }

    private sealed class Transaction(State state) : IActivationTransaction
    {
        public DeviceRegistration? FindDevice(string deviceId) =>
            state.Devices.GetValueOrDefault(deviceId);

        public DeviceActivationRequest? FindActivationRequest(string activationRequestId) =>
            state.Requests.GetValueOrDefault(activationRequestId);

        public DeviceActivationRequest? FindLatestActivationRequestForDevice(string deviceId) =>
            state.Requests.Values
                .Where(request => StringComparer.Ordinal.Equals(request.DeviceId, deviceId))
                .OrderByDescending(request => request.RequestedAt)
                .FirstOrDefault();

        public int GetLastDailySequence(DateOnly date) => state.DailySequences.GetValueOrDefault(date);

        public ActivationIdempotencyRecord? FindIdempotencyRecord(string requestId) =>
            state.Idempotency.GetValueOrDefault(requestId);

        public void SaveDevice(DeviceRegistration device) => state.Devices[device.DeviceId] = device;

        public void SaveActivationRequest(DeviceActivationRequest request) =>
            state.Requests[request.ActivationRequestId] = request;

        public void SetLastDailySequence(DateOnly date, int sequence) => state.DailySequences[date] = sequence;

        public void SaveIdempotencyRecord(ActivationIdempotencyRecord record) =>
            state.Idempotency[record.RequestId] = record;
    }
}
