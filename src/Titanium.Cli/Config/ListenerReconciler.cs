using System.Text.Json;
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

    /// <summary>
    ///     Applies the plan make-before-break: new listeners bind first, changed listeners are replaced (old endpoint
    ///     down, new one up), and listeners that are gone are removed last. If any bind fails, everything done in this
    ///     pass is undone: new endpoints are removed and replaced listeners are restored from their previous config,
    ///     so a failed reload never leaves the proxy with fewer listeners than it started with.
    /// </summary>
    internal static void Apply(
        IReadOnlyList<ListenerConfig> desired,
        Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> running,
        Func<ListenerConfig, ProxyEndPoint> addListener,
        Action<ProxyEndPoint> removeEndpoint)
    {
        var plan = Plan(running.ToDictionary(k => k.Key, k => k.Value.Config), desired);
        var added = new List<(ListenerKey Key, ProxyEndPoint Endpoint)>();
        var replaced = new List<(ListenerKey Key, ListenerConfig Config)>();
        try
        {
            foreach (var entry in plan.Where(static p => p.Action is ListenerPlanAction.Add && p.Desired is not null))
            {
                Bind(entry, running, added, addListener);
            }

            foreach (var entry in plan.Where(static p => p.Action is ListenerPlanAction.Replace && p.Desired is not null))
            {
                // Same host:port, so the old endpoint must be down before the new one can bind.
                if (running.TryGetValue(entry.Key, out var existing))
                {
                    removeEndpoint(existing.Endpoint);
                    running.Remove(entry.Key);
                    replaced.Add((entry.Key, existing.Config));
                }

                Bind(entry, running, added, addListener);
            }

            foreach (var entry in plan.Where(static p => p.Action is ListenerPlanAction.Remove))
            {
                if (running.TryGetValue(entry.Key, out var existing))
                {
                    removeEndpoint(existing.Endpoint);
                    running.Remove(entry.Key);
                }
            }
        }
        catch
        {
            foreach (var (key, endpoint) in added)
            {
                TryRun(() => removeEndpoint(endpoint));
                running.Remove(key);
            }

            foreach (var (key, config) in replaced)
            {
                TryRun(() => running[key] = (config, addListener(config)));
            }

            throw;
        }
    }

    private static void Bind(
        ListenerPlanEntry entry,
        Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> running,
        List<(ListenerKey Key, ProxyEndPoint Endpoint)> added,
        Func<ListenerConfig, ProxyEndPoint> addListener)
    {
        var endpoint = addListener(entry.Desired!);
        added.Add((entry.Key, endpoint));
        running[entry.Key] = (entry.Desired!, endpoint);
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            // Best effort while unwinding: the original failure is the one that propagates.
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
