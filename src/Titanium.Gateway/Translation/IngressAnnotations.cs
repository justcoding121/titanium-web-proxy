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
///     The <c>titanium.io/</c> annotations an Ingress may carry. Each maps to a data-plane feature.
///     An annotation under the prefix that is not listed is reported with a
///     Warning event (never ignored silently); a listed one with a value that cannot be honored makes every rule of
///     the Ingress answer <c>500</c>, with an event that says why.
/// </summary>
public static partial class IngressAnnotations
{
    public const string Prefix = "titanium.io/";

    private static readonly string[] CanaryDependents = ["canary-weight", "canary-by-header"];

    private static readonly string[] CorsDependents =
        ["cors-allow-methods", "cors-allow-headers", "cors-allow-credentials", "cors-expose-headers", "cors-max-age"];

    /// <summary>Every annotation, in the order the docs list them.</summary>
    public static IReadOnlyList<AnnotationSpec> All { get; } = InsertAdded(
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
        new("cors-allow-origin", "`*`, an origin, or a wildcard such as `https://*.example.com`. `regex:` is not accepted here",
            "`cors` policy and a `ResponseCors` transform",
            "Answers a real preflight (`OPTIONS` with `Origin` and `Access-Control-Request-Method`) with `204` and the `Access-Control-Allow-*` headers. A plain `OPTIONS` is forwarded. The actual response gets `Access-Control-Allow-Origin` (`*`, or the request `Origin` when it matches, plus `Vary: Origin`). A wildcard matches one host label and echoes that origin. An unlisted origin gets no allow header. `regex:` is configuration only, not this annotation."),
        new("cors-allow-methods", "comma-separated method names such as `GET, POST`",
            "`cors` policy `methods`",
            "Sent as `Access-Control-Allow-Methods`. Needs `cors-allow-origin`. Default when omitted: `GET, POST, OPTIONS`."),
        new("cors-allow-headers", "`*` or comma-separated header names",
            "`cors` policy `headerName`",
            "Sent as `Access-Control-Allow-Headers`. With `cors-allow-credentials` and no list, the request's `Access-Control-Request-Headers` is reflected. Needs `cors-allow-origin`."),
        new("cors-allow-credentials", "`true` or `false`",
            "`cors` policy `allowCredentials` and the `ResponseCors` transform",
            "Adds `Access-Control-Allow-Credentials: true` on the preflight and the actual response. Needs `cors-allow-origin`, and `*` is rejected."),
        new("cors-expose-headers", "comma-separated header names",
            "`cors` policy `exposeHeaders` and the `ResponseCors` transform",
            "Adds `Access-Control-Expose-Headers` to the actual response. Needs `cors-allow-origin`."),
        new("cors-max-age", "seconds, 0-86400",
            "`cors` policy `maxAgeSeconds`",
            "Sent as `Access-Control-Max-Age` on the preflight. Needs `cors-allow-origin`."),
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
    ]);

    /// <summary>Annotations inserted after <c>proxy-read-timeout</c>. Four lines each: name, values, feature, notes.</summary>
    private const string AddedCatalog = """
        proxy-connect-timeout
        seconds, 1-3600
        cluster `httpClient.connectTimeout`
        How long the data plane waits to open the backend connection. Gives the backend its own cluster.
        proxy-next-upstream
        `off`, `http_500`, `http_502`, `http_503`, `http_504`, `http_429`, `non_idempotent`, `timeout`
        cluster `retry`
        `error` is rejected: a connection failure is not replayed by this policy. `timeout` replays when `proxy-next-upstream-timeout` is set. Omitting the status tokens keeps the default 502, 503, and 504. Gives the backend its own cluster.
        proxy-next-upstream-tries
        total tries including the first, 1-1000000
        cluster `retry.attempts` (tries minus one)
        `1` turns retry off. `0` is rejected. When omitted, two tries are used once another retry annotation is set. Gives the backend its own cluster.
        proxy-next-upstream-timeout
        seconds, 1-3600
        cluster `retry.perTryTimeoutMilliseconds`
        Per-try deadline. A timeout is replayed onto another destination. Gives the backend its own cluster.
        upstream-vhost
        a DNS name
        `RequestHeaderSet` of `Host`
        Sets the Host header sent to the backend, after any rewrite. No scheme, port, or path.
        denylist-source-range
        comma-separated addresses or CIDR prefixes
        `ipAllowDeny` policy (`deny`)
        A listed address gets 403. Combined with `whitelist-source-range` on one policy.
        app-root
        an absolute path starting with `/`
        redirect route for exact `/`
        Adds a 302 to that path for each host, ahead of the Ingress's other rules. Skipped, with a warning, when that host already has an exact `/` rule. No query.
        permanent-redirect
        an absolute http or https URL
        redirect route
        The Ingress's rules redirect and do not use a backend. No query, fragment, or credentials. `permanent-redirect-code` is 301, 302, 307, or 308 (default 301). Rejected together with `canary` set to true.
        permanent-redirect-code
        `301`, `302`, `307`, or `308`
        redirect status
        Used with `permanent-redirect`. Default 301.
        """;

    private static IReadOnlyList<AnnotationSpec> InsertAdded(IReadOnlyList<AnnotationSpec> specs)
    {
        var list = new List<AnnotationSpec>(specs.Count + 9);
        foreach (var spec in specs)
        {
            list.Add(spec);
            if (spec.Name == "proxy-read-timeout")
            {
                list.AddRange(ReadAdded());
            }
        }

        return list;
    }

    private static IEnumerable<AnnotationSpec> ReadAdded()
    {
        var lines = AddedCatalog.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i += 4)
        {
            yield return new AnnotationSpec(lines[i], lines[i + 1], lines[i + 2], lines[i + 3]);
        }
    }

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

        if (settings.CorsOrigins is null)
        {
            foreach (var dependent in CorsDependents.Where(d => annotations.ContainsKey(Prefix + d)))
            {
                settings.Problems.Add($"{Prefix}{dependent} needs {Prefix}cors-allow-origin.");
            }
        }
        else if (settings.CorsAllowCredentials == true && CorsOriginIsWildcard(settings.CorsOrigins))
        {
            settings.Problems.Add($"{Prefix}cors-allow-credentials cannot be combined with {Prefix}cors-allow-origin \"*\".");
        }

        if (settings.RetryOff && (settings.RetryTries is not null || settings.RetryPerTryTimeoutSeconds is not null || settings.RetryRequested))
        {
            settings.Problems.Add($"{Prefix}proxy-next-upstream: \"off\" cannot be combined with other retry annotations.");
        }

        if (settings.PermanentRedirectCode is not null && settings.PermanentRedirectHost is null)
        {
            settings.Problems.Add($"{Prefix}permanent-redirect-code needs {Prefix}permanent-redirect.");
        }

        if (settings.Canary && settings.PermanentRedirectHost is not null)
        {
            settings.Problems.Add($"{Prefix}permanent-redirect cannot be combined with {Prefix}canary: \"true\".");
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
                if (TryCidrList(value, out var ranges))
                {
                    settings.WhitelistRanges = ranges;
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
                if (origins is ["*"] || (origins.Length > 0 && origins.All(IsOrigin) && !origins.Any(part => part.StartsWith("regex:", StringComparison.Ordinal))))
                {
                    settings.CorsOrigins = string.Join(",", origins);
                }
                else
                {
                    Bad("\"*\", comma-separated origins, or a wildcard such as https://*.example.com. regex: is not accepted");
                }

                break;
            case "cors-allow-methods":
                if (TryTokenList(value, allowStar: false, out var methods))
                {
                    settings.CorsMethods = methods;
                }
                else
                {
                    Bad("comma-separated method names such as GET, POST");
                }

                break;
            case "cors-allow-headers":
                if (TryTokenList(value, allowStar: true, out var allowHeaders))
                {
                    settings.CorsAllowHeaders = allowHeaders;
                }
                else
                {
                    Bad("\"*\" or comma-separated header names");
                }

                break;
            case "cors-allow-credentials":
                if (TryBool(value, out var credentials))
                {
                    settings.CorsAllowCredentials = credentials;
                }
                else
                {
                    Bad("\"true\" or \"false\"");
                }

                break;
            case "cors-expose-headers":
                if (TryTokenList(value, allowStar: false, out var exposeHeaders))
                {
                    settings.CorsExposeHeaders = exposeHeaders;
                }
                else
                {
                    Bad("comma-separated header names");
                }

                break;
            case "cors-max-age":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var maxAge) && maxAge is >= 0 and <= 86_400)
                {
                    settings.CorsMaxAge = maxAge;
                }
                else
                {
                    Bad("a number of seconds from 0 to 86400");
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
            case "proxy-connect-timeout":
                if (TryInt(value, 1, 3600, out var connectSeconds))
                {
                    settings.ConnectTimeoutSeconds = connectSeconds;
                }
                else
                {
                    Bad("a number of seconds from 1 to 3600");
                }

                break;
            case "proxy-next-upstream":
                if (!TryNextUpstream(value, settings, out var retryProblem))
                {
                    settings.Problems.Add($"{key}: {retryProblem}.");
                }

                break;
            case "proxy-next-upstream-tries":
                if (TryInt(value, 1, 1_000_000, out var tries))
                {
                    settings.RetryTries = tries;
                }
                else
                {
                    Bad("a number of tries from 1 to 1000000");
                }

                break;
            case "proxy-next-upstream-timeout":
                if (TryInt(value, 1, 3600, out var trySeconds))
                {
                    settings.RetryPerTryTimeoutSeconds = trySeconds;
                }
                else
                {
                    Bad("a number of seconds from 1 to 3600");
                }

                break;
            case "upstream-vhost":
                if (IsDnsHost(value))
                {
                    settings.UpstreamVhost = value;
                }
                else
                {
                    Bad("a DNS name with no scheme, port, or path");
                }

                break;
            case "denylist-source-range":
                if (TryCidrList(value, out var denied))
                {
                    settings.DenyRanges = denied;
                }
                else
                {
                    Bad("a comma-separated list of addresses or CIDR prefixes");
                }

                break;
            case "app-root":
                if (value.StartsWith('/') && !value.Contains('?', StringComparison.Ordinal) && !value.Contains('#', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal))
                {
                    settings.AppRoot = value;
                }
                else
                {
                    Bad("an absolute path starting with \"/\" and no query");
                }

                break;
            case "permanent-redirect":
                if (TryPermanentRedirect(value, out var scheme, out var redirectHost, out var port, out var path))
                {
                    settings.PermanentRedirectScheme = scheme;
                    settings.PermanentRedirectHost = redirectHost;
                    settings.PermanentRedirectPort = port;
                    settings.PermanentRedirectPath = path;
                }
                else
                {
                    Bad("an absolute http or https URL with no query, fragment, or credentials");
                }

                break;
            case "permanent-redirect-code":
                if (value is "301" or "302" or "307" or "308")
                {
                    settings.PermanentRedirectCode = int.Parse(value, CultureInfo.InvariantCulture);
                }
                else
                {
                    Bad("301, 302, 307, or 308");
                }

                break;
        }
    }

    private static bool TryInt(string value, int min, int max, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= min && number <= max;

    private static bool TryCidrList(string value, out string canonical)
    {
        var ranges = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ranges.Length > 0 && ranges.All(range => CidrPrefix.TryParse(range, out _)))
        {
            canonical = string.Join(",", ranges);
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    private static bool TryNextUpstream(string value, IngressSettings settings, out string problem)
    {
        problem = string.Empty;
        var tokens = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            problem = "expected at least one token";
            return false;
        }

        if (tokens.Any(token => token.Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            if (tokens.Length != 1)
            {
                problem = "\"off\" cannot be combined with other tokens";
                return false;
            }

            settings.RetryOff = true;
            return true;
        }

        var codes = new List<int>();
        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "error":
                    problem = "\"error\" is not honored, because a connection failure is not replayed by the status-retry policy";
                    return false;
                case "timeout":
                    break;
                case "non_idempotent":
                    settings.RetryNonIdempotent = true;
                    break;
                case "http_500":
                    codes.Add(500);
                    break;
                case "http_502":
                    codes.Add(502);
                    break;
                case "http_503":
                    codes.Add(503);
                    break;
                case "http_504":
                    codes.Add(504);
                    break;
                case "http_429":
                    codes.Add(429);
                    break;
                default:
                    problem = $"unknown token \"{token}\"";
                    return false;
            }
        }

        settings.RetryRequested = true;
        if (codes.Count > 0)
        {
            settings.RetryStatusCodes = codes.Distinct().ToArray();
        }

        return true;
    }

    private static bool IsDnsHost(string value) =>
        value.Length is > 0 and <= 253 && DnsName().IsMatch(value.ToLowerInvariant());

    private static bool TryPermanentRedirect(string value, out string scheme, out string host, out int? port, out string path)
    {
        scheme = string.Empty;
        host = string.Empty;
        port = null;
        path = "/";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        scheme = uri.Scheme;
        host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host + "]" : uri.Host;
        port = uri.IsDefaultPort ? null : uri.Port;
        path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return host.Length > 0;
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

    private static bool CorsOriginIsWildcard(string origins) =>
        origins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(part => part == "*");

    private static bool TryTokenList(string value, bool allowStar, out string canonical)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var star = parts.Any(part => part == "*");
        if (parts.Length == 0 || !parts.All(part => HeaderToken().IsMatch(part)) || (star && !(allowStar && parts.Length == 1)))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = string.Join(",", parts);
        return true;
    }

    private static bool IsOrigin(string value)
    {
        if (value.Contains('*'))
        {
            return IsWildcardOrigin(value);
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.AbsolutePath is "" or "/") && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
            !value.EndsWith('/');
    }

    private static bool IsWildcardOrigin(string value)
    {
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || value[..schemeEnd] is not ("http" or "https"))
        {
            return false;
        }

        var rest = value[(schemeEnd + 3)..];
        if (!rest.StartsWith("*.", StringComparison.Ordinal) || rest.Contains('/') || rest.Contains('?') || rest.Contains('#') || rest.AsSpan(2).Contains('*'))
        {
            return false;
        }

        var host = rest[2..];
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                return false;
            }

            host = host[..colon];
        }

        var dot = host.IndexOf('.');
        return dot > 0 && dot < host.Length - 1 && !host.EndsWith('.');
    }
}

