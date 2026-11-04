using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Security.Oidc;

namespace Titanium.Plus.Dashboard;

/// <summary>OIDC settings that turn on dashboard SSO. Null/empty Issuer means SSO is off.</summary>
public sealed class DashboardOidcOptions
{
    public required string Issuer { get; init; }

    public required string ClientId { get; init; }

    public string? ClientSecret { get; init; }

    /// <summary>32-byte cookie key as base64 or 64 hex digits (comma-separated rotation keys allowed).</summary>
    public required string CookieSecret { get; init; }

    /// <summary>Absolute redirect URI registered with the IdP. Required when SSO is enabled.</summary>
    public required string RedirectUri { get; init; }

    public IReadOnlyList<string>? OperatorGroups { get; init; }

    public IReadOnlyList<string>? ViewerGroups { get; init; }

    public IReadOnlyList<string>? AllowedEmails { get; init; }

    public IReadOnlyList<string>? AllowedEmailDomains { get; init; }

    public string? GroupsClaim { get; init; }

    /// <summary>Optional handler for tests (for example to point at <c>FakeIdp</c>).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
}

/// <summary>A login in progress for the dashboard SSO flow cookie.</summary>
internal sealed class DashboardOidcFlow
{
    [JsonPropertyName("s")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("v")]
    public string Verifier { get; set; } = string.Empty;

    [JsonPropertyName("u")]
    public string RedirectUri { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public long ExpiresAt { get; set; }
}

/// <summary>
///     Dashboard authorization-code + PKCE sign-in. Reuses <see cref="OidcProvider"/> for discovery, token
///     exchange, and ID-token validation (including fail-closed key refresh). Constructed only when OIDC is configured.
/// </summary>
internal sealed class DashboardOidc : IDisposable
{
    public const string FlowCookiePrefix = "titanium_plus_oidc_f";
    private const string FlowPurpose = "flow";
    private const string PlainTextUtf8 = "text/plain; charset=utf-8";
    private const string BadRequestBody = "bad request";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);

    private readonly OidcSettings _settings;
    private readonly OidcProvider _provider;
    private readonly OidcGuards _guards;
    private readonly OidcCookieCrypto _crypto;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _operatorGroups;
    private readonly HashSet<string> _viewerGroups;
    private readonly string _redirectUri;
    private readonly HttpClient _http;
    private bool _disposed;

    private DashboardOidc(DashboardOidcParts parts)
    {
        _settings = parts.Settings;
        _provider = parts.Provider;
        _guards = parts.Guards;
        _crypto = parts.Crypto;
        _time = parts.Time;
        _operatorGroups = parts.OperatorGroups;
        _viewerGroups = parts.ViewerGroups;
        _redirectUri = parts.RedirectUri;
        _http = parts.Http;
    }

    private sealed record DashboardOidcParts(
        OidcSettings Settings,
        OidcProvider Provider,
        OidcGuards Guards,
        OidcCookieCrypto Crypto,
        TimeProvider Time,
        HashSet<string> OperatorGroups,
        HashSet<string> ViewerGroups,
        string RedirectUri,
        HttpClient Http);

    /// <summary>How many times the provider was built (tests: default config must stay at zero).</summary>
    internal static int ConstructedProviders { get; private set; }

    internal OidcProvider Provider => _provider;

    public static DashboardOidc Create(DashboardOidcOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new OidcConfigException("dashboard OIDC needs issuer and clientId.");
        }

        if (string.IsNullOrWhiteSpace(options.RedirectUri) ||
            !Uri.TryCreate(options.RedirectUri.Trim(), UriKind.Absolute, out var redirect) ||
            (redirect.Scheme != Uri.UriSchemeHttp && redirect.Scheme != Uri.UriSchemeHttps))
        {
            throw new OidcConfigException("dashboard OIDC needs an absolute http(s) redirectUri.");
        }

        var operatorGroups = ToSet(options.OperatorGroups);
        var viewerGroups = ToSet(options.ViewerGroups);
        var allowedEmails = ToSet(options.AllowedEmails);
        var allowedDomains = ToSet(options.AllowedEmailDomains);
        if (operatorGroups.Count == 0 && viewerGroups.Count == 0 &&
            allowedEmails.Count == 0 && allowedDomains.Count == 0)
        {
            throw new OidcConfigException(
                "dashboard OIDC needs at least one of allowedEmails, allowedEmailDomains, operatorGroups, or viewerGroups.");
        }

