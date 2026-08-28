using Titanium.Plus;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.State;

/// <summary>Stretch: Redis-backed stick tables / rate limits (opt-in middleware).</summary>
public sealed class SharedStateStore
{
    public static SharedStateStore? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("state.redis", out var redis) || string.IsNullOrWhiteSpace(redis))
        {
            return null;
        }

        PlusLog.Info(context, $"Plus State: redis={redis} (middleware registration deferred until Redis client is configured).");
        _ = context;
        return new SharedStateStore();
    }
}

/// <summary>Legacy stub type name.</summary>
public sealed class StatePlaceholder;
