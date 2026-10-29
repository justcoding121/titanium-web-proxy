using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Plus.Security.Oidc;

/// <summary>A login in progress. Lives in a short-lived encrypted cookie, one per browser tab.</summary>
internal sealed class OidcFlow
{
    [JsonPropertyName("s")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("v")]
    public string Verifier { get; set; } = string.Empty;

    [JsonPropertyName("r")]
    public string ReturnTo { get; set; } = "/";

    [JsonPropertyName("u")]
    public string RedirectUri { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public long ExpiresAt { get; set; }
}

/// <summary>
/// One <c>oidc</c> policy at run time: the sign-in redirect, the callback, the session check, silent refresh,
/// and sign-out. Nothing here logs a token, a code, a state, or a cookie value.
/// </summary>
internal sealed class OidcRuntime
{
    private const string SessionPurpose = "session";
    private const string FlowPurpose = "flow";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    private static readonly long SkewSeconds = (long)OidcProvider.ClockSkew.TotalSeconds;

    private readonly OidcSettings _settings;
    private readonly OidcProvider _provider;
    private readonly OidcGuards _guards;
    private readonly OidcRefresher _refresher;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly OidcPolicyHandler _owner;

    private OidcRuntime(OidcSettings settings, OidcPolicyHandler owner, ILogger? logger, TimeProvider time, HttpClient http)
    {
        _settings = settings;
        _owner = owner;
        _logger = logger;
        _time = time;
        _guards = new OidcGuards(time);
        _refresher = new OidcRefresher(time);
        _provider = new OidcProvider(settings, http, time, logger);
    }

    private OidcRuntime(string error)
    {
        Error = error;
        _settings = null!;
        _provider = null!;
        _guards = null!;
        _refresher = null!;
        _time = TimeProvider.System;
        _owner = null!;
    }

    /// <summary>Set when the policy cannot run. Every request is refused with 503.</summary>
    public string? Error { get; }

    internal OidcProvider Provider => _provider;

    public static OidcRuntime Create(
        Titanium.Web.Proxy.Abstractions.Routing.RoutePolicyConfig policy, OidcPolicyHandler owner, ILogger? logger,
        TimeProvider time, Func<string, string?>? readEnv)
    {
        try
        {
            var settings = OidcSettings.Create(policy, readEnv);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };
            var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
            return new OidcRuntime(settings, owner, logger, time, http);
        }
        catch (OidcConfigException ex)
        {
            logger?.LogError("Plus OIDC: {Message} The policy refuses every request until the config is fixed.", ex.Message);
            return new OidcRuntime(ex.Message);
        }
    }

