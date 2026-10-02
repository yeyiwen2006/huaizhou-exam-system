namespace Exam.Contracts.Activation;

/// <summary>A short, non-secret locator for manually matching a pending activation request. Never an authentication credential.</summary>
public static class ActivationDisplay
{
    public static string RequestCode(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return string.Empty;
        var text = requestId.Trim();
        return (text.Length > 8 ? text[^8..] : text).ToUpperInvariant();
    }
}