/// <summary>The annotations of one Ingress, parsed.</summary>
internal sealed class IngressSettings
{
    public bool Canary { get; set; }

    public int CanaryWeight { get; set; }

    public string? CanaryHeader { get; set; }

    public string? RewriteTarget { get; set; }

    public int? ReadTimeoutSeconds { get; set; }

    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary><c>proxy-next-upstream: off</c>.</summary>
    public bool RetryOff { get; set; }

    /// <summary>A retry token list other than <c>off</c> was accepted.</summary>
    public bool RetryRequested { get; set; }

    public int? RetryTries { get; set; }

    public int? RetryPerTryTimeoutSeconds { get; set; }

    public bool RetryNonIdempotent { get; set; }

    /// <summary>Null keeps the data-plane default of 502, 503, and 504.</summary>
    public int[]? RetryStatusCodes { get; set; }

    public string? UpstreamVhost { get; set; }

    public string? DenyRanges { get; set; }

    public string? AppRoot { get; set; }

    public string? PermanentRedirectScheme { get; set; }

    public string? PermanentRedirectHost { get; set; }

    public int? PermanentRedirectPort { get; set; }

    public string? PermanentRedirectPath { get; set; }

    public int? PermanentRedirectCode { get; set; }

    /// <summary>True when this Ingress should emit a cluster <c>retry</c> object.</summary>
    public bool EmitsRetry
    {
        get
        {
            if (RetryOff)
            {
                return false;
            }

            var active = RetryRequested || RetryTries is > 1 || RetryPerTryTimeoutSeconds is not null;
            var tries = RetryTries ?? (active ? 2 : 1);
            return active && tries > 1;
        }
    }

