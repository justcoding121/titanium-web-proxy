using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Network;

namespace Titanium.Gateway.Translation;

/// <summary>One documented annotation. The table in <c>website/docs/ingress.md</c> is generated from these.</summary>
/// <param name="Name">The key after the <c>titanium.io/</c> prefix.</param>
/// <param name="Values">The accepted values, as the docs show them.</param>
/// <param name="Feature">The data-plane feature the annotation maps to.</param>
/// <param name="Notes">Scope and limits.</param>
public sealed record AnnotationSpec(string Name, string Values, string Feature, string Notes)
{
    public string Key => IngressAnnotations.Prefix + Name;
}

/// <summary>How the backend is spoken to.</summary>
internal enum BackendProtocol
{
    Http,
    Https,
    H2c,
    Grpc,
}

/// <summary>
///     The <c>titanium.io/</c> annotations an Ingress may carry. Each maps to a feature the data plane already has;
///     nothing here needs new data-plane code. An annotation under the prefix that is not listed is reported with a
///     Warning event (never ignored silently); a listed one with a value that cannot be honored makes every rule of
///     the Ingress answer <c>500</c>, with an event that says why.
/// </summary>
public static partial class IngressAnnotations
{
    public const string Prefix = "titanium.io/";

    private static readonly string[] CanaryDependents = ["canary-weight", "canary-by-header"];

    /// <summary>Every annotation, in the order the docs list them.</summary>
    public static IReadOnlyList<AnnotationSpec> All { get; } =
    [
        new("ssl-redirect", "`false` (default)",
            "none",
            "`false` is accepted. `true` is rejected: the data plane matches routes by host, path, header, query, and method, not by scheme, so a redirect route would also redirect HTTPS requests in a loop."),
        new("rewrite-target", "a path starting with `/`",
            "`PathRegexReplace` transform",
            "Replaces the matched prefix (`Prefix`, `ImplementationSpecific`) or the whole path (`Exact`). Capture groups (`$1`) are not supported and are rejected. The query string is kept."),
        new("proxy-read-timeout", "seconds, 1-3600",
            "cluster `httpClient.requestTimeout`",
            "How long the data plane waits for the backend's response headers. Gives the backend its own cluster."),
        new("proxy-body-size", "`8m`, `512k`, `1g`, or bytes; `0` for no limit",
            "`maxRequestBodySize` policy",
            "Checked against `Content-Length`; a request with no length (chunked) is not checked. Answers `413`."),
        new("backend-protocol", "`HTTP` (default), `HTTPS`, `H2C`, `GRPC`",
            "destination `useHttps`",
            "`HTTPS` connects to the backend with TLS and validates its certificate. `H2C` and `GRPC` connect in cleartext; the data plane speaks HTTP/2 to the backend when the client does (ALPN or prior knowledge), so gRPC clients get end-to-end HTTP/2. Gives the backend its own cluster."),
        new("affinity", "`cookie`",
            "cluster `sessionAffinity` (cookie)",
            "Pins a client to one backend pod with an opaque cookie named `titanium-affinity`. Gives the backend its own cluster."),
        new("load-balance", "`round_robin` (default), `random`, `least_conn`, `ewma`, `p2c`",
            "cluster `algorithm`",
            "`least_conn` is `LeastRequests`, `ewma` is `LeastTime`, `p2c` is `PowerOfTwoChoices`. Gives the backend its own cluster."),
        new("whitelist-source-range", "comma-separated addresses or CIDR prefixes",
            "`ipAllowDeny` policy (`allow`)",
            "Any other client address gets `403`. The address is the socket peer, so behind a proxy list the proxy's range."),
        new("limit-rps", "requests per second per client, 1-1000000",
            "`rateLimit` policy (fixed window of 1 second)",
            "Counted per client address and per Ingress. Excess requests get `429` with `Retry-After`."),
        new("cors-allow-origin", "`*` or comma-separated origins such as `https://app.example.com`",
            "`cors` policy",
            "Answers the `OPTIONS` preflight itself with `204` and the `Access-Control-Allow-*` headers. It does not add headers to the actual response."),
        new("auth-url", "an absolute `http` or `https` URL without credentials",
            "`extAuth` policy",
            "Each request is checked with a `GET` subrequest first; a `2xx` answer admits it, any other answer is relayed to the client. Fails closed: a timeout or an error answers `503`, never admits."),
        new("canary", "`true` or `false` (default)",
            "weighted route clusters",
            "Marks the Ingress as a canary of the Ingress that serves the same host, path, and path type. The canary Ingress contributes only its backend; every other annotation comes from the primary."),
        new("canary-weight", "percent, 0-100 (default 0)",
            "route `clusters` weights",
            "Share of requests the canary backend receives. Needs `canary: \"true\"`."),
        new("canary-by-header", "a header name",
            "route header match",
            "`always` in that header sends the request to the canary, `never` to the primary; any other value follows the weight. Needs `canary: \"true\"`."),
        new("upstream-tls-secret", "`name` (same namespace) or `namespace/name` of the Ingress's own namespace",
            "cluster `httpClient` TLS files",
            "A `kubernetes.io/tls` Secret. `ca.crt` becomes the trust anchors for the backend, and `tls.crt` and `tls.key` the client certificate. Needs `backend-protocol: HTTPS`. Gives the backend its own cluster."),
    ];

