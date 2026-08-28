using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Parsers;

/// <summary>
/// Subset of an HTTP-server style config: <c>listen</c>, <c>server_name</c>, <c>location</c>, <c>proxy_pass</c>.
/// </summary>
public static class HttpServerConfigReader
{
    public static TwpConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var listeners = new List<ListenerConfig>();
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        string? serverName = null;
        int? listenPort = null;
        string? locationPath = null;
        var order = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim().TrimEnd(';');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("listen ", StringComparison.OrdinalIgnoreCase))
            {
                var token = line["listen ".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (int.TryParse(token.TrimEnd('s'), out var port))
                {
                    listenPort = port;
                }
            }
            else if (line.StartsWith("server_name ", StringComparison.OrdinalIgnoreCase))
            {
                serverName = line["server_name ".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            }
            else if (line.StartsWith("location ", StringComparison.OrdinalIgnoreCase))
            {
                locationPath = line["location ".Length..].Trim().Trim('{', '}', ' ');
            }
            else if (line.StartsWith("proxy_pass ", StringComparison.OrdinalIgnoreCase))
            {
                var pass = line["proxy_pass ".Length..].Trim().TrimEnd('/');
                ParseProxyPass(pass, out var address, out var port, out var https);
                var clusterId = $"http-srv-{clusters.Count + 1}";
                clusters.Add(new ClusterConfig
                {
                    Id = clusterId,
                    Destinations =
                    [
                        new DestinationConfig
                        {
                            Id = $"{clusterId}-d0",
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
                        Host = serverName,
                        Path = string.IsNullOrEmpty(locationPath) ? "/" : locationPath,
                        PathKind = PathMatchKind.Prefix,
                    },
                });
            }
        }

        if (listenPort is int p)
        {
            listeners.Add(new ListenerConfig
            {
                Port = p,
                ForwardHost = routes.Count == 1
                    ? clusters[0].Destinations[0].Address
                    : null,
                ForwardPort = routes.Count == 1
                    ? clusters[0].Destinations[0].Port
                    : null,
            });
        }

        return new TwpConfig
        {
            Listeners = listeners,
            Routes = routes,
            Clusters = clusters,
        };
    }

    public static TwpConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx < 0 ? line : line[..idx];
    }

    private static void ParseProxyPass(string pass, out string address, out int port, out bool https)
    {
        https = false;
        var value = pass;
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            https = true;
            value = value["https://".Length..];
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://".Length..];
        }

        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            value = value[..slash];
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out port))
        {
            address = value[..colon];
            return;
        }

        address = value;
        port = https ? 443 : 80;
    }
}
