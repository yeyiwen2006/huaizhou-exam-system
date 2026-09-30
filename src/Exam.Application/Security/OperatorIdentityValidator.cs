using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Exam.Application.Security;

/// <summary>Shared operator identity rules; no database or active-node dependency.</summary>
public static class OperatorIdentityValidator
{
    public static bool IsConfigured(string? token, string? operatorId) =>
        !string.IsNullOrWhiteSpace(token) && token.Length >= 32 && !string.IsNullOrWhiteSpace(operatorId);

    public static bool Authenticate(string? authorization, string? token, string? operatorId)
    {
        if (!IsConfigured(token, operatorId) || authorization is null ||
            !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var supplied = Encoding.UTF8.GetBytes(authorization[7..]);
        var expected = Encoding.UTF8.GetBytes(token!);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    public static bool HasMatchingActor(JsonElement value, string operatorId)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            if (new[] { "actorId", "operatorId", "operator" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != operatorId)) return false;
            if (property.Value.ValueKind == JsonValueKind.Object && !HasMatchingActor(property.Value, operatorId)) return false;
            if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && !HasMatchingActor(item, operatorId)) return false;
        }
        return true;
    }
}
