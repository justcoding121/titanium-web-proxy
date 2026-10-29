using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Oidc;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Plus.Tests;

/// <summary>A clock the tests move by hand. The identity provider and the policy share one.</summary>
internal sealed class OidcClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

internal sealed record TokenCall(string GrantType, string? Authorization, IReadOnlyDictionary<string, string> Form);

internal sealed record AuthorizeCall(IReadOnlyDictionary<string, string> Query);

/// <summary>
/// A small identity provider: discovery, authorize, token, and JWKS endpoints, with switches for each way a
/// provider can misbehave. Tokens are signed with RSA keys it can rotate.
/// </summary>
internal sealed class FakeIdp : IAsyncDisposable
{
    public const string ClientId = "twp-client";
    public const string ClientSecret = "client-secret-value";

    private sealed record Key(string Kid, RSA Rsa);

    private sealed record Pending(string Challenge, string Nonce, string RedirectUri);

    private readonly IWebHost _host;
    private readonly OidcClock _clock;
    private readonly object _gate = new();
    private readonly List<Key> _published = [];
    private readonly Dictionary<string, Pending> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
    private Key _active;
    private int _keyCounter;
    private int _refreshCalls;
    private int _discoveryHits;
    private int _jwksHits;

    private FakeIdp(OidcClock clock)
    {
        _clock = clock;
        _active = AddKey();
        _host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app => app.Run(HandleAsync))
            .Build();
    }

    public string Issuer { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public string RegisteredRedirectUri { get; set; } = "http://matrix.test/_titanium/oidc/callback";

    public string Subject { get; set; } = "user-1";

    public string? Email { get; set; } = "alice@example.test";

    public bool EmailVerified { get; set; } = true;

    public string[] Groups { get; set; } = ["users"];

    public string GroupsClaimName { get; set; } = "groups";

    public string? AudienceOverride { get; set; }

    public string? IssuerOverride { get; set; }

    public string? NonceOverride { get; set; }

    public TimeSpan IdTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    public bool IssueRefreshToken { get; set; } = true;

    public bool RotateRefreshToken { get; set; }

    public bool RejectRefresh { get; set; }

    public TimeSpan RefreshDelay { get; set; } = TimeSpan.Zero;

    public bool CorruptPkce { get; set; }

    public bool Down { get; set; }

    public bool DenyAtAuthorize { get; set; }

    /// <summary>When set, the next tokens are signed with a key the JWKS does not list.</summary>
    public bool SignWithUnpublishedKey { get; set; }

    public int RefreshCalls => Volatile.Read(ref _refreshCalls);

    public int DiscoveryHits => Volatile.Read(ref _discoveryHits);

    public int JwksHits => Volatile.Read(ref _jwksHits);

    public ConcurrentQueue<TokenCall> TokenCalls { get; } = new();

    public ConcurrentQueue<AuthorizeCall> AuthorizeCalls { get; } = new();

    public static async Task<FakeIdp> StartAsync(OidcClock clock)
    {
        var idp = new FakeIdp(clock);
        await idp._host.StartAsync();
        var addresses = idp._host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses;
        idp.Port = new Uri(addresses.First()).Port;
        idp.Issuer = $"http://127.0.0.1:{idp.Port}";
        return idp;
    }

    /// <summary>A new signing key becomes the active one and joins the JWKS. The old keys stay listed.</summary>
    public void RotateKeys()
    {
        lock (_gate)
        {
            _active = AddKey();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        foreach (var key in _published)
        {
            key.Rsa.Dispose();
        }
    }

    private Key AddKey()
    {
        var key = new Key("k" + Interlocked.Increment(ref _keyCounter), RSA.Create(2048));
        lock (_gate)
        {
            _published.Add(key);
        }

        return key;
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (Down)
        {
            context.Response.StatusCode = 503;
            return;
        }

        switch (context.Request.Path.Value)
        {
            case "/.well-known/openid-configuration":
                Interlocked.Increment(ref _discoveryHits);
                await WriteJsonAsync(context, new Dictionary<string, object>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = Issuer + "/authorize",
                    ["token_endpoint"] = Issuer + "/token",
                    ["jwks_uri"] = Issuer + "/jwks",
                    ["response_types_supported"] = new[] { "code" },
                    ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic" },
                    ["code_challenge_methods_supported"] = new[] { "S256" },
                });
                break;
            case "/jwks":
                Interlocked.Increment(ref _jwksHits);
                await WriteJsonAsync(context, new Dictionary<string, object> { ["keys"] = PublishedJwks() });
                break;
            case "/authorize":
                Authorize(context);
                break;
            case "/token":
                await TokenAsync(context);
                break;
            default:
                context.Response.StatusCode = 404;
                break;
        }
    }

    private List<object> PublishedJwks()
    {
        lock (_gate)
        {
            return _published.Select(static k =>
            {
                var p = k.Rsa.ExportParameters(false);
                return (object)new Dictionary<string, string>
                {
                    ["kty"] = "RSA",
                    ["use"] = "sig",
                    ["alg"] = "RS256",
                    ["kid"] = k.Kid,
                    ["n"] = Base64UrlEncoder.Encode(p.Modulus!),
                    ["e"] = Base64UrlEncoder.Encode(p.Exponent!),
                };
            }).ToList();
        }
    }

    private void Authorize(HttpContext context)
    {
        var query = context.Request.Query.ToDictionary(static p => p.Key, static p => p.Value.ToString(), StringComparer.Ordinal);
        AuthorizeCalls.Enqueue(new AuthorizeCall(query));
        string Get(string name) => query.GetValueOrDefault(name) ?? string.Empty;

        if (Get("response_type") != "code" || Get("client_id") != ClientId || Get("redirect_uri") != RegisteredRedirectUri ||
            Get("code_challenge_method") != "S256" || Get("code_challenge").Length == 0 || Get("state").Length == 0 ||
            Get("nonce").Length == 0)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var target = RegisteredRedirectUri;
        if (DenyAtAuthorize)
        {
            context.Response.Redirect($"{target}?error=access_denied&state={Uri.EscapeDataString(Get("state"))}");
            return;
        }

        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(18));
        lock (_gate)
        {
            _codes[code] = new Pending((CorruptPkce ? "x" : string.Empty) + Get("code_challenge"), Get("nonce"), Get("redirect_uri"));
        }

        context.Response.Redirect($"{target}?code={code}&state={Uri.EscapeDataString(Get("state"))}");
    }

    private async Task TokenAsync(HttpContext context)
    {
        var form = (await context.Request.ReadFormAsync()).ToDictionary(static p => p.Key, static p => p.Value.ToString(), StringComparer.Ordinal);
        var authorization = context.Request.Headers.Authorization.ToString();
        var grant = form.GetValueOrDefault("grant_type") ?? string.Empty;
        TokenCalls.Enqueue(new TokenCall(grant, authorization.Length == 0 ? null : authorization, form));

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Uri.EscapeDataString(ClientId) + ":" + Uri.EscapeDataString(ClientSecret)));
        if (!string.Equals(authorization, expected, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            await WriteJsonAsync(context, new Dictionary<string, object> { ["error"] = "invalid_client" });
            return;
        }

        if (grant == "authorization_code")
        {
            await CodeGrantAsync(context, form);
        }
        else if (grant == "refresh_token")
        {
            await RefreshGrantAsync(context, form);
        }
        else
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new Dictionary<string, object> { ["error"] = "unsupported_grant_type" });
        }
    }

    private async Task CodeGrantAsync(HttpContext context, Dictionary<string, string> form)
    {
        Pending? pending;
        lock (_gate)
        {
            _codes.Remove(form.GetValueOrDefault("code") ?? string.Empty, out pending);
        }

        var verifier = form.GetValueOrDefault("code_verifier") ?? string.Empty;
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        if (pending is null || pending.RedirectUri != form.GetValueOrDefault("redirect_uri") || pending.Challenge != challenge)
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new Dictionary<string, object> { ["error"] = "invalid_grant" });
            return;
        }

        var answer = new Dictionary<string, object>
        {
            ["access_token"] = "at-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(12)),
            ["token_type"] = "Bearer",
            ["expires_in"] = 600,
            ["id_token"] = IdToken(NonceOverride ?? pending.Nonce),
        };
        if (IssueRefreshToken)
        {
            answer["refresh_token"] = NewRefreshToken(Subject);
        }

        await WriteJsonAsync(context, answer);
    }

    private async Task RefreshGrantAsync(HttpContext context, Dictionary<string, string> form)
    {
        Interlocked.Increment(ref _refreshCalls);
        if (RefreshDelay > TimeSpan.Zero)
        {
            await Task.Delay(RefreshDelay);
        }

        var presented = form.GetValueOrDefault("refresh_token") ?? string.Empty;
        string? subject;
        lock (_gate)
        {
            if (RotateRefreshToken)
            {
                _refreshTokens.Remove(presented, out subject);
            }
            else
            {
                _refreshTokens.TryGetValue(presented, out subject);
            }
        }

        if (RejectRefresh || subject is null)
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new Dictionary<string, object> { ["error"] = "invalid_grant" });
            return;
        }

        var answer = new Dictionary<string, object>
        {
            ["access_token"] = "at-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(12)),
            ["token_type"] = "Bearer",
            ["expires_in"] = 600,
            ["id_token"] = IdToken(nonce: null),
            ["refresh_token"] = RotateRefreshToken ? NewRefreshToken(subject) : presented,
        };
        await WriteJsonAsync(context, answer);
    }

    private string NewRefreshToken(string subject)
    {
        var token = "rt-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(18));
        lock (_gate)
        {
            _refreshTokens[token] = subject;
        }

        return token;
    }

    /// <summary>Signs an ID token for the current settings. Public so a test can hand-make a callback answer.</summary>
    public string IdToken(string? nonce)
    {
        var now = _clock.GetUtcNow();
        var expires = now + IdTokenLifetime;
        var notBefore = expires < now ? expires - TimeSpan.FromMinutes(1) : now;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            [GroupsClaimName] = Groups,
            ["name"] = "Alice Example",
        };
        if (Email is not null)
        {
            claims["email"] = Email;
            claims["email_verified"] = EmailVerified;
        }

        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        Key key;
        RSA? stray = null;
        lock (_gate)
        {
            key = _active;
        }

        if (SignWithUnpublishedKey)
        {
            stray = RSA.Create(2048);
            key = new Key("k-unpublished", stray);
        }

        try
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = IssuerOverride ?? Issuer,
                Audience = AudienceOverride ?? ClientId,
                Claims = claims,
                IssuedAt = notBefore.UtcDateTime,
                NotBefore = notBefore.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(key.Rsa) { KeyId = key.Kid }, SecurityAlgorithms.RsaSha256),
            };
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }
        finally
        {
            stray?.Dispose();
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, object body)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}

