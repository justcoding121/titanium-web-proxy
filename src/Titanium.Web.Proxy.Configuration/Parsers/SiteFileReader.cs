using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Parsers;

/// <summary>
/// Reads a simple site-file dialect (one virtual host / upstream mapping per line).
/// Lines: <c>host path => upstream:port</c>, <c>listen host:port</c>, <c>forward host:port</c>, or <c>#</c> comments.
/// </summary>
public static class SiteFileReader
{
    private const string HttpSchemePrefix = "http://";
    private const string HttpsSchemePrefix = "https://";

    public static TwpConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        var listeners = new List<ListenerConfig>();
        var order = 0;
        string? pendingForwardHost = null;
        int? pendingForwardPort = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (TryParseListen(line, out var listenHost, out var listenPort))
            {
                listeners.Add(new ListenerConfig
                {
                    Host = listenHost,
                    Port = listenPort,
                    DecryptSsl = false,
                    EnableHttp2 = false,
                    ForwardHost = pendingForwardHost,
                    ForwardPort = pendingForwardPort,
                });
                continue;
            }

            if (TryParseForward(line, out var forwardHost, out var forwardPort))
            {
                ApplyForward(listeners, forwardHost, forwardPort, ref pendingForwardHost, ref pendingForwardPort);
                continue;
            }

            ParseRouteLine(line, routes, clusters, ref order);
        }

        // listen ... then forward ... (forward after listen) already applied above.
        // forward ... then listen ... applies pending on listen creation.
        if (listeners.Count == 0 && pendingForwardHost is not null)
            throw new FormatException("site-file forward requires a listen host:port line.");

        return new TwpConfig
        {
            Listeners = listeners,
            Routes = routes,
            Clusters = clusters,
            // Match YAML RPS / CLI defaults for fair ForwardHost equivalence: site-file has no
            // logging block, so leave diagnostics off unless a future directive opts in.
            Logging = new LoggingConfig
            {
                Enabled = false,
                EnableConsole = false,
            },
        };
    }

    private static void ApplyForward(
        List<ListenerConfig> listeners,
        string forwardHost,
        int forwardPort,
        ref string? pendingForwardHost,
        ref int? pendingForwardPort)
    {
        pendingForwardHost = forwardHost;
        pendingForwardPort = forwardPort;
        if (listeners.Count == 0)
            return;

        var last = listeners[^1];
        last.ForwardHost = forwardHost;
        last.ForwardPort = forwardPort;
    }

    private static void ParseRouteLine(
        string line,
        List<RouteConfig> routes,
        List<ClusterConfig> clusters,
        ref int order)
    {
        var arrow = line.IndexOf("=>", StringComparison.Ordinal);
        if (arrow < 0)
            throw new FormatException($"Invalid site-file line (missing =>): {line}");

        var left = line[..arrow].Trim();
        var right = line[(arrow + 2)..].Trim();
        var leftParts = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftParts.Length < 1)
            throw new FormatException($"Invalid site-file left-hand side: {line}");

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

    public static TwpConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    private static bool TryParseListen(string line, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!line.StartsWith("listen ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var endpoint = line["listen ".Length..].Trim();
        return TryParseHostPort(endpoint, out host, out port);
    }

    private static bool TryParseForward(string line, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!line.StartsWith("forward ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var endpoint = line["forward ".Length..].Trim();
        if (endpoint.StartsWith(HttpSchemePrefix, StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint[HttpSchemePrefix.Length..];
        else if (endpoint.StartsWith(HttpsSchemePrefix, StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint[HttpsSchemePrefix.Length..];

        return TryParseHostPort(endpoint, out host, out port);
    }

    private static bool TryParseHostPort(string endpoint, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out port) || port <= 0)
        {
            return false;
        }

        host = endpoint[..colon].Trim();
        return host.Length > 0;
    }

    private static void ParseUpstream(string upstream, out string address, out int port, out bool https)
    {
        https = false;
        var value = upstream;
        if (value.StartsWith(HttpsSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            https = true;
            value = value[HttpsSchemePrefix.Length..];
        }
        else if (value.StartsWith(HttpSchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[HttpSchemePrefix.Length..];
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
