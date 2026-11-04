using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Compiled form of a listener's SNI routes. Exact names win, then the longest matching
///     <c>*.suffix</c>. Built once when the routes are assigned, so a connection pays a dictionary
///     lookup and a short scan.
/// </summary>
internal sealed class PassthroughRouteTable
{
    private readonly Dictionary<string, PassthroughSniRoute> exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly (string Suffix, PassthroughSniRoute Route)[] wildcards;

    public PassthroughRouteTable(IReadOnlyList<PassthroughSniRoute> routes)
    {
        var wild = new List<(string, PassthroughSniRoute)>();
        foreach (var route in routes)
        {
            var name = route.ServerName.Trim();
            if (name.StartsWith("*.", StringComparison.Ordinal))
            {
                wild.Add((name[1..], route));
            }
            else
            {
                // The first entry for a name wins, matching file order elsewhere in the config.
                exact.TryAdd(name, route);
            }
        }

        wild.Sort(static (a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        wildcards = wild.ToArray();
    }

    public bool IsEmpty => exact.Count == 0 && wildcards.Length == 0;

    /// <summary>Finds the route for a server name, or null when none matches.</summary>
    public PassthroughSniRoute? Match(string serverName)
    {
        if (exact.TryGetValue(serverName, out var route))
        {
            return route;
        }

        for (var i = 0; i < wildcards.Length; i++)
        {
            var (suffix, candidate) = wildcards[i];
            if (serverName.Length > suffix.Length &&
                serverName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
