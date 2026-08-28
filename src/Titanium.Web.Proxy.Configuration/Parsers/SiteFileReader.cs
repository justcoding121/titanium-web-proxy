using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Parsers;

/// <summary>
/// Reads a simple site-file dialect (one virtual host / upstream mapping per line).
/// Lines: <c>host path => upstream:port</c> or <c>#</c> comments.
/// </summary>
public static class SiteFileReader
{
    public static TwpConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        var order = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
            {
                throw new FormatException($"Invalid site-file line (missing =>): {line}");
            }

            var left = line[..arrow].Trim();
            var right = line[(arrow + 2)..].Trim();
            var leftParts = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (leftParts.Length < 1)
            {
                throw new FormatException($"Invalid site-file left-hand side: {line}");
            }

            var host = leftParts[0];
            var path = leftParts.Length > 1 ? leftParts[1] : "/";
            ParseUpstream(right, out var address, out var port, out var https);

            var clusterId = $"site-{clusters.Count + 1}";
            var destId = $"{clusterId}-d0";
            clusters.Add(new ClusterConfig
            {
                Id = clusterId,
                Destinations =
                [
                    new DestinationConfig
                    {
                        Id = destId,
                        Address = address,
                        Port = port,
                        UseHttps = https,
                    },
                ],
            });

            routes.Add(new RouteConfig
            {
                Id = $"route-{routes.Count + 1}",
                ClusterId = clusterId,
                Order = order++,
                Match = new RouteMatch
                {
                    Host = host,
                    Path = path,
                    PathKind = PathMatchKind.Prefix,
                },
            });
        }

        return new TwpConfig
        {
            Routes = routes,
            Clusters = clusters,
        };
    }

    public static TwpConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    private static void ParseUpstream(string upstream, out string address, out int port, out bool https)
    {
        https = false;
        var value = upstream;
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            https = true;
            value = value["https://".Length..];
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://".Length..];
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out port))
        {
            address = value[..colon];
            return;
        }

        address = value.TrimEnd('/');
        port = https ? 443 : 80;
    }
}