    private static readonly Dictionary<string, AnnotationSpec> ByName =
        All.ToDictionary(a => a.Name, StringComparer.Ordinal);

    public static bool IsKnown(string name) => ByName.ContainsKey(name);

    /// <summary>The Markdown table that <c>website/docs/ingress.md</c> must contain between its annotation markers.</summary>
    public static string RenderMarkdownTable()
    {
        var text = new StringBuilder();
        text.Append("| Annotation | Value | Data-plane feature | Notes |\n");
        text.Append("|------------|-------|--------------------|-------|\n");
        foreach (var annotation in All)
        {
            text.Append("| `").Append(annotation.Key).Append("` | ")
                .Append(Escape(annotation.Values)).Append(" | ")
                .Append(Escape(annotation.Feature)).Append(" | ")
                .Append(Escape(annotation.Notes)).Append(" |\n");
        }

        return text.ToString();
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    [GeneratedRegex(@"^([0-9]+)([kKmMgG]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex BodySize();

    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderToken();

    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DnsName();

    /// <summary>
    ///     Reads the annotations of one Ingress. Values that cannot be honored land in <see cref="IngressSettings.Problems"/>;
    ///     keys under the prefix that name nothing land in <see cref="IngressSettings.Unknown"/>.
    /// </summary>
    internal static IngressSettings Parse(IReadOnlyDictionary<string, string>? annotations, string ingressNamespace)
    {
        var settings = new IngressSettings();
        if (annotations is null)
        {
            return settings;
        }

        foreach (var (key, value) in annotations.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = key[Prefix.Length..];
            if (!IsKnown(name))
            {
                settings.Unknown.Add(key);
                continue;
            }

            ParseOne(settings, name, value.Trim(), ingressNamespace);
        }

        if (!settings.Canary)
        {
            foreach (var dependent in CanaryDependents.Where(d => annotations.ContainsKey(Prefix + d)))
            {
                settings.Problems.Add($"{Prefix}{dependent} needs {Prefix}canary: \"true\".");
            }
        }

        if (settings.UpstreamTlsSecret is not null && settings.Protocol != BackendProtocol.Https)
        {
            settings.Problems.Add($"{Prefix}upstream-tls-secret needs {Prefix}backend-protocol: \"HTTPS\".");
        }

        return settings;
    }

    private static void ParseOne(IngressSettings settings, string name, string value, string ingressNamespace) // NOSONAR S3776 -- Annotation kinds share one problem reporter; splitting the switch would scatter validation messages.
    {
        var key = Prefix + name;
        void Bad(string expected) => settings.Problems.Add($"{key} is \"{value}\"; expected {expected}.");

        switch (name)
        {
            case "ssl-redirect":
                if (!TryBool(value, out var redirect))
                {
                    Bad("\"true\" or \"false\"");
                }
                else if (redirect)
                {
                    settings.Problems.Add($"{key}: \"true\" cannot be honored. The data plane matches routes by host and path, not by scheme, so a redirect would loop on HTTPS. Set it to \"false\" or remove it.");
                }

                break;
            case "rewrite-target":
                if (!value.StartsWith('/') || value.Contains('$', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal))
                {
                    Bad("a path that starts with \"/\" and has no capture groups ($1) or query");
                }
                else
                {
                    settings.RewriteTarget = value;
                }

                break;
            case "proxy-read-timeout":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 3600)
                {
                    settings.ReadTimeoutSeconds = seconds;
                }
                else
                {
                    Bad("a number of seconds from 1 to 3600");
                }

                break;
            case "proxy-body-size":
                if (TryBodySize(value, out var bytes))
                {
                    settings.BodyLimitBytes = bytes;
                }
                else
                {
                    Bad("bytes or a number with a k, m, or g suffix (for example 8m)");
                }

                break;
            case "backend-protocol":
                switch (value.ToUpperInvariant())
                {
                    case "HTTP":
                        settings.Protocol = BackendProtocol.Http;
                        break;
                    case "HTTPS":
                        settings.Protocol = BackendProtocol.Https;
                        break;
                    case "H2C":
                        settings.Protocol = BackendProtocol.H2c;
                        break;
                    case "GRPC":
                        settings.Protocol = BackendProtocol.Grpc;
                        break;
                    default:
                        Bad("HTTP, HTTPS, H2C, or GRPC");
                        break;
                }

                break;
            case "affinity":
                if (string.Equals(value, "cookie", StringComparison.OrdinalIgnoreCase))
                {
                    settings.AffinityCookie = true;
                }
                else
                {
                    Bad("\"cookie\"");
                }

                break;
            case "load-balance":
                switch (value.ToLowerInvariant())
                {
                    case "round_robin":
                        settings.Algorithm = LoadBalanceAlgorithm.RoundRobin;
                        break;
                    case "random":
                        settings.Algorithm = LoadBalanceAlgorithm.Random;
                        break;
                    case "least_conn":
                        settings.Algorithm = LoadBalanceAlgorithm.LeastRequests;
                        break;
                    case "ewma":
                        settings.Algorithm = LoadBalanceAlgorithm.LeastTime;
                        break;
                    case "p2c":
                        settings.Algorithm = LoadBalanceAlgorithm.PowerOfTwoChoices;
                        break;
                    default:
                        Bad("round_robin, random, least_conn, ewma, or p2c");
                        break;
                }

                break;
            case "whitelist-source-range":
                var ranges = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ranges.Length > 0 && ranges.All(r => CidrPrefix.TryParse(r, out _)))
                {
                    settings.WhitelistRanges = string.Join(",", ranges);
                }
                else
                {
                    Bad("a comma-separated list of addresses or CIDR prefixes");
                }

                break;
            case "limit-rps":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rps) && rps is >= 1 and <= 1_000_000)
                {
                    settings.LimitRps = rps;
                }
                else
                {
                    Bad("a number of requests per second from 1 to 1000000");
                }

                break;
            case "cors-allow-origin":
                var origins = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (origins is ["*"] || (origins.Length > 0 && origins.All(IsOrigin)))
                {
                    settings.CorsOrigins = string.Join(",", origins);
                }
                else
                {
                    Bad("\"*\" or comma-separated origins such as https://app.example.com");
                }

                break;
            case "auth-url":
                if (Uri.TryCreate(value, UriKind.Absolute, out var url) &&
                    url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo))
                {
                    settings.AuthUrl = value;
                }
                else
                {
                    Bad("an absolute http or https URL without credentials");
                }

