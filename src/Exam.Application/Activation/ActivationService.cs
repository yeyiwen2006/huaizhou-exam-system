using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Domain.Activation;

namespace Exam.Application.Activation;

public sealed class ActivationService(
    IActivationPersistence persistence,
    IDeviceCredentialProtector credentialProtector,
    TimeProvider? timeProvider = null) : IDeviceCredentialValidator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask<ActivationRequestResult> SubmitRequestAsync(
        SubmitActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.DeviceId, nameof(request.DeviceId));
        Require(request.Credential, nameof(request.Credential));
        var protectedCredential = credentialProtector.Protect(request.Credential);
        var now = _timeProvider.GetUtcNow();

        return persistence.ExecuteTransactionAsync(transaction =>
            RunIdempotent(transaction, request.RequestId, "activation.submit", request, () =>
            {
                var device = transaction.FindDevice(request.DeviceId);
                if (device is not null)
                {
                    EnsureCredential(request.Credential, device.ProtectedCredential);
                    return new ActivationRequestResult(
                        transaction.FindLatestActivationRequestForDevice(request.DeviceId)?.ActivationRequestId
                            ?? $"restored-{device.DeviceId}",
                        device.DeviceId,
                        device.Status,
                        device.ClientNumber,
                        device.CredentialVersion,
                        device.CreatedAt);
                }

                var existing = transaction.FindLatestActivationRequestForDevice(request.DeviceId);
                if (existing is not null)
                {
                    EnsureCredential(request.Credential, existing.ProtectedCredential);
                    return ToRequestResult(existing);
                }

                var activationRequest = new DeviceActivationRequest(
                    Guid.NewGuid().ToString("N"),
                    request.DeviceId,
                    protectedCredential,
                    ActivationStatus.Pending,
                    now,
                    null,
                    1);
                transaction.SaveActivationRequest(activationRequest);
                return ToRequestResult(activationRequest);
            }), cancellationToken);
    }

    public ValueTask<ActivationResult> ApproveAsync(
        ApproveActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.ActivationRequestId, nameof(request.ActivationRequestId));
        var now = _timeProvider.GetUtcNow();
        var activationDate = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);

        return persistence.ExecuteTransactionAsync(transaction =>
            RunIdempotent(transaction, request.RequestId, "activation.approve", request, () =>
            {
                var activationRequest = transaction.FindActivationRequest(request.ActivationRequestId)
                    ?? throw Error(ActivationErrorCodes.ActivationRequestNotFound, "找不到激活申请。");
                var existing = transaction.FindDevice(activationRequest.DeviceId);
                if (existing is not null)
                {
                    if (existing.Status == ActivationStatus.Revoked)
                    {
                        throw Error(ActivationErrorCodes.NotActivated, "设备已撤销，须核验原凭据后重新激活。");
                    }

                    return ToResult(existing);
                }

                var nextSequence = checked(transaction.GetLastDailySequence(activationDate) + 1);
                if (nextSequence > 999)
                {
                    throw Error(ActivationErrorCodes.DailySequenceExhausted, "服务器当日设备编号已用尽。");
                }

                var clientNumber = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{activationDate:yyyyMMdd}{nextSequence:000}");
                var device = new DeviceRegistration(
                    activationRequest.DeviceId,
                    clientNumber,
                    activationDate,
                    ActivationStatus.Active,
                    NormalizeOptional(request.RoomGroupId),
                    activationRequest.ProtectedCredential,
                    activationRequest.CredentialVersion,
                    now,
                    now,
                    1);

                transaction.SetLastDailySequence(activationDate, nextSequence);
                transaction.SaveDevice(device);
                transaction.SaveActivationRequest(activationRequest with
                {
                    Status = ActivationStatus.Active,
                    ClientNumber = clientNumber
                });
                return ToResult(device);
            }), cancellationToken);
    }

    public ValueTask<ActivationResult> RevokeAsync(
        RevokeActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.DeviceId, nameof(request.DeviceId));
        Require(request.Reason, nameof(request.Reason));
        var now = _timeProvider.GetUtcNow();

        return persistence.ExecuteTransactionAsync(transaction =>
            RunIdempotent(transaction, request.RequestId, "activation.revoke", request, () =>
            {
                var device = transaction.FindDevice(request.DeviceId)
                    ?? throw Error(ActivationErrorCodes.DeviceNotFound, "找不到设备登记。");
                if (device.Status == ActivationStatus.Revoked)
                {
                    return ToResult(device);
                }

                var revoked = device.Revoke(now);
                transaction.SaveDevice(revoked);
                return ToResult(revoked);
            }), cancellationToken);
    }

    public ValueTask<ActivationResult> ReactivateAsync(
        ReactivateDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.DeviceId, nameof(request.DeviceId));
        Require(request.Credential, nameof(request.Credential));
        var now = _timeProvider.GetUtcNow();

        return persistence.ExecuteTransactionAsync(transaction =>
            RunIdempotent(transaction, request.RequestId, "activation.reactivate", request, () =>
            {
                var device = transaction.FindDevice(request.DeviceId)
                    ?? throw Error(ActivationErrorCodes.DeviceNotFound, "找不到设备登记。");
                EnsureCredential(request.Credential, device.ProtectedCredential);
                if (device.Status == ActivationStatus.Active)
                {
                    return ToResult(device);
                }

                var reactivated = device.Reactivate(now);
                transaction.SaveDevice(reactivated);
                return ToResult(reactivated);
            }), cancellationToken);
    }

    public ValueTask<bool> ValidateActiveDeviceAsync(
        string deviceId,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(credential))
        {
            return ValueTask.FromResult(false);
        }

        return persistence.ExecuteTransactionAsync(transaction =>
        {
            var device = transaction.FindDevice(deviceId);
            return device is { Status: ActivationStatus.Active } &&
                   credentialProtector.Verify(credential, device.ProtectedCredential);
        }, cancellationToken);
    }

    public ValueTask<bool> IsActiveDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return ValueTask.FromResult(false);
        }

        return persistence.ExecuteTransactionAsync(
            transaction => transaction.FindDevice(deviceId) is { Status: ActivationStatus.Active },
            cancellationToken);
    }

    public ValueTask<ActivationResult?> FindDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        persistence.ExecuteTransactionAsync(
            transaction => transaction.FindDevice(deviceId) is { } device ? ToResult(device) : null,
            cancellationToken);

    private static T RunIdempotent<T>(
        IActivationTransaction transaction,
        string requestId,
        string operation,
        object payload,
        Func<T> execute)
    {
        var payloadHash = HashPayload(payload);
        var previous = transaction.FindIdempotencyRecord(requestId);
        if (previous is not null)
        {
            if (previous.Operation != operation || previous.PayloadHash != payloadHash)
            {
                throw Error(ActivationErrorCodes.IdempotencyConflict, "requestId 已用于另一项写请求或不同参数。");
            }

            return JsonSerializer.Deserialize<T>(previous.ResponseJson)
                ?? throw new InvalidOperationException("无法读取已保存的幂等响应。");
        }

        var result = execute();
        transaction.SaveIdempotencyRecord(new ActivationIdempotencyRecord(
            requestId,
            operation,
            payloadHash,
            typeof(T).FullName ?? typeof(T).Name,
            JsonSerializer.Serialize(result)));
        return result;
    }

    private static string HashPayload(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private void EnsureCredential(string credential, string protectedCredential)
    {
        if (!credentialProtector.Verify(credential, protectedCredential))
        {
            throw Error(ActivationErrorCodes.InvalidCredential, "设备凭据不匹配。");
        }
    }

    private static ActivationRequestResult ToRequestResult(DeviceActivationRequest request) => new(
        request.ActivationRequestId,
        request.DeviceId,
        request.Status,
        request.ClientNumber,
        request.CredentialVersion,
        request.RequestedAt);

    private static ActivationResult ToResult(DeviceRegistration device) => new(
        device.DeviceId,
        device.ClientNumber,
        device.ActivationDate,
        device.Status,
        device.RoomGroupId,
        device.CredentialVersion,
        device.Version);

    private static void ValidateRequestId(string requestId) => Require(requestId, nameof(requestId));

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Error(ActivationErrorCodes.InvalidRequest, $"{field} 不能为空。");
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ActivationRuleException Error(string code, string message) => new(code, message);
}
