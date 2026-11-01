using System.Net;
using System.Text.Json;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Config;

internal readonly record struct ListenerKey(string Type, string Host, int Port)
{
    public static ListenerKey From(ListenerConfig listener)
    {
        var type = string.IsNullOrWhiteSpace(listener.Type)
            ? string.IsNullOrEmpty(listener.ForwardHost) ? "explicit" : "transparent"
            : listener.Type.Trim().ToLowerInvariant();
        var host = listener.Host ?? "0.0.0.0";
        return new ListenerKey(type, host, listener.Port);
    }
}

internal enum ListenerPlanAction
{
    Keep,
    Add,
    Remove,
    Replace,
}

internal sealed record ListenerPlanEntry(ListenerKey Key, ListenerPlanAction Action, ListenerConfig? Desired);

internal static class ListenerReconciler
{
    internal static IReadOnlyList<ListenerPlanEntry> Plan(
        IReadOnlyDictionary<ListenerKey, ListenerConfig> running,
        IReadOnlyList<ListenerConfig> desired)
    {
        var desiredMap = desired.ToDictionary(ListenerKey.From, c => c);
        var plan = new List<ListenerPlanEntry>();

        foreach (var (key, config) in desiredMap)
        {
            if (!running.TryGetValue(key, out var existing))
            {
                plan.Add(new ListenerPlanEntry(key, ListenerPlanAction.Add, config));
                continue;
            }

            if (string.Equals(CanonicalJson(existing), CanonicalJson(config), StringComparison.Ordinal))
            {
                plan.Add(new ListenerPlanEntry(key, ListenerPlanAction.Keep, config));
            }
            else
            {
                plan.Add(new ListenerPlanEntry(key, ListenerPlanAction.Replace, config));
            }
        }

        foreach (var (key, _) in running)
        {
            if (!desiredMap.ContainsKey(key))
            {
                plan.Add(new ListenerPlanEntry(key, ListenerPlanAction.Remove, null));
            }
        }

        return plan;
    }

    internal static bool DetectListenerChangeWithoutReload(
        IReadOnlyDictionary<ListenerKey, ListenerConfig> running,
        IReadOnlyList<ListenerConfig> desired)
    {
        var plan = Plan(running, desired);
        return plan.Any(p => p.Action is not ListenerPlanAction.Keep);
    }

    internal static async Task ApplyAsync(
        ProxyServer proxy,
        IReadOnlyList<ListenerConfig> desired,
        Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> running,
        Action<ListenerConfig> addListener,
        int drainTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var plan = Plan(running.ToDictionary(k => k.Key, k => k.Value.Config), desired);
        var added = new List<(ListenerKey Key, ProxyEndPoint Endpoint)>();
        try
        {
            // Removals and replaces tear down first so the same host:port can be rebound.
            foreach (var entry in plan.Where(p => p.Action is ListenerPlanAction.Remove or ListenerPlanAction.Replace))
            {
                if (!running.TryGetValue(entry.Key, out var existing))
                {
                    continue;
                }

                if (drainTimeoutSeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(drainTimeoutSeconds, 5)), cancellationToken)
                        .ConfigureAwait(false);
                }

                proxy.RemoveEndPoint(existing.Endpoint);
                running.Remove(entry.Key);
            }

            foreach (var entry in plan.Where(p => p.Action is ListenerPlanAction.Add or ListenerPlanAction.Replace))
            {
                if (entry.Desired is null)
                {
                    continue;
                }

                addListener(entry.Desired);
                var endpoint = proxy.ProxyEndPoints.LastOrDefault();
                if (endpoint is null)
                {
                    throw new InvalidOperationException($"failed to bind listener {entry.Key.Host}:{entry.Key.Port}");
                }

                added.Add((entry.Key, endpoint));
                running[entry.Key] = (entry.Desired, endpoint);
            }
        }
        catch
        {
            foreach (var (_, endpoint) in added)
            {
                try
                {
                    if (proxy.ProxyEndPoints.Contains(endpoint))
                    {
                        proxy.RemoveEndPoint(endpoint);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            throw;
        }
    }

    internal static string CanonicalJson(ListenerConfig config) =>
        JsonSerializer.Serialize(config, CanonicalOptions);

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
