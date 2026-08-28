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
        var state = new ParseState();

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim().TrimEnd(';');
            if (line.Length == 0)
            {
                continue;
            }

            ApplyDirective(line, state);
        }

        return state.ToConfig();
    }

    public static TwpConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    private static void ApplyDirective(string line, ParseState state)
    {
        if (line.StartsWith("listen ", StringComparison.OrdinalIgnoreCase))
        {
            ApplyListen(line, state);
        }
        else if (line.StartsWith("server_name ", StringComparison.OrdinalIgnoreCase))
        {
            state.ServerName = line["server_name ".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
        else if (line.StartsWith("location ", StringComparison.OrdinalIgnoreCase))
        {
            state.LocationPath = line["location ".Length..].Trim().Trim('{', '}', ' ');
        }
        else if (line.StartsWith("proxy_pass ", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProxyPass(line, state);
        }
    }

    private static void ApplyListen(string line, ParseState state)
    {
        var token = line["listen ".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (int.TryParse(token.TrimEnd('s'), out var port))
        {
            state.ListenPort = port;
        }
    }

    private static void ApplyProxyPass(string line, ParseState state)
    {
        var pass = line["proxy_pass ".Length..].Trim().TrimEnd('/');
        ParseProxyPass(pass, out var address, out var port, out var https);
        var clusterId = $"http-srv-{state.Clusters.Count + 1}";
        state.Clusters.Add(new ClusterConfig
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

        state.Routes.Add(new RouteConfig
        {
            Id = $"route-{state.Routes.Count + 1}",
            ClusterId = clusterId,
            Order = state.Order++,
            Match = new RouteMatch
            {
                Host = state.ServerName,
                Path = string.IsNullOrEmpty(state.LocationPath) ? "/" : state.LocationPath,
                PathKind = PathMatchKind.Prefix,
            },
        });
    }

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

    private sealed class ParseState
    {
        public string? ServerName { get; set; }
        public int? ListenPort { get; set; }
        public string? LocationPath { get; set; }
        public int Order { get; set; }
        public List<ListenerConfig> Listeners { get; } = [];
        public List<RouteConfig> Routes { get; } = [];
        public List<ClusterConfig> Clusters { get; } = [];

        public TwpConfig ToConfig()
        {
            if (ListenPort is int p)
            {
                Listeners.Add(new ListenerConfig
                {
                    Port = p,
                    ForwardHost = Routes.Count == 1
                        ? Clusters[0].Destinations[0].Address
                        : null,
                    ForwardPort = Routes.Count == 1
                        ? Clusters[0].Destinations[0].Port
                        : null,
                });
            }

            return new TwpConfig
            {
                Listeners = Listeners,
                Routes = Routes,
                Clusters = Clusters,
            };
        }
    }
}
