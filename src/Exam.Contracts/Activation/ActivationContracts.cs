namespace Exam.Contracts.Activation;

public enum ActivationStatus
{
    Pending,
    Active,
    Revoked,
    Rejected
}

public sealed record SubmitActivationRequest(
    string RequestId,
    string DeviceId,
    string Credential);

public sealed record ActivationRequestResult(
    string ActivationRequestId,
    string DeviceId,
    ActivationStatus Status,
    string? ClientNumber,
    long CredentialVersion,
    DateTimeOffset RequestedAt);

public sealed record ApproveActivationRequest(
    string RequestId,
    string ActivationRequestId,
    string? RoomGroupId = null);

public sealed record RevokeActivationRequest(
    string RequestId,
    string DeviceId,
    string Reason);

public sealed record ReactivateDeviceRequest(
    string RequestId,
    string DeviceId,
    string Credential);

public sealed record ActivationResult(
    string DeviceId,
    string ClientNumber,
    DateOnly ActivationDate,
    ActivationStatus Status,
    string? RoomGroupId,
    long CredentialVersion,
    long Version);

public static class ActivationErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string InvalidCredential = "INVALID_CREDENTIAL";
    public const string ActivationRequestNotFound = "ACTIVATION_REQUEST_NOT_FOUND";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string NotActivated = "NOT_ACTIVATED";
    public const string AlreadyActive = "ALREADY_ACTIVE";
    public const string DailySequenceExhausted = "DAILY_SEQUENCE_EXHAUSTED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
}