        OidcCookieCrypto crypto;
        try
        {
            crypto = new OidcCookieCrypto(OidcCookieCrypto.ParseKeys(options.CookieSecret));
        }
        catch (OidcConfigException ex)
        {
            throw new OidcConfigException("dashboard OIDC: " + ex.Message);
        }

        var settings = OidcSettings.CreateDashboard(
            options.Issuer.Trim(),
            options.ClientId.Trim(),
            options.ClientSecret,
            crypto,
            options.AllowedEmails,
            options.AllowedEmailDomains,
            options.GroupsClaim);

        var handler = options.HttpHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        var ownsHandler = options.HttpHandler is null;
        var http = new HttpClient(handler, disposeHandler: ownsHandler) { Timeout = TimeSpan.FromSeconds(10) };
        var time = timeProvider ?? TimeProvider.System;
        var provider = new OidcProvider(settings, http, time, logger: null);
        ConstructedProviders++;

        return new DashboardOidc(new DashboardOidcParts(
            settings,
            provider,
            new OidcGuards(time),
            crypto,
            time,
            operatorGroups,
            viewerGroups,
            options.RedirectUri.Trim(),
            http));
    }

    /// <summary>Resets the construction counter (tests).</summary>
    internal static void ResetConstructedProviders() => ConstructedProviders = 0;

    public async Task<ControlHttpResponse> StartLoginAsync(ControlHttpRequest request, bool secure, CancellationToken cancellationToken)
    {
        OidcDiscovery discovery;
        try
        {
            discovery = await _provider.GetDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OidcProviderUnavailableException)
        {
            return ControlHttpResponse.Text(503, PlainTextUtf8, "login provider unavailable",
                new KeyValuePair<string, string>("Retry-After", "5"));
        }

        var redirectUri = _redirectUri;
        var state = RandomText(32);
        var verifier = RandomText(32);
        var flow = new DashboardOidcFlow
        {
            State = state,
            Nonce = RandomText(32),
            Verifier = verifier,
            RedirectUri = redirectUri,
            ExpiresAt = _time.GetUtcNow().Add(FlowLifetime).ToUnixTimeSeconds(),
        };
        var flowName = FlowCookieName(state);
        var sealedFlow = _crypto.Seal(FlowPurpose, flowName, JsonSerializer.SerializeToUtf8Bytes(flow));
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

        return ControlHttpResponse.Text(303, PlainTextUtf8, "",
            new KeyValuePair<string, string>("Location", location),
            FlowCookieHeader(flowName, sealedFlow, FlowLifetime, secure));
    }

    public async Task<(ControlHttpResponse Response, OidcIdentity? Identity, DashboardRole Role)> CompleteCallbackAsync(
        ControlHttpRequest request,
        bool secure,
        CancellationToken cancellationToken)
    {
        var client = request.RemoteAddress?.ToString() ?? "unknown";
        if (!_guards.TryAdmitCallback(client, limitPerMinute: 30))
        {
            return (ControlHttpResponse.Text(429, PlainTextUtf8, "too many requests",
                new KeyValuePair<string, string>("Retry-After", "60")), null, DashboardRole.Viewer);
        }

        var state = request.QueryValue("state");
        if (string.IsNullOrEmpty(state) || state.Length > 256)
        {
            return (ControlHttpResponse.Text(400, PlainTextUtf8, BadRequestBody), null, DashboardRole.Viewer);
        }

        var flowName = FlowCookieName(state);
        var clear = ClearFlowCookie(flowName, secure);
        var flow = OpenFlow(request, flowName);
        var nowUnix = _time.GetUtcNow().ToUnixTimeSeconds();
        if (flow is null || flow.ExpiresAt <= nowUnix || !OidcProvider.FixedEquals(flow.State, state))
        {
            return (ControlHttpResponse.Text(400, PlainTextUtf8, BadRequestBody, clear), null, DashboardRole.Viewer);
        }

        if (!_guards.TryUseState(state, FlowLifetime))
        {
            return (ControlHttpResponse.Text(400, PlainTextUtf8, BadRequestBody, clear), null, DashboardRole.Viewer);
        }

        if (!string.IsNullOrEmpty(request.QueryValue("error")))
        {
            return (ControlHttpResponse.Text(401, PlainTextUtf8, "sign-in was not completed", clear), null, DashboardRole.Viewer);
        }

        var code = request.QueryValue("code");
        if (string.IsNullOrEmpty(code) || code.Length > 2048)
        {
            return (ControlHttpResponse.Text(400, PlainTextUtf8, BadRequestBody, clear), null, DashboardRole.Viewer);
        }

        OidcIdentity identity;
        try
        {
            var tokens = await _provider.ExchangeCodeAsync(code, flow.Verifier, flow.RedirectUri, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(tokens.IdToken))
            {
                return (ControlHttpResponse.Text(401, PlainTextUtf8, "unauthorized", clear), null, DashboardRole.Viewer);
            }

            identity = await _provider.ValidateIdTokenAsync(tokens.IdToken, flow.Nonce, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OidcTokenRejectedException or OidcTokenInvalidException)
        {
            return (ControlHttpResponse.Text(401, PlainTextUtf8, "unauthorized", clear), null, DashboardRole.Viewer);
        }
        catch (OidcProviderUnavailableException)
        {
            return (ControlHttpResponse.Text(503, PlainTextUtf8, "login provider unavailable",
                new KeyValuePair<string, string>("Retry-After", "5"), clear), null, DashboardRole.Viewer);
        }

        if (string.IsNullOrEmpty(identity.Subject) ||
            !_settings.Allows(identity.Email, identity.EmailVerified, identity.Groups) ||
            !TryMapRole(identity.Groups, out var role))
        {
            return (ControlHttpResponse.Text(403, PlainTextUtf8, "forbidden", clear), null, DashboardRole.Viewer);
        }

        return (ControlHttpResponse.Text(200, PlainTextUtf8, "ok", clear), identity, role);
    }

    internal bool TryMapRole(IReadOnlyCollection<string> groups, out DashboardRole role)
    {
        if (_operatorGroups.Count > 0 && groups.Any(_operatorGroups.Contains))
        {
            role = DashboardRole.Operator;
            return true;
        }

        if (_viewerGroups.Count > 0 && groups.Any(_viewerGroups.Contains))
        {
            role = DashboardRole.Viewer;
            return true;
        }

        // No group lists configured: any identity that passed allow-lists is a viewer.
        if (_operatorGroups.Count == 0 && _viewerGroups.Count == 0)
        {
            role = DashboardRole.Viewer;
            return true;
        }

        role = DashboardRole.Viewer;
        return false;
    }

    private DashboardOidcFlow? OpenFlow(ControlHttpRequest request, string cookieName)
    {
        var header = request.Header("Cookie");
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !part.AsSpan(0, eq).Equals(cookieName, StringComparison.Ordinal))
            {
                continue;
            }

            var plain = _crypto.Open(FlowPurpose, cookieName, part[(eq + 1)..]);
            if (plain is null)
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<DashboardOidcFlow>(plain);
            }
            catch (JsonException)
            {
                // Authenticated but unreadable.
            }
        }

        return null;
    }

    private static string FlowCookieName(string state) =>
        FlowCookiePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)))[..16].ToLowerInvariant();

    private static KeyValuePair<string, string> FlowCookieHeader(string name, string value, TimeSpan lifetime, bool secure)
    {
        var maxAge = Math.Max(1, (int)lifetime.TotalSeconds);
        var flags = secure ? "; Secure; SameSite=Lax" : "; SameSite=Lax";
        return new("Set-Cookie", $"{name}={value}; Path=/; HttpOnly; Max-Age={maxAge}{flags}");
    }

    private static KeyValuePair<string, string> ClearFlowCookie(string name, bool secure)
    {
        var flags = secure ? "; Secure; SameSite=Lax" : "; SameSite=Lax";
        return new("Set-Cookie", $"{name}=; Path=/; HttpOnly; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT{flags}");
    }

    private static string RandomText(int bytes) =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    private static HashSet<string> ToSet(IReadOnlyList<string>? values)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (values is null)
        {
            return set;
        }

        foreach (var value in values.Where(static v => !string.IsNullOrWhiteSpace(v)))
        {
            set.Add(value.Trim());
        }

        return set;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}
