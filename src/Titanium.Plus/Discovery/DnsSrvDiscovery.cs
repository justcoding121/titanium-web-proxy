using System.Globalization;
using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Plus.Discovery;

/// <summary>DNS SRV discovery helpers. DnsClient is referenced only from methods invoked in dnsSrv mode.</summary>
internal static class DnsSrvDiscovery
{
    internal const int DefaultIntervalMs = 15_000;
    internal const int MinIntervalMs = 1_000;
    internal const int DefaultMaxTargets = 64;
    internal const int MaxMaxTargets = 256;

    internal sealed record SrvRow(string Target, int Port, int Priority, int Weight);

    internal static int ReadIntervalMs(IReadOnlyDictionary<string, string> options) =>
        int.TryParse(options.GetValueOrDefault("discovery.intervalMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? Math.Clamp(ms, MinIntervalMs, 3_600_000)
            : DefaultIntervalMs;

    internal static int ReadMaxTargets(IReadOnlyDictionary<string, string> options) =>
        int.TryParse(options.GetValueOrDefault("discovery.srvMaxTargets"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            ? Math.Clamp(max, 1, MaxMaxTargets)
            : DefaultMaxTargets;

    internal static IReadOnlyList<SrvRow> OrderRows(IEnumerable<SrvRow> rows) =>
        rows.OrderBy(r => r.Priority).ThenBy(r => r.Target, StringComparer.Ordinal).ToList();

    internal static List<DestinationConfig> BuildDestinations(
        string clusterId,
        IReadOnlyList<SrvRow> rows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> addressesByTarget,
        int maxTargets,
        out bool truncated)
    {
        truncated = false;
        var destinations = new List<DestinationConfig>();
        foreach (var row in rows)
        {
            if (!addressesByTarget.TryGetValue(row.Target, out var addresses) || addresses.Count == 0)
            {
                continue;
            }

            foreach (var address in addresses)
            {
                if (destinations.Count >= maxTargets)
                {
                    truncated = true;
                    return destinations;
                }

                destinations.Add(new DestinationConfig
                {
                    Id = $"{clusterId}-{row.Target}-{row.Port}-{address}",
                    Address = address,
                    Port = row.Port,
                    Weight = Math.Max(1, row.Weight),
                    Priority = row.Priority,
                });
            }
        }

        return destinations;
    }

    internal static async Task<(List<DestinationConfig> Destinations, int TtlSeconds, bool Truncated)> QueryAsync(
        string srvName,
        string clusterId,
        int maxTargets,
        string? dnsServer,
        CancellationToken cancellationToken)
    {
        var client = CreateLookupClient(dnsServer);
        var result = await client.QueryAsync(srvName, QueryType.SRV, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<SrvRow>();
        var ttl = int.MaxValue;
        foreach (var record in result.Answers.OfType<SrvRecord>())
        {
            var target = record.Target.Value.TrimEnd('.');
            rows.Add(new SrvRow(target, record.Port, record.Priority, record.Weight));
            ttl = Math.Min(ttl, record.InitialTimeToLive);
            if (rows.Count >= maxTargets * 4)
            {
                break;
            }
        }

        var ordered = OrderRows(rows);
        var addressesByTarget = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in ordered.Select(r => r.Target).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var host = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
            if (host.Length == 0)
            {
                continue;
            }

            addressesByTarget[target] = host.Select(static ip => ip.ToString()).ToArray();
        }

        var destinations = BuildDestinations(clusterId, ordered, addressesByTarget, maxTargets, out var truncated);
        return (destinations, ttl == int.MaxValue ? 0 : Math.Max(1, ttl), truncated);
    }

    private static LookupClient CreateLookupClient(string? dnsServer)
    {
        if (string.IsNullOrWhiteSpace(dnsServer))
        {
            return new LookupClient();
        }

        var endpoint = ParseNameServer(dnsServer.Trim());
        return new LookupClient(new LookupClientOptions(new NameServer(endpoint.Address, endpoint.Port)));
    }

    internal static IPEndPoint ParseNameServer(string value)
    {
        var text = value;
        string host;
        var port = 53;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0 || close + 2 >= text.Length || text[close + 1] != ':')
            {
                throw new FormatException("discovery.dnsServer must be host[:port].");
            }

            host = text[1..close];
            port = int.Parse(text[(close + 2)..], CultureInfo.InvariantCulture);
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon > 0 && colon == text.IndexOf(':', StringComparison.Ordinal))
            {
                host = text[..colon];
                port = int.Parse(text[(colon + 1)..], CultureInfo.InvariantCulture);
            }
            else
            {
                host = text;
            }
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            var resolved = Dns.GetHostAddresses(host);
            ip = resolved.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                 ?? resolved.FirstOrDefault()
                 ?? throw new InvalidOperationException($"discovery.dnsServer host '{host}' did not resolve.");
        }

        return new IPEndPoint(ip, port);
    }
}
