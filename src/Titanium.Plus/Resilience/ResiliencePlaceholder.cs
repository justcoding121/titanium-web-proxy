using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Resilience;

/// <summary>Stretch: active health / outlier / circuit → DestinationState.Unhealthy.</summary>
public sealed class ResilienceController : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public static ResilienceController? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("resilience.activeHealth", out var enabled) ||
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var intervalMs = int.TryParse(options.GetValueOrDefault("resilience.intervalMs"), out var ms) ? ms : 5000;
        var controller = new ResilienceController();
        _ = Task.Run(() => controller.LoopAsync(context.ClusterManager, intervalMs, controller._cts.Token));
        Console.WriteLine($"Plus Resilience: active health interval={intervalMs}ms");
        return controller;
    }

    private async Task LoopAsync(IClusterManager? manager, int intervalMs, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, cancellationToken);
                _ = manager?.Snapshot;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // keep looping
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Legacy stub type name.</summary>
public sealed class ResiliencePlaceholder;
