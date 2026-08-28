using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Discovery;

/// <summary>Stretch: Docker / Kubernetes label watch → cluster ApplyAsync.</summary>
public sealed class ServiceDiscovery
{
    public static ServiceDiscovery? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.mode", out var mode) || string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        Console.WriteLine($"Plus Discovery: mode={mode} (watch not yet connected — ApplyAsync ready via control plane).");
        _ = context;
        return new ServiceDiscovery();
    }
}

/// <summary>Legacy stub type name.</summary>
public sealed class DiscoveryPlaceholder;
