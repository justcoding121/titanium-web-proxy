using System.Net;
using System.Text;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Plus.Security;

/// <summary>
/// Opt-in API key header and/or HTTP Basic auth for reverse-proxy edge. Basic passwords may be plaintext or
/// <c>{bcrypt}</c> / <c>{pbkdf2}</c> hashes (<see cref="BasicUserStore"/>); every secret comparison is constant-time.
/// With a throttle, a remote that keeps failing is answered 429 until its window clears.
/// </summary>
public sealed class ApiKeyBasicAuthMiddleware : IProxyMiddleware
{
    private readonly List<byte[]> _apiKeyDigests;
    private readonly BasicUserStore _basicUsers;
    private readonly string _apiKeyHeader;
    private readonly AuthThrottle? _throttle;

    public ApiKeyBasicAuthMiddleware(
        IEnumerable<string>? apiKeys,
        IReadOnlyDictionary<string, string>? basicUsers,
        string apiKeyHeader = "X-Api-Key",
        AuthThrottle? throttle = null)
    {
        _apiKeyDigests = apiKeys is null
            ? []
            : apiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal)
                .Select(ConstantTime.Digest).ToList();
        _basicUsers = new BasicUserStore(
            basicUsers ?? new Dictionary<string, string>(StringComparer.Ordinal));
        _apiKeyHeader = string.IsNullOrWhiteSpace(apiKeyHeader) ? "X-Api-Key" : apiKeyHeader.Trim();
        _throttle = throttle;
    }

    /// <summary>User names that still carry a plaintext password (never the passwords).</summary>
    public IReadOnlyList<string> PlaintextUsers => _basicUsers.PlaintextUsers;

    public ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        if (_apiKeyDigests.Count == 0 && _basicUsers.Count == 0)
        {
            return next(context, cancellationToken);
        }

        var remote = ResolveRemote(context);
        if (_throttle is not null && remote is not null &&
            _throttle.IsBlocked(remote, out var retryAfter, out _))
        {
            context.HandledHeaders = [new KeyValuePair<string, string>("Retry-After", retryAfter.ToString())];
            CidrAccessMiddleware.Deny(context, HttpStatusCode.TooManyRequests, "too many failed attempts");
            return ValueTask.CompletedTask;
        }

        if (IsAuthorized(context))
        {
            return next(context, cancellationToken);
        }

        if (_throttle is not null && remote is not null)
        {
            _throttle.RecordFailure(remote);
        }

        CidrAccessMiddleware.Deny(context, HttpStatusCode.Unauthorized, "unauthorized");
        return ValueTask.CompletedTask;
    }

    public bool IsAuthorized(ProxyMiddlewareContext context)
    {
        if (_apiKeyDigests.Count > 0)
        {
            var key = GetHeader(context, _apiKeyHeader);
            if (key is not null && MatchesAnyApiKey(key))
            {
                return true;
            }
        }

        if (_basicUsers.Count > 0)
        {
            var auth = context.Request?.Authorization ?? GetHeader(context, "Authorization");
            if (TryParseBasic(auth, out var user, out var pass) && _basicUsers.Verify(user, pass))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Visits every configured key so the time taken does not reveal which one matched.</summary>
    private bool MatchesAnyApiKey(string presented)
    {
        var digest = ConstantTime.Digest(presented);
        var match = false;
        foreach (var candidate in _apiKeyDigests)
        {
            match |= System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(digest, candidate);
        }

        return match;
    }

    private static string? ResolveRemote(ProxyMiddlewareContext context)
    {
        if (context.ClientRemoteEndPoint is { } ep)
        {
            return ep.Address.ToString();
        }

        return context.Session is SessionEventArgsBase args ? args.ClientRemoteEndPoint.Address.ToString() : null;
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