    public async ValueTask<RoutePolicyVerdict> EvaluateAsync(SessionEventArgs session, CancellationToken cancellationToken)
    {
        var request = session.HttpClient.Request;
        var path = PathOf(request);
        if (_settings.LogoutPath is { } logout && string.Equals(path, logout, StringComparison.Ordinal))
        {
            return Logout(session, request);
        }

        if (string.Equals(path, _settings.RedirectPath, StringComparison.Ordinal))
        {
            return await CallbackAsync(session, request, cancellationToken).ConfigureAwait(false);
        }

        return await GuardAsync(session, request, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ protected request

    private async ValueTask<RoutePolicyVerdict> GuardAsync(SessionEventArgs session, Request request, CancellationToken cancellationToken)
    {
        var cookies = OidcCookies.Values(request, _settings.CookieName);

        // Nothing the client sent for these names reaches the origin: the session cookie holds tokens, and a
        // claim header is only ever set here.
        OidcCookies.RemoveOwn(request, _settings.CookieName, _settings.FlowCookiePrefix);
        foreach (var pair in _settings.PassClaims)
        {
            request.Headers.RemoveHeader(pair.Value);
        }

        var nowUnix = _time.GetUtcNow().ToUnixTimeSeconds();
        OidcSession? current = null;
        foreach (var value in cookies)
        {
            current = OpenSession(value);
            if (current is not null)
            {
                break;
            }
        }

        if (current is not null &&
            (current.ExpiresAt <= nowUnix || !_settings.Allows(current.Email, null, current.Groups)))
        {
            current = null;
        }

        string? renewedCookie = null;
        var clearCookie = cookies.Count > 0 && current is null;
        if (current is not null && _settings.RefreshTokens && nowUnix >= current.RefreshAt - SkewSeconds)
        {
            var refreshed = await RefreshAsync(current, nowUnix).ConfigureAwait(false);
            switch (refreshed.Status)
            {
                case OidcRefresher.Status.Renewed:
                    current = refreshed.Session;
                    renewedCookie = refreshed.SetCookie;
                    break;
                case OidcRefresher.Status.Rejected:
                    current = null;
                    clearCookie = true;
                    break;
                default:
                    if (nowUnix >= current.RefreshAt + SkewSeconds)
                    {
                        return Reply(session, 503, "text/plain", "login provider unavailable", [("Retry-After", "5")]);
                    }

                    break;
            }
        }

        if (current is not null)
        {
            foreach (var pair in _settings.PassClaims)
            {
                if (current.Claims.TryGetValue(pair.Key, out var claim) && claim.Length > 0)
                {
                    request.Headers.AddHeader(pair.Value, claim);
                }
            }

            return renewedCookie is null
                ? RoutePolicyVerdict.Continue
                : new RoutePolicyVerdict { ResponseSetCookie = renewedCookie };
        }

        var clear = clearCookie ? OidcCookies.Clear(_settings.CookieName) : null;
        var method = request.Method ?? "GET";
        var browserNavigation = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                                method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (IsApiRequest(request) || !browserNavigation)
        {
            return Reply(session, 401, "application/json", "{\"error\":\"unauthenticated\"}", [], clear);
        }

        return await StartLoginAsync(session, request, clear, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OidcRefresher.Outcome> RefreshAsync(OidcSession current, long nowUnix)
    {
        if (current.RefreshToken is not { Length: > 0 } refreshToken)
        {
            // Nothing to renew with. The session lives until the token it was made from runs out.
            return new OidcRefresher.Outcome(
                nowUnix >= current.RefreshAt + SkewSeconds ? OidcRefresher.Status.Rejected : OidcRefresher.Status.Unavailable,
                null, null);
        }

        return await _refresher.RunAsync(refreshToken, () => RedeemRefreshTokenAsync(current, refreshToken)).ConfigureAwait(false);
    }

    private async Task<OidcRefresher.Outcome> RedeemRefreshTokenAsync(OidcSession old, string refreshToken)
    {
        try
        {
            var tokens = await _provider.RefreshAsync(refreshToken, CancellationToken.None).ConfigureAwait(false);
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var next = new OidcSession
            {
                Subject = old.Subject,
                Email = old.Email,
                Groups = old.Groups,
                Claims = old.Claims,
                RefreshToken = tokens.RefreshToken is { Length: > 0 } rotated ? rotated : refreshToken,
            };

            if (tokens.IdToken is { Length: > 0 } idToken)
            {
                // A refresh answer carries no nonce. Signature, issuer, audience and lifetime still apply.
                var identity = await _provider.ValidateIdTokenAsync(idToken, null, CancellationToken.None).ConfigureAwait(false);
                if (old.Subject is not null && !OidcProvider.FixedEquals(identity.Subject, old.Subject))
                {
                    return new OidcRefresher.Outcome(OidcRefresher.Status.Rejected, null, null);
                }

                if (!_settings.Allows(identity.Email, identity.EmailVerified, identity.Groups))
                {
                    return new OidcRefresher.Outcome(OidcRefresher.Status.Rejected, null, null);
                }

                next.Email = identity.Email ?? old.Email;
                next.Groups = KeptGroups(identity.Groups);
                foreach (var claim in identity.Claims)
                {
                    next.Claims[claim.Key] = claim.Value;
                }

                next.RefreshAt = identity.ExpiresAt.ToUnixTimeSeconds();
            }
            else
            {
                next.RefreshAt = now + (tokens.ExpiresInSeconds ?? 3600);
            }

            next.ExpiresAt = now + (long)_settings.SessionTtl.TotalSeconds;
            if (!TrySeal(next, out var value))
            {
                _logger?.LogWarning("Plus OIDC: policy {Policy}: the renewed session no longer fits in a cookie.", _settings.PolicyName);
                return new OidcRefresher.Outcome(OidcRefresher.Status.Rejected, null, null);
            }

            var cookie = OidcCookies.Set(_settings.CookieName, value, TimeSpan.FromSeconds(next.ExpiresAt - now));
            return new OidcRefresher.Outcome(OidcRefresher.Status.Renewed, next, cookie);
        }
        catch (Exception ex) when (ex is OidcTokenRejectedException or OidcTokenInvalidException)
        {
            _logger?.LogInformation("Plus OIDC: policy {Policy}: refresh refused ({Reason}).", _settings.PolicyName, ex.Message);
            return new OidcRefresher.Outcome(OidcRefresher.Status.Rejected, null, null);
        }
        catch (OidcProviderUnavailableException)
        {
            return new OidcRefresher.Outcome(OidcRefresher.Status.Unavailable, null, null);
        }
    }

    // ------------------------------------------------------------------ sign-in

    private async ValueTask<RoutePolicyVerdict> StartLoginAsync(
        SessionEventArgs session, Request request, string? clearCookie, CancellationToken cancellationToken)
    {
        OidcDiscovery discovery;
        try
        {
            discovery = await _provider.GetDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OidcProviderUnavailableException)
        {
            return Reply(session, 503, "text/plain", "login provider unavailable", [("Retry-After", "5")], clearCookie);
        }

        var redirectUri = RedirectUriFor(request);
        if (redirectUri is null)
        {
            return Reply(session, 400, "text/plain", "bad request", [], clearCookie);
        }

        var state = RandomText(32);
        var verifier = RandomText(32);
        var flow = new OidcFlow
        {
            State = state,
            Nonce = RandomText(32),
            Verifier = verifier,
            ReturnTo = ReturnTarget(request),
            RedirectUri = redirectUri,
            ExpiresAt = _time.GetUtcNow().Add(FlowLifetime).ToUnixTimeSeconds(),
        };
        var flowCookie = _settings.Crypto.Seal(FlowPurpose, FlowCookieName(state), JsonSerializer.SerializeToUtf8Bytes(flow));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var location = discovery.AuthorizationEndpoint +
                       (discovery.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
                       "response_type=code" +
                       "&client_id=" + Uri.EscapeDataString(_settings.ClientId) +
                       "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                       "&scope=" + Uri.EscapeDataString(string.Join(' ', _settings.Scopes)) +
                       "&state=" + Uri.EscapeDataString(state) +
                       "&nonce=" + Uri.EscapeDataString(flow.Nonce) +
                       "&code_challenge=" + challenge +
                       "&code_challenge_method=S256";

        var cookies = new List<string> { OidcCookies.Set(FlowCookieName(state), flowCookie, FlowLifetime) };
        if (clearCookie is not null)
        {
            cookies.Add(clearCookie);
        }

        return Redirect(session, location, cookies);
    }

    private string ReturnTarget(Request request)
    {
        var (_, query) = SplitTarget(request.RequestUriString);
        if (query.Length > 0 &&
            OidcRedirect.Resolve(HttpUtility.ParseQueryString(query)["rd"], _settings.RedirectOrigins) is { } requested)
        {
            return requested;
        }

        return OidcRedirect.SamePathAndQuery(request.RequestUriString) ?? "/";
    }

    private string? RedirectUriFor(Request request)
    {
        if (_settings.PublicBase is { } publicBase)
        {
            return publicBase.GetLeftPart(UriPartial.Authority) + _settings.RedirectPath;
        }

        var host = request.Host;
        if (string.IsNullOrEmpty(host) || host.Length > 255 || !IsHostText(host))
        {
            return null;
        }

        return (request.IsHttps ? "https://" : "http://") + host + _settings.RedirectPath;
    }

    private static bool IsHostText(string host)
    {
        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ callback

    private async ValueTask<RoutePolicyVerdict> CallbackAsync(SessionEventArgs session, Request request, CancellationToken cancellationToken)
    {
        var client = session.ClientRemoteEndPoint?.Address.ToString() ?? "unknown";
        if (!_guards.TryAdmitCallback(client, _owner.CallbackLimitPerMinute))
        {
            return Reply(session, 429, "text/plain", "too many requests", [("Retry-After", "60")]);
        }

        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return Reply(session, 405, "text/plain", "method not allowed", [("Allow", "GET")]);
        }

        var (_, queryText) = SplitTarget(request.RequestUriString);
        var query = HttpUtility.ParseQueryString(queryText);
        var state = query["state"];
        if (string.IsNullOrEmpty(state) || state.Length > 256)
        {
            return Reply(session, 400, "text/plain", "bad request");
        }

        var flowName = FlowCookieName(state);
        var clear = OidcCookies.Clear(flowName);
        var flow = OpenFlow(request, flowName);
        var nowUnix = _time.GetUtcNow().ToUnixTimeSeconds();

        // The state in the URL must be the one this browser started with: a callback built by someone else
        // (a forged link) has no matching cookie.
        if (flow is null || flow.ExpiresAt <= nowUnix || !OidcProvider.FixedEquals(flow.State, state))
        {
            return Reply(session, 400, "text/plain", "bad request", [], clear);
        }

        if (!_guards.TryUseState(state, FlowLifetime))
        {
            _logger?.LogWarning("Plus OIDC: policy {Policy}: a callback was replayed.", _settings.PolicyName);
            return Reply(session, 400, "text/plain", "bad request", [], clear);
        }

        if (!string.IsNullOrEmpty(query["error"]))
        {
            return Reply(session, 401, "text/plain", "sign-in was not completed", [], clear);
        }

        var code = query["code"];
        if (string.IsNullOrEmpty(code) || code.Length > 2048)
        {
            return Reply(session, 400, "text/plain", "bad request", [], clear);
        }

        OidcTokens tokens;
        OidcIdentity identity;
        try
        {
            tokens = await _provider.ExchangeCodeAsync(code, flow.Verifier, flow.RedirectUri, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(tokens.IdToken))
            {
                _logger?.LogWarning("Plus OIDC: policy {Policy}: the token answer had no ID token.", _settings.PolicyName);
                return Reply(session, 401, "text/plain", "unauthorized", [], clear);
            }

            identity = await _provider.ValidateIdTokenAsync(tokens.IdToken, flow.Nonce, cancellationToken).ConfigureAwait(false);
        }
        catch (OidcTokenRejectedException ex)
        {
            _logger?.LogWarning("Plus OIDC: policy {Policy}: the provider refused the code ({Reason}).", _settings.PolicyName, ex.Message);
            return Reply(session, 401, "text/plain", "unauthorized", [], clear);
        }
        catch (OidcTokenInvalidException ex)
        {
            _logger?.LogWarning("Plus OIDC: policy {Policy}: the ID token was rejected ({Reason}).", _settings.PolicyName, ex.Message);
            return Reply(session, 401, "text/plain", "unauthorized", [], clear);
        }
        catch (OidcProviderUnavailableException)
        {
            // Fail closed: a JWKS or token-endpoint outage must not keep admitting logins on stale keys.
            return Reply(session, 503, "text/plain", "login provider unavailable", [("Retry-After", "5")], clear);
        }

        if (string.IsNullOrEmpty(identity.Subject))
        {
            return Reply(session, 401, "text/plain", "unauthorized", [], clear);
        }

        if (!_settings.Allows(identity.Email, identity.EmailVerified, identity.Groups))
        {
            _logger?.LogInformation("Plus OIDC: policy {Policy}: a sign-in was denied by the allow rules.", _settings.PolicyName);
            return Reply(session, 403, "text/plain", "forbidden", [], clear);
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var refreshToken = _settings.RefreshTokens && tokens.RefreshToken is { Length: > 0 } rt ? rt : null;
        var fresh = new OidcSession
        {
            Subject = identity.Subject,
            Email = identity.Email,
            Groups = KeptGroups(identity.Groups),
            Claims = identity.Claims,
            RefreshAt = identity.ExpiresAt.ToUnixTimeSeconds(),
            ExpiresAt = now + (long)_settings.SessionTtl.TotalSeconds,
            RefreshToken = refreshToken,
        };
        if (_settings.RefreshTokens && refreshToken is null)
        {
            // Nothing to renew with: the session ends with the ID token.
            fresh.ExpiresAt = Math.Min(fresh.ExpiresAt, fresh.RefreshAt);
        }

        if (!TrySeal(fresh, out var value))
        {
            // Without the refresh token the session ends with the ID token, but it still fits.
            fresh.RefreshToken = null;
            fresh.ExpiresAt = Math.Min(fresh.ExpiresAt, fresh.RefreshAt);
            if (!TrySeal(fresh, out value))
            {
                _logger?.LogError("Plus OIDC: policy {Policy}: the session does not fit in a cookie. Trim passClaims or allowedGroups.", _settings.PolicyName);
                return Reply(session, 500, "text/plain", "session too large", [], clear);
            }
        }

        var lifetime = TimeSpan.FromSeconds(Math.Max(1, fresh.ExpiresAt - now));
        var target = OidcRedirect.Resolve(flow.ReturnTo, _settings.RedirectOrigins) ?? "/";
        return Redirect(session, target, [OidcCookies.Set(_settings.CookieName, value, lifetime), clear]);
    }

    // ------------------------------------------------------------------ sign-out

    private RoutePolicyVerdict Logout(SessionEventArgs session, Request request)
    {
        var (_, query) = SplitTarget(request.RequestUriString);
        var requested = query.Length > 0 ? HttpUtility.ParseQueryString(query)["rd"] : null;
        var target = OidcRedirect.Resolve(requested, _settings.RedirectOrigins) ?? "/";
        return Redirect(session, target, [OidcCookies.Clear(_settings.CookieName)]);
    }

    // ------------------------------------------------------------------ helpers

    private OidcSession? OpenSession(string value)
    {
        var plain = _settings.Crypto.Open(SessionPurpose, _settings.CookieName, value);
        return plain is null ? null : OidcSession.Parse(plain);
    }

    private OidcFlow? OpenFlow(Request request, string cookieName)
    {
        foreach (var value in OidcCookies.Values(request, cookieName))
        {
            var plain = _settings.Crypto.Open(FlowPurpose, cookieName, value);
            if (plain is null)
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<OidcFlow>(plain);
            }
            catch (JsonException)
            {
                // Authenticated but unreadable: treat as missing.
            }
        }

        return null;
    }

    private bool TrySeal(OidcSession session, out string value)
    {
        value = _settings.Crypto.Seal(SessionPurpose, _settings.CookieName, session.ToUtf8());
        return OidcCookies.Fits(_settings.CookieName, value);
    }

    /// <summary>Only the groups a rule can name are kept, so a person in hundreds of groups still fits a cookie.</summary>
    private List<string> KeptGroups(List<string> groups)
    {
        if (_settings.AllowedGroups.Count == 0)
        {
            return [];
        }

        return groups.Where(_settings.AllowedGroups.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    private string FlowCookieName(string state) =>
        _settings.FlowCookiePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)))[..16].ToLowerInvariant();

    private static string RandomText(int bytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    private static string PathOf(Request request) => SplitTarget(request.RequestUriString).Path;

    /// <summary>Splits a request target into its path and its query (without the <c>?</c>).</summary>
    private static (string Path, string Query) SplitTarget(string? target)
    {
        var raw = target ?? string.Empty;
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var slash = raw.IndexOf('/', scheme + 3);
            raw = slash < 0 ? "/" : raw[slash..];
        }

        var hash = raw.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            raw = raw[..hash];
        }

        var q = raw.IndexOf('?', StringComparison.Ordinal);
        var path = q >= 0 ? raw[..q] : raw;
        return (path.Length == 0 ? "/" : path, q >= 0 ? raw[(q + 1)..] : string.Empty);
    }

    /// <summary>A script or fetch call cannot follow a sign-in redirect, so it gets a 401 to act on.</summary>
    private static bool IsApiRequest(Request request)
    {
        if (request.Headers.GetHeaders("X-Requested-With") is { Count: > 0 })
        {
            return true;
        }

        if (request.Headers.GetHeaders("Accept") is not { Count: > 0 } accept)
        {
            return false;
        }

        var wantsJson = false;
        foreach (var line in accept)
        {
            if (line.Value.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            wantsJson |= line.Value.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }

        return wantsJson;
    }

    private static RoutePolicyVerdict Redirect(SessionEventArgs session, string location, IEnumerable<string> cookies)
    {
        var headers = new List<HttpHeader>
        {
            new("Location", location),
            new("Cache-Control", "no-store"),
            new("Content-Type", "text/plain; charset=utf-8"),
        };
        foreach (var cookie in cookies)
        {
            headers.Add(new HttpHeader("Set-Cookie", cookie));
        }

        session.GenericResponse(string.Empty, HttpStatusCode.Found, headers);
        return RoutePolicyVerdict.Respond;
    }

    private static RoutePolicyVerdict Reply(
        SessionEventArgs session, int status, string contentType, string body,
        IEnumerable<(string Name, string Value)>? extra = null, string? setCookie = null)
    {
        var headers = new List<HttpHeader>
        {
            new("Content-Type", contentType == "application/json" ? contentType : contentType + "; charset=utf-8"),
            new("Cache-Control", "no-store"),
        };
        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                headers.Add(new HttpHeader(name, value));
            }
        }

        if (setCookie is not null)
        {
            headers.Add(new HttpHeader("Set-Cookie", setCookie));
        }

        session.GenericResponse(body, (HttpStatusCode)status, headers);
        return RoutePolicyVerdict.Respond;
    }

    /// <summary>The 503 sent for every request while the policy cannot run (bad secret, missing file).</summary>
    public static RoutePolicyVerdict Refuse(SessionEventArgs session) =>
        Reply(session, 503, "text/plain", "login is not available", [("Retry-After", "30")]);
}
