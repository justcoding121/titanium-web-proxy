using System.Net;
using System.Text;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Plus.Security;

/// <summary>Opt-in API key header and/or HTTP Basic auth for reverse-proxy edge.</summary>
public sealed class ApiKeyBasicAuthMiddleware : IProxyMiddleware
{
    private readonly HashSet<string> _apiKeys;
    private readonly Dictionary<string, string> _basicUsers;
    private readonly string _apiKeyHeader;

    public ApiKeyBasicAuthMiddleware(
        IEnumerable<string>? apiKeys,
        IReadOnlyDictionary<string, string>? basicUsers,
        string apiKeyHeader = "X-Api-Key")
    {
        _apiKeys = apiKeys is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(apiKeys.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.Ordinal);
        _basicUsers = basicUsers is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(basicUsers, StringComparer.Ordinal);
        _apiKeyHeader = string.IsNullOrWhiteSpace(apiKeyHeader) ? "X-Api-Key" : apiKeyHeader.Trim();
    }

    public ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        if (_apiKeys.Count == 0 && _basicUsers.Count == 0)
        {
            return next(context, cancellationToken);
        }

        if (IsAuthorized(context))
        {
            return next(context, cancellationToken);
        }

        CidrAccessMiddleware.Deny(context, HttpStatusCode.Unauthorized, "unauthorized");
        return ValueTask.CompletedTask;
    }

    public bool IsAuthorized(ProxyMiddlewareContext context)
    {
        if (_apiKeys.Count > 0)
        {
            var key = GetHeader(context, _apiKeyHeader);
            if (key is not null && _apiKeys.Contains(key))
            {
                return true;
            }
        }

        if (_basicUsers.Count > 0)
        {
            var auth = context.Request?.Authorization ?? GetHeader(context, "Authorization");
            if (TryParseBasic(auth, out var user, out var pass) &&
                _basicUsers.TryGetValue(user, out var expected) &&
                string.Equals(expected, pass, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetHeader(ProxyMiddlewareContext context, string name)
    {
        var values = context.Request?.GetHeaderValues?.Invoke(name);
        if (values is { Count: > 0 })
        {
            return values[0];
        }

        if (context.Session is SessionEventArgsBase args)
        {
            var headers = args.HttpClient.Request.Headers.GetHeaders(name);
            if (headers is { Count: > 0 })
            {
                return headers[0].Value;
            }
        }

        return null;
    }

    internal static bool TryParseBasic(string? authorization, out string user, out string password)
    {
        user = "";
        password = "";
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
            var idx = raw.IndexOf(':');
            if (idx <= 0)
            {
                return false;
            }

            user = raw[..idx];
            password = raw[(idx + 1)..];
            return user.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Opt-in CORS preflight short-circuit + response header injection.</summary>
public sealed class CorsMiddleware : IProxyMiddleware
{
    private readonly string _allowOrigin;
    private readonly string _allowMethods;
    private readonly string _allowHeaders;
    private readonly bool _allowCredentials;
    private readonly int _maxAgeSeconds;

    public CorsMiddleware(
        string allowOrigin = "*",
        string allowMethods = "GET,POST,PUT,PATCH,DELETE,OPTIONS",
        string allowHeaders = "*",
        bool allowCredentials = false,
        int maxAgeSeconds = 86400)
    {
        _allowOrigin = string.IsNullOrWhiteSpace(allowOrigin) ? "*" : allowOrigin;
        _allowMethods = string.IsNullOrWhiteSpace(allowMethods) ? "GET,POST,PUT,PATCH,DELETE,OPTIONS" : allowMethods;
        _allowHeaders = string.IsNullOrWhiteSpace(allowHeaders) ? "*" : allowHeaders;
        _allowCredentials = allowCredentials;
        _maxAgeSeconds = Math.Max(0, maxAgeSeconds);
    }

    public ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var method = context.Request?.Method
                     ?? (context.Session is SessionEventArgsBase s ? s.HttpClient.Request.Method : null)
                     ?? "GET";

        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            RespondPreflight(context);
            return ValueTask.CompletedTask;
        }

        // Stash CORS headers for AfterResponse / HandledHeaders when short-circuiting later.
        context.Items["cors.allowOrigin"] = _allowOrigin;
        context.Items["cors.allowCredentials"] = _allowCredentials;
        return next(context, cancellationToken);
    }

    public IReadOnlyList<HttpHeader> BuildResponseHeaders()
    {
        var list = new List<HttpHeader>
        {
            new("Access-Control-Allow-Origin", _allowOrigin),
            new("Access-Control-Allow-Methods", _allowMethods),
            new("Access-Control-Allow-Headers", _allowHeaders),
            new("Access-Control-Max-Age", _maxAgeSeconds.ToString()),
        };
        if (_allowCredentials)
        {
            list.Add(new HttpHeader("Access-Control-Allow-Credentials", "true"));
        }

        return list;
    }

    private void RespondPreflight(ProxyMiddlewareContext context)
    {
        var headers = BuildResponseHeaders();
        if (context.Session is SessionEventArgs session)
        {
            session.GenericResponse(Array.Empty<byte>(), HttpStatusCode.NoContent, headers);
        }

        context.HandledStatusCode = (int)HttpStatusCode.NoContent;
        context.HandledBody = "";
        context.HandledHeaders = headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList();
        context.IsHandled = true;
    }
}
