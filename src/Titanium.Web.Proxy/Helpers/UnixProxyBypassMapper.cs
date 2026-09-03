using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Maps WinINET-style semicolon bypass lists to macOS/Linux formats.
/// </summary>
public static class UnixProxyBypassMapper
{
    public static IReadOnlyList<string> ToUnixBypassHosts(string? winInetProxyOverride)
    {
        if (string.IsNullOrWhiteSpace(winInetProxyOverride))
            return Array.Empty<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in winInetProxyOverride.Split(';'))
        {
            var rule = raw.Trim();
            if (rule.Length == 0) continue;

            if (rule.Equals("<-loopback>", StringComparison.OrdinalIgnoreCase))
                continue;

            if (rule.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                Add(result, seen, "*.local");
                Add(result, seen, "169.254.0.0/16");
                continue;
            }

            Add(result, seen, rule);
        }

        return result;
    }

    public static string ToCommaSeparated(string? winInetProxyOverride) =>
        string.Join(",", ToUnixBypassHosts(winInetProxyOverride));

    public static string ToGsettingsArray(string? winInetProxyOverride)
    {
        var hosts = ToUnixBypassHosts(winInetProxyOverride);
        if (hosts.Count == 0) return "@as []";

        var sb = new StringBuilder("[");
        for (var i = 0; i < hosts.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('\'').Append(hosts[i].Replace("'", @"'\''")).Append('\'');
        }

        sb.Append(']');
        return sb.ToString();
    }

    public static string ToNoProxyEnv(string? winInetProxyOverride) =>
        ToNoProxyEnv(winInetProxyOverride, HasLoopbackSubtractRule(winInetProxyOverride));

    /// <summary>
    ///     Builds a <c>NO_PROXY</c> value. When <paramref name="proxyLoopback"/> is true, localhost is omitted
    ///     so loopback traffic can use the proxy (parity with WinINET <c>&lt;-loopback&gt;</c>).
    /// </summary>
    public static string ToNoProxyEnv(string? winInetProxyOverride, bool proxyLoopback)
    {
        var hosts = ToUnixBypassHosts(winInetProxyOverride).ToList();
        if (!proxyLoopback)
        {
            if (!hosts.Any(h => h.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                hosts.Insert(0, "localhost");
            }

            if (!hosts.Any(h => h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
            {
                hosts.Insert(0, "127.0.0.1");
            }
        }

        return string.Join(",", hosts);
    }

    private static bool HasLoopbackSubtractRule(string? winInetProxyOverride)
    {
        if (string.IsNullOrWhiteSpace(winInetProxyOverride))
        {
            return false;
        }

        return winInetProxyOverride.Split(';')
            .Any(r => r.Trim().Equals("<-loopback>", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return NetworkHelper.IsLocalIpAddress(host);
        }
        catch
        {
            return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        }
    }

    private static void Add(List<string> result, HashSet<string> seen, string value)
    {
        if (seen.Add(value)) result.Add(value);
    }
}
