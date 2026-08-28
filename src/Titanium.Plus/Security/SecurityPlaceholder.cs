using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Titanium.Plus;
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
            PlusLog.Warn(context, "Plus Security: Middleware list is null — CIDR/JWT not registered.");
            return new AccessSecurity();
        }

        if (hasCidr)
        {
            context.Middleware.Add(new CidrAccessMiddleware(cidrs!, logger: context.Logger));
            PlusLog.Info(context, $"Plus Security: CIDR allow list registered ({cidrs}).");
        }

        if (hasJwt)
        {
            options.TryGetValue("security.jwtAudience", out var audience);
            options.TryGetValue("security.jwksUrl", out var jwksUrl);
            var middleware = new JwtAccessMiddleware(
                authority!,
                audience,
                jwksUrl,
                httpClientFactory: null,
                logger: context.Logger);
            context.Middleware.Add(middleware);
            PlusLog.Info(context,
                $"Plus Security: JWT authority={authority} audience={audience ?? "(any)"} jwks={jwksUrl ?? "(oidc discovery)"}.");
        }

        return new AccessSecurity();
    }
}

/// <summary>Denies requests whose client IP is outside the configured CIDR allow list.</summary>
public sealed class CidrAccessMiddleware : IProxyMiddleware
{
    private readonly List<IPNetwork> _networks = [];
    private readonly Func<object, IPAddress?>? _resolveClientIp;
    private readonly ILogger? _logger;
    private static int _skipLogged;

    public CidrAccessMiddleware(string allowCidrs, Func<object, IPAddress?>? resolveClientIp = null, ILogger? logger = null)
    {
        _resolveClientIp = resolveClientIp;
        _logger = logger;
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

    public static bool IsIpAllowed(IPAddress clientIp, IReadOnlyList<IPNetwork> networks) =>
        networks.Count > 0 && networks.Any(network => network.Contains(clientIp));

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
                _logger?.LogWarning("Plus Security: could not resolve client IP from Session — CIDR check skipped.");
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
/// Validates Authorization Bearer JWT via OIDC discovery / JWKS (RS256/ES256) with iss/aud/nbf/exp.
/// </summary>
public sealed class JwtAccessMiddleware : IProxyMiddleware
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private readonly string _authority;
    private readonly string? _audience;
    private readonly string? _jwksUrlOverride;
    private readonly Func<HttpClient>? _httpClientFactory;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenValidationParameters? _validation;
    private DateTimeOffset _keysLoadedAt = DateTimeOffset.MinValue;

    public JwtAccessMiddleware(
        string jwtAuthority,
        string? audience = null,
        string? jwksUrl = null,
        Func<HttpClient>? httpClientFactory = null,
        ILogger? logger = null)
    {
        _authority = jwtAuthority.TrimEnd('/');
        _audience = string.IsNullOrWhiteSpace(audience) ? null : audience;
        _jwksUrlOverride = string.IsNullOrWhiteSpace(jwksUrl) ? null : jwksUrl;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Authority => _authority;

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context,
        ProxyMiddlewareDelegate next,
        CancellationToken cancellationToken)
    {
        var token = TryGetBearerToken(context.Session);
        if (token is null || !await TryValidateJwtAsync(token, cancellationToken).ConfigureAwait(false))
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

    /// <summary>Validates JWT signature and standard claims using cached JWKS keys.</summary>
    public async Task<bool> TryValidateJwtAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var parameters = await EnsureValidationParametersAsync(cancellationToken).ConfigureAwait(false);
            if (parameters is null)
            {
                return false;
            }

            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(token, parameters, out _);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Plus Security: JWT validation failed");
            return false;
        }
    }

    /// <summary>
    /// Synchronous validate for unit tests when keys are preloaded via <see cref="SetValidationParametersForTests"/>.
    /// </summary>
    public static bool TryValidateJwt(string token, out string? error) =>
        TryValidateJwt(token, validationParameters: null, out error);

    public static bool TryValidateJwt(string token, TokenValidationParameters? validationParameters, out string? error)
    {
        error = null;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            error = "jwt must have three segments";
            return false;
        }

        if (validationParameters is null)
        {
            error = "JWKS validation parameters required";
            return false;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(token, validationParameters, out _);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void SetValidationParametersForTests(TokenValidationParameters parameters)
    {
        _validation = parameters;
        _keysLoadedAt = DateTimeOffset.UtcNow;
    }

    private async Task<TokenValidationParameters?> EnsureValidationParametersAsync(CancellationToken cancellationToken)
    {
        if (_validation is not null && DateTimeOffset.UtcNow - _keysLoadedAt < TimeSpan.FromHours(1))
        {
            return _validation;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_validation is not null && DateTimeOffset.UtcNow - _keysLoadedAt < TimeSpan.FromHours(1))
            {
                return _validation;
            }

            using var http = _httpClientFactory?.Invoke() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var jwksUrl = _jwksUrlOverride;
            if (string.IsNullOrEmpty(jwksUrl))
            {
                var discoveryUrl = $"{_authority}/.well-known/openid-configuration";
                var discovery = await http.GetFromJsonAsync<OidcDiscoveryDocument>(discoveryUrl, cancellationToken)
                    .ConfigureAwait(false);
                jwksUrl = discovery?.JwksUri;
            }

            if (string.IsNullOrEmpty(jwksUrl))
            {
                _logger?.LogWarning("Plus Security: could not resolve JWKS URL for {Authority}", _authority);
                return null;
            }

            var jwksJson = await http.GetStringAsync(jwksUrl, cancellationToken).ConfigureAwait(false);
            var keys = JsonWebKeySet.Create(jwksJson).GetSigningKeys();
            _validation = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _authority,
                ValidateAudience = _audience is not null,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys,
                ClockSkew = ClockSkew,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
            };
            _keysLoadedAt = DateTimeOffset.UtcNow;
            return _validation;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Plus Security: failed to load JWKS for {Authority}", _authority);
            return _validation;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class OidcDiscoveryDocument
    {
        [JsonPropertyName("jwks_uri")]
        public string? JwksUri { get; set; }
    }
}
