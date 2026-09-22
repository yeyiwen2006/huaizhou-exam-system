using System.Security.Cryptography;

namespace Exam.Application.Activation;

public interface IDeviceCredentialProtector
{
    string Protect(string credential);

    bool Verify(string credential, string protectedCredential);
}

public sealed class Pbkdf2DeviceCredentialProtector : IDeviceCredentialProtector
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public string Protect(string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            credential,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return $"pbkdf2-sha256.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string credential, string protectedCredential)
    {
        if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(protectedCredential))
        {
            return false;
        }

        var parts = protectedCredential.Split('.', StringSplitOptions.None);
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (iterations < Iterations || salt.Length < SaltSize || expected.Length < HashSize)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                credential,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public interface IDeviceCredentialValidator
{
    ValueTask<bool> ValidateActiveDeviceAsync(
        string deviceId,
        string credential,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsActiveDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default);
}
