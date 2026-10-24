using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Parsers;

/// <summary>
/// Subset of an HTTP-server style config: listen, server_name, location, proxy_pass, and a minimal upstream block.
/// </summary>
public static class HttpServerConfigReader
{
    public static TwpConfig Parse(string text) => Parse(text, null);

    public static TwpConfig Parse(string text, ICollection<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(text);
        var state = new ParseState();
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = StripComment(raw).Trim().TrimEnd(';').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var opens = Count(line, '{');
            var closes = Count(line, '}');
            var body = line.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Trim();
            if (body.Length > 0)
            {
                ApplyDirective(body, state, warnings, lineNo);
            }

            state.Depth += opens - closes;
            if (closes > 0 && state.Depth <= state.ServerDepth)
            {
                state.FinishServer();
            }
        }

        return state.ToConfig();
    }

    public static TwpConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    private static void ApplyDirective(string line, ParseState state, ICollection<string>? warnings, int lineNo)
    {
        if (line.StartsWith("server_name ", StringComparison.OrdinalIgnoreCase) || line.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            if (line.Equals("server", StringComparison.OrdinalIgnoreCase))
            {
                state.BeginServer();
                return;
            }
        }

        if (line.StartsWith("server ", StringComparison.OrdinalIgnoreCase) || line.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            state.BeginServer();
            return;
        }

        if (line.StartsWith("listen ", StringComparison.OrdinalIgnoreCase))
        {
            ApplyListen(line, state);
            return;
        }

        if (line.StartsWith("server_name ", StringComparison.OrdinalIgnoreCase))
        {
            state.ServerName = line["server_name ".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return;
        }

        if (line.StartsWith("location ", StringComparison.OrdinalIgnoreCase))
        {
            ApplyLocation(line, state);
            return;
        }

        if (line.StartsWith("proxy_pass ", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProxyPass(line, state);
            return;
        }

        if (line.StartsWith("upstream ", StringComparison.OrdinalIgnoreCase))
        {
            state.BeginUpstream(line["upstream ".Length..].Trim());
            return;
        }

        if (state.InUpstream && line.StartsWith("server ", StringComparison.OrdinalIgnoreCase))
        {
            state.AddUpstreamServer(line["server ".Length..].Trim());
            return;
        }

        if (line is "least_conn" or "ip_hash")
        {
            warnings?.Add($"Directive '{line}' is accepted but not enforced yet (line {lineNo}).");
            return;
        }

        warnings?.Add($"Ignored directive '{line}' (line {lineNo}).");
    }

    private static void ApplyLocation(string line, ParseState state)
    {
        var rest = line["location ".Length..].Trim();
        var kind = PathMatchKind.Prefix;
        if (rest.StartsWith("= ", StringComparison.Ordinal) || rest.StartsWith('='))
        {
            kind = PathMatchKind.Exact;
            rest = rest.TrimStart('=').Trim();
        }
        else if (rest.StartsWith("^~ ", StringComparison.Ordinal))
        {
            rest = rest["^~ ".Length..].Trim();
        }
        else if (rest.StartsWith("~*", StringComparison.Ordinal))
        {
            kind = PathMatchKind.Regex;
            state.LocationCaseSensitive = false;
            rest = rest["~*".Length..].Trim();
        }
        else if (rest.StartsWith('~'))
        {
            kind = PathMatchKind.Regex;
            state.LocationCaseSensitive = true;
            rest = rest[1..].Trim();
        }

        state.LocationPath = rest;
        state.LocationKind = kind;
    }

    private static void ApplyListen(string line, ParseState state)
    {
        var token = line["listen ".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!TryParseListen(token, out var host, out var port))
        {
            throw new FormatException($"Cannot parse listen directive: {line}");
        }

        state.Listens.Add((host, port));
    }

    private static bool TryParseListen(string token, out string host, out int port)
    {
        host = "";
        port = 0;
        var flags = token;
        if (flags.StartsWith('['))
        {
            var end = flags.IndexOf(']');
            if (end < 0 || end + 1 >= flags.Length || flags[end + 1] != ':')
            {
                return false;
            }

            host = flags[..(end + 1)];
            return int.TryParse(flags[(end + 2)..], out port);
        }

        var colon = flags.LastIndexOf(':');
        if (colon > 0 && int.TryParse(flags[(colon + 1)..], out port))
        {
            host = flags[..colon];
            if (host == "*")
            {
                host = "";
            }

            return port > 0;
        }

        host = "";
        return int.TryParse(flags, out port) && port > 0;
    }

    private static void ApplyProxyPass(string line, ParseState state)
    {
        var pass = line["proxy_pass ".Length..].Trim().TrimEnd('/');
        if (state.Upstreams.TryGetValue(pass, out var upstream) ||
            state.Upstreams.TryGetValue(StripScheme(pass), out upstream))
        {
            var clusterId = upstream.Name;
            if (state.Clusters.All(c => c.Id != clusterId))
            {
                state.Clusters.Add(upstream.ToCluster());
            }

            state.AddRoute(clusterId);
            return;
        }

        ParseProxyPass(pass, out var address, out var port, out var https);
        var id = $"http-srv-{state.Clusters.Count + 1}";
        state.Clusters.Add(new ClusterConfig
        {
            Id = id,
            Destinations =
            [
                new DestinationConfig
                {
                    Id = $"{id}-d0",
                    Address = address,
                    Port = port,
                    UseHttps = https,
                },
            ],
        });
        state.AddRoute(id);
    }

    private static string StripScheme(string value)
    {
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return value["https://".Length..];
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return value["http://".Length..];
        }

        return value;
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx < 0 ? line : line[..idx];
    }

    private static int Count(string line, char c)
    {
        var n = 0;
        foreach (var ch in line)
        {
            if (ch == c)
            {
                n++;
            }
        }

        return n;
    }

    private static void ParseProxyPass(string pass, out string address, out int port, out bool https)
    {
        https = false;
        var value = StripScheme(pass);
        if (pass.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            https = true;
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

    private sealed class UpstreamDef(string name)
    {
        public string Name { get; } = name;
        public List<DestinationConfig> Destinations { get; } = [];

        public ClusterConfig ToCluster() => new()
        {
            Id = Name,
            Destinations = Destinations,
        };
    }

    private sealed class ParseState
    {
        public string? ServerName { get; set; }
        public string? LocationPath { get; set; }
        public PathMatchKind LocationKind { get; set; } = PathMatchKind.Prefix;
        public bool LocationCaseSensitive { get; set; } = true;
        public int Order { get; set; }
        public int Depth { get; set; }
        public int ServerDepth { get; set; } = int.MaxValue;
        public bool InUpstream { get; set; }
        public string? UpstreamName { get; set; }
        public List<(string Host, int Port)> Listens { get; } = [];
        public List<RouteConfig> Routes { get; } = [];
        public List<ClusterConfig> Clusters { get; } = [];
        public Dictionary<string, UpstreamDef> Upstreams { get; } = new(StringComparer.Ordinal);

        public void BeginServer()
        {
            ServerName = null;
            LocationPath = null;
            ServerDepth = Depth;
        }

        public void FinishServer()
        {
            ServerName = null;
            LocationPath = null;
            ServerDepth = int.MaxValue;
            InUpstream = false;
        }

        public void BeginUpstream(string name)
        {
            InUpstream = true;
            UpstreamName = name;
            Upstreams[name] = new UpstreamDef(name);
        }

        public void AddUpstreamServer(string spec)
        {
            if (UpstreamName is null || !Upstreams.TryGetValue(UpstreamName, out var def))
            {
                return;
            }

            var token = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            ParseProxyPass(token.Contains("://", StringComparison.Ordinal) ? token : "http://" + token, out var address, out var port, out var https);
            def.Destinations.Add(new DestinationConfig
            {
                Id = $"{UpstreamName}-d{def.Destinations.Count}",
                Address = address,
                Port = port,
                UseHttps = https,
            });
        }

        public void AddRoute(string clusterId)
        {
            Routes.Add(new RouteConfig
            {
                Id = $"route-{Routes.Count + 1}",
                ClusterId = clusterId,
                Order = Order++,
                Match = new RouteMatch
                {
                    Host = ServerName,
                    Path = string.IsNullOrEmpty(LocationPath) ? "/" : LocationPath,
                    PathKind = LocationKind,
                    PathCaseSensitive = LocationCaseSensitive,
                },
            });
        }

        public TwpConfig ToConfig()
        {
            var listeners = new List<ListenerConfig>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (host, port) in Listens)
            {
                var key = host + ":" + port;
                if (!seen.Add(key))
                {
                    continue;
                }

                listeners.Add(new ListenerConfig
                {
                    Host = string.IsNullOrEmpty(host) ? null : host,
                    Port = port,
                    ForwardHost = Routes.Count == 1 ? Clusters[0].Destinations[0].Address : null,
                    ForwardPort = Routes.Count == 1 ? Clusters[0].Destinations[0].Port : null,
                });
            }

            return new TwpConfig
            {
                Listeners = listeners,
                Routes = Routes,
                Clusters = Clusters,
            };
        }
    }
}