/// <summary>A protected application: records what the proxy forwards, and sets a cookie of its own.</summary>
internal sealed class OidcOrigin : IAsyncDisposable
{
    private readonly IWebHost _host;
    private int _hits;

    public OidcOrigin()
    {
        _host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app => app.Run(async context =>
            {
                Interlocked.Increment(ref _hits);
                var headers = context.Request.Headers.ToDictionary(
                    static h => h.Key, static h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue(new OriginRequest(context.Request.Method, context.Request.Path + context.Request.QueryString, headers));
                context.Response.Headers.Append("Set-Cookie", "app=1; Path=/");
                await context.Response.WriteAsync("origin-ok");
            }))
            .Build();
    }

    public int Port { get; private set; }

    public int Hits => Volatile.Read(ref _hits);

    public ConcurrentQueue<OriginRequest> Requests { get; } = new();

    public OriginRequest Last => Requests.Last();

    public async Task StartAsync()
    {
        await _host.StartAsync();
        Port = new Uri(_host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

internal sealed record OriginRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What a test may change in the policy. Defaults are a working login with claim headers.</summary>
internal sealed class OidcPolicyOptions
{
    public string? RedirectPath { get; set; }

    public string? PublicBaseUrl { get; set; }

    public string CookieKeys { get; set; } = Convert.ToHexString(Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray());

    public string? CookieName { get; set; }

    public string[]? AllowedEmails { get; set; }

    public string[]? AllowedEmailDomains { get; set; }

    public string[]? AllowedGroups { get; set; }

    public string? GroupsClaim { get; set; }

    public string[]? Allowlist { get; set; }

    public bool Refresh { get; set; }

    public int? SessionTtlSeconds { get; set; }

    public bool MissingClientSecret { get; set; }

    public RoutePolicyConfig Build(string issuer) => new()
    {
        Name = "login",
        Kind = "oidc",
        Issuer = issuer,
        ClientId = FakeIdp.ClientId,
        ClientSecretEnv = "OIDC_CLIENT_SECRET",
        CookieSecretEnv = "OIDC_COOKIE_KEY",
        RedirectPath = RedirectPath,
        PublicBaseUrl = PublicBaseUrl,
        CookieName = CookieName,
        LogoutPath = "/logout",
        SessionTtlSeconds = SessionTtlSeconds,
        AllowedEmails = AllowedEmails,
        AllowedEmailDomains = AllowedEmailDomains,
        AllowedGroups = AllowedGroups,
        GroupsClaim = GroupsClaim,
        PostLoginRedirectAllowlist = Allowlist,
        RefreshTokens = Refresh ? true : null,
        PassClaims = new Dictionary<string, string>
        {
            ["email"] = "X-User-Email",
            ["sub"] = "X-User-Id",
            ["name"] = "X-User-Name",
        },
    };
}

/// <summary>An identity provider, a protected origin, and a proxy with the <c>oidc</c> policy in front.</summary>
internal sealed class OidcHarness : IAsyncDisposable
{
    public const string Host = "matrix.test";

    private ProxyServer? _proxy;

    private OidcHarness(OidcClock clock, FakeIdp idp, OidcOrigin origin, OidcPolicyHandler? handler, OidcPolicyOptions options)
    {
        Clock = clock;
        Idp = idp;
        Origin = origin;
        Handler = handler;
        Options = options;
    }

    public OidcClock Clock { get; }

    public FakeIdp Idp { get; }

    public OidcOrigin Origin { get; }

    public OidcPolicyHandler? Handler { get; }

    public OidcPolicyOptions Options { get; }

    public RoutePolicyConfig Policy { get; private set; } = null!;

    public int ProxyPort => _proxy!.ProxyEndPoints[0].Port;

    public static async Task<OidcHarness> StartAsync(Action<OidcPolicyOptions>? configure = null, bool registerHandler = true)
    {
        var clock = new OidcClock();
        var options = new OidcPolicyOptions();
        configure?.Invoke(options);
        var idp = await FakeIdp.StartAsync(clock);
        var origin = new OidcOrigin();
        await origin.StartAsync();

        OidcPolicyHandler? handler = null;
        if (registerHandler)
        {
            handler = new OidcPolicyHandler(logger: null, time: clock)
            {
                ReadEnvironment = name => name switch
                {
                    "OIDC_CLIENT_SECRET" => options.MissingClientSecret ? null : FakeIdp.ClientSecret,
                    "OIDC_COOKIE_KEY" => options.CookieKeys,
                    _ => null,
                },
            };
        }

        var harness = new OidcHarness(clock, idp, origin, handler, options);
        await harness.StartProxyAsync();
        return harness;
    }

    private async Task StartProxyAsync()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = Origin.Port }],
            },
        ]);

        Policy = Options.Build(Idp.Issuer);
        _proxy = new ProxyServer(false, false, false);
        _proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Policies = ["login"],
                },
            ],
            Policies = [Policy],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            PolicyHandlers = Handler is null ? null : [Handler],
        };
        _proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        _proxy.Start();
    }

    public Browser NewBrowser() => new(ProxyPort);

    /// <summary>Asks for a page with no session: the proxy answers with the sign-in redirect.</summary>
    public async Task<string> StartLoginAsync(Browser browser, string path = "/app?x=1")
    {
        using var response = await browser.GetAsync($"http://{Host}{path}", ("Accept", "text/html"));
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>Follows the authorize redirect the way a browser would and returns where the provider sends it next.</summary>
    public async Task<string> VisitProviderAsync(Browser browser, string authorizeUrl)
    {
        using var response = await browser.GetDirectAsync(authorizeUrl);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode, "the provider refused the authorize request");
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>The whole sign-in. Returns the callback response (a redirect back to the page when it works).</summary>
    public async Task<HttpResponseMessage> SignInAsync(Browser browser, string path = "/app?x=1")
    {
        var authorize = await StartLoginAsync(browser, path);
        var callback = await VisitProviderAsync(browser, authorize);
        return await browser.GetAsync(callback, ("Accept", "text/html"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null)
        {
            if (_proxy.ProxyRunning)
            {
                await _proxy.StopAsync();
            }

            _proxy.Dispose();
        }

        await Origin.DisposeAsync();
        await Idp.DisposeAsync();
    }
}

/// <summary>A client with a cookie jar that talks to the proxy as an explicit proxy, and to the provider directly.</summary>
internal sealed class Browser : IDisposable
{
    private readonly HttpClient _viaProxy;
    private readonly HttpClient _direct;

    public Browser(int proxyPort)
    {
        _viaProxy = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _direct = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    /// <summary>The raw Set-Cookie lines of the last response from the proxy.</summary>
    public List<string> LastSetCookies { get; private set; } = [];

    public Task<HttpResponseMessage> GetAsync(string url, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Get, url, headers);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(static c => c.Key + "=" + c.Value)));
        }

        var response = await _viaProxy.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var lines) ? lines.ToList() : [];
        foreach (var line in LastSetCookies)
        {
            Store(line);
        }

        return response;
    }

    /// <summary>A request to the provider: no proxy, no cookies.</summary>
    public Task<HttpResponseMessage> GetDirectAsync(string url) => _direct.GetAsync(url);

    public void Dispose()
    {
        _viaProxy.Dispose();
        _direct.Dispose();
    }

    private void Store(string line)
    {
        var parts = line.Split(';', StringSplitOptions.TrimEntries);
        var pair = parts[0];
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            return;
        }

        var name = pair[..eq];
        var value = pair[(eq + 1)..];
        var expired = value.Length == 0 || parts.Skip(1).Any(static p => p.Equals("Max-Age=0", StringComparison.OrdinalIgnoreCase));
        if (expired)
        {
            Cookies.Remove(name);
        }
        else
        {
            Cookies[name] = value;
        }
    }

    /// <summary>The cookie whose name starts with <paramref name="prefix"/> (the flow cookie has a derived name).</summary>
    public KeyValuePair<string, string>? CookieStartingWith(string prefix) =>
        Cookies.FirstOrDefault(c => c.Key.StartsWith(prefix, StringComparison.Ordinal)) is { Key: not null } found ? found : null;
}
