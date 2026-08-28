using System.Net;
using System.Text;
using System.Text.Json;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Security;

/// <summary>Opt-in JWT/OIDC + CIDR allow/deny middleware registration.</summary>
public sealed class AccessSecurity
{
    public static AccessSecurity? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        var hasJwt = options.TryGetValue("security.jwtAuthority", out var authority) &&
                     !string.IsNullOrWhiteSpace(authority);
        var hasCidr = options.TryGetValue("security.allowCidrs", out var cidrs) &&
                      !string.IsNullOrWhiteSpace(cidrs);
        if (!hasJwt && !hasCidr)
        {
            return null;
        }

        if (context.Middleware is null)
        {
            Console.WriteLine("Plus Security: Middleware list is null — CIDR/JWT not registered.");
            return new AccessSecurity();
        }

        if (hasCidr)
        {
            context.Middleware.Add(new CidrAccessMiddleware(cidrs!));
            Console.WriteLine($"Plus Security: CIDR allow list registered ({cidrs}).");
        }

        if (hasJwt)
        {
            context.Middleware.Add(new JwtAccessMiddleware(authority!));
            Console.WriteLine($"Plus Security: JWT authority={authority} (MVP structure/exp validation).");
        }

        return new AccessSecurity();
    }
}

/// <summary>Denies requests whose client IP is outside the configured CIDR allow list.</summary>
public sealed class CidrAccessMiddleware : IProxyMiddleware
{
    private readonly List<IPNetwork> _networks = [];
    private readonly Func<object, IPAddress?>? _resolveClientIp;
    private static int _skipLogged;

    public CidrAccessMiddleware(string allowCidrs, Func<object, IPAddress?>? resolveClientIp = null)
    {
        _resolveClientIp = resolveClientIp;
        foreach (var part in allowCidrs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPNetwork.TryParse(part, out var network))
            {
                _networks.Add(network);
            }
            else if (IPAddress.TryParse(part, out var single))
            {
                var prefix = single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                _networks.Add(new IPNetwork(single, prefix));
            }
        }
    }

    /// <summary>Returns true when <paramref name="clientIp"/> is inside any configured CIDR.</summary>
    public bool IsAllowed(IPAddress clientIp) => IsIpAllowed(clientIp, _networks);

    public static bool IsIpAllowed(IPAddress clientIp, IReadOnlyList<IPNetwork> networks)
    {
        if (networks.Count == 0)
        {
            return false;
        }

        foreach (var network in networks)
        {
            if (network.Contains(clientIp))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var ip = ResolveClientIp(context.Session);
        if (ip is null)
        {
            if (Interlocked.Exchange(ref _skipLogged, 1) == 0)
            {
                Console.WriteLine("Plus Security: could not resolve client IP from Session — CIDR check skipped.");
            }

            await next(context, cancellationToken);
            return;
        }

        if (!IsAllowed(ip))
        {
            Deny(context, HttpStatusCode.Forbidden, "forbidden");
            return;
        }

        await next(context, cancellationToken);
    }

    private IPAddress? ResolveClientIp(object session)
    {
        if (_resolveClientIp is not null)
        {
            return _resolveClientIp(session);
        }

        if (session is SessionEventArgsBase args)
        {
            return args.ClientRemoteEndPoint.Address;
        }

        return null;
    }

    internal static void Deny(ProxyMiddlewareContext context, HttpStatusCode status, string body)
    {
        if (context.Session is SessionEventArgs session)
        {
            session.GenericResponse(body, status);
        }

        context.IsHandled = true;
    }
}

/// <summary>
/// Validates Authorization Bearer JWT structure and <c>exp</c> when <c>security.jwtAuthority</c> is set.
/// Full OIDC signature verification can be added later; authority is logged for operators.
/// </summary>
public sealed class JwtAccessMiddleware : IProxyMiddleware
{
    private readonly string _authority;

    public JwtAccessMiddleware(string jwtAuthority)
    {
        _authority = jwtAuthority;
    }

    public string Authority => _authority;

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var token = TryGetBearerToken(context.Session);
        if (token is null || !TryValidateJwt(token, out _))
        {
            CidrAccessMiddleware.Deny(context, HttpStatusCode.Unauthorized, "unauthorized");
            return;
        }

        await next(context, cancellationToken);
    }

    internal static string? TryGetBearerToken(object session)
    {
        if (session is not SessionEventArgsBase args)
        {
            return null;
        }

        var headers = args.HttpClient.Request.Headers.GetHeaders("Authorization");
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var value = headers[0].Value;
        const string prefix = "Bearer ";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[prefix.Length..].Trim();
        }

        return null;
    }

    /// <summary>MVP: three base64url segments, JSON payload, optional exp not expired.</summary>
    public static bool TryValidateJwt(string token, out string? error)
    {
        error = null;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            error = "jwt must have three segments";
            return false;
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("exp", out var expEl))
            {
                long exp;
                if (expEl.ValueKind == JsonValueKind.Number)
                {
                    exp = expEl.GetInt64();
                }
                else if (expEl.ValueKind == JsonValueKind.String && long.TryParse(expEl.GetString(), out var parsed))
                {
                    exp = parsed;
                }
                else
                {
                    error = "invalid exp";
                    return false;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (exp < now)
                {
                    error = "token expired";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}

/// <summary>Legacy stub type name.</summary>
public sealed class SecurityPlaceholder;
