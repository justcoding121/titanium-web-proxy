using Titanium.Plus;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Plus.Resilience;

/// <summary>
/// Opt-in bounded connection retries for idempotent methods only
/// (<c>resilience.retry.idempotentAttempts</c>), via per-session
/// <see cref="SessionEventArgs.NetworkFailureRetryAttempts"/>.
/// </summary>
public static class IdempotentRetryGuard
{
    public static IdempotentRetryGuardMarker? TryStart(
        PlusActivationContext context,
        IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("resilience.retry.idempotentAttempts", out var raw) ||
            !int.TryParse(raw, out var attempts) ||
            attempts <= 0)
        {
            return null;
        }

        attempts = Math.Clamp(attempts, 1, 5);
        if (context.ProxyServer is not ProxyServer proxy)
        {
            return null;
        }

        var marker = new IdempotentRetryGuardMarker(attempts);
        proxy.BeforeRequest += (_, e) =>
        {
            if (IdempotentRetryPolicy.IsIdempotent(e.HttpClient.Request.Method))
            {
                e.NetworkFailureRetryAttempts = marker.Attempts;
            }
            else
            {
                // Non-idempotent: do not enlarge connection retries beyond a single attempt.
                e.NetworkFailureRetryAttempts = 0;
            }

            return Task.CompletedTask;
        };

        PlusLog.Info(context,
            $"Plus Retry: idempotent NetworkFailureRetryAttempts={attempts} (GET/HEAD/OPTIONS/TRACE only)");
        return marker;
    }
}

/// <summary>Configured idempotent retry attempts.</summary>
public sealed class IdempotentRetryGuardMarker
{
    public IdempotentRetryGuardMarker(int attempts) => Attempts = attempts;
    public int Attempts { get; }
}