    /// <summary>Null for no annotation; 0 for "no limit".</summary>
    public long? BodyLimitBytes { get; set; }

    public BackendProtocol Protocol { get; set; } = BackendProtocol.Http;

    public bool AffinityCookie { get; set; }

    public LoadBalanceAlgorithm? Algorithm { get; set; }

    public string? WhitelistRanges { get; set; }

    public int? LimitRps { get; set; }

    public string? CorsOrigins { get; set; }

    public string? CorsMethods { get; set; }

    public string? CorsAllowHeaders { get; set; }

    public bool? CorsAllowCredentials { get; set; }

    public string? CorsExposeHeaders { get; set; }

    public int? CorsMaxAge { get; set; }

    public string? AuthUrl { get; set; }

    public string? UpstreamTlsSecret { get; set; }

    /// <summary>Annotations under the prefix that are not in the table.</summary>
    public List<string> Unknown { get; } = [];

    /// <summary>Annotations the controller knows but cannot honor. Any entry makes every rule of the Ingress answer 500.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>True when the backend needs a cluster of its own, because a cluster-level setting differs from the default.</summary>
    public bool NeedsOwnCluster =>
        ReadTimeoutSeconds is not null || ConnectTimeoutSeconds is not null || EmitsRetry ||
        Protocol != BackendProtocol.Http || AffinityCookie ||
        (Algorithm is not null && Algorithm != LoadBalanceAlgorithm.RoundRobin) || UpstreamTlsSecret is not null;
}
