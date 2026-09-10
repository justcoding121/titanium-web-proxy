namespace Titanium.Plus.Resilience;

/// <summary>Policy helpers for opt-in idempotent retries (connection / next-destination).</summary>
public static class IdempotentRetryPolicy
{
    private static readonly HashSet<string> IdempotentMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE",
    };

    public static bool IsIdempotent(string? method) =>
        !string.IsNullOrEmpty(method) && IdempotentMethods.Contains(method);

    /// <summary>True when another attempt is allowed (0-based <paramref name="attempt"/> already used).</summary>
    public static bool ShouldRetry(string? method, int attempt, int maxAttempts) =>
        maxAttempts > 0 && attempt < maxAttempts && IsIdempotent(method);
}