                break;
            case "canary":
                if (TryBool(value, out var canary))
                {
                    settings.Canary = canary;
                }
                else
                {
                    Bad("\"true\" or \"false\"");
                }

                break;
            case "canary-weight":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) && weight is >= 0 and <= 100)
                {
                    settings.CanaryWeight = weight;
                }
                else
                {
                    Bad("a percent from 0 to 100");
                }

                break;
            case "canary-by-header":
                if (HeaderToken().IsMatch(value))
                {
                    settings.CanaryHeader = value;
                }
                else
                {
                    Bad("a header name");
                }

                break;
            case "upstream-tls-secret":
                var parts = value.Split('/');
                var secretName = parts[^1];
                if (parts.Length is 1 or 2 &&
                    (parts.Length == 1 || string.Equals(parts[0], ingressNamespace, StringComparison.Ordinal)) &&
                    DnsName().IsMatch(secretName))
                {
                    settings.UpstreamTlsSecret = secretName;
                }
                else
                {
                    Bad("the name of a Secret in the Ingress's namespace (name or namespace/name)");
                }

                break;
        }
    }

    private static bool TryBool(string value, out bool result)
    {
        result = false;
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBodySize(string value, out long bytes)
    {
        bytes = 0;
        var match = BodySize().Match(value);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var factor = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" => 1024L,
            "m" => 1024L * 1024,
            "g" => 1024L * 1024 * 1024,
            _ => 1L,
        };
        if (number > long.MaxValue / factor)
        {
            return false;
        }

        bytes = number * factor;
        return true;
    }

    private static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.AbsolutePath is "" or "/") && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        !value.EndsWith('/');
}

/// <summary>The annotations of one Ingress, parsed.</summary>
internal sealed class IngressSettings
{
    public bool Canary { get; set; }

    public int CanaryWeight { get; set; }

    public string? CanaryHeader { get; set; }

    public string? RewriteTarget { get; set; }

    public int? ReadTimeoutSeconds { get; set; }

    /// <summary>Null for no annotation; 0 for "no limit".</summary>
    public long? BodyLimitBytes { get; set; }

    public BackendProtocol Protocol { get; set; } = BackendProtocol.Http;

    public bool AffinityCookie { get; set; }

    public LoadBalanceAlgorithm? Algorithm { get; set; }

    public string? WhitelistRanges { get; set; }

    public int? LimitRps { get; set; }

    public string? CorsOrigins { get; set; }

    public string? AuthUrl { get; set; }

    public string? UpstreamTlsSecret { get; set; }

    /// <summary>Annotations under the prefix that are not in the table.</summary>
    public List<string> Unknown { get; } = [];

    /// <summary>Annotations the controller knows but cannot honor. Any entry makes every rule of the Ingress answer 500.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>True when the backend needs a cluster of its own, because a cluster-level setting differs from the default.</summary>
    public bool NeedsOwnCluster =>
        ReadTimeoutSeconds is not null || Protocol != BackendProtocol.Http || AffinityCookie ||
        (Algorithm is not null && Algorithm != LoadBalanceAlgorithm.RoundRobin) || UpstreamTlsSecret is not null;
}
