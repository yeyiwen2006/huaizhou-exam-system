using Exam.Contracts.Activation;

namespace Exam.Domain.Activation;

public sealed record DeviceRegistration(
    string DeviceId,
    string ClientNumber,
    DateOnly ActivationDate,
    ActivationStatus Status,
    string? RoomGroupId,
    string ProtectedCredential,
    long CredentialVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    long Version)
{
    public DeviceRegistration Revoke(DateTimeOffset at) =>
        this with { Status = ActivationStatus.Revoked, LastSeenAt = at, Version = checked(Version + 1) };

    public DeviceRegistration Reactivate(DateTimeOffset at) =>
        this with { Status = ActivationStatus.Active, LastSeenAt = at, Version = checked(Version + 1) };
}

public sealed record DeviceActivationRequest(
    string ActivationRequestId,
    string DeviceId,
    string ProtectedCredential,
    ActivationStatus Status,
    DateTimeOffset RequestedAt,
    string? ClientNumber,
    long CredentialVersion);

public sealed record ActivationIdempotencyRecord(
    string RequestId,
    string Operation,
    string PayloadHash,
    string ResponseType,
    string ResponseJson);
