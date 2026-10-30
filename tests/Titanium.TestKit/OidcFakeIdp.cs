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

namespace Titanium.TestKit;
public sealed class OidcClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

public sealed record TokenCall(string GrantType, string? Authorization, IReadOnlyDictionary<string, string> Form);

public sealed record AuthorizeCall(IReadOnlyDictionary<string, string> Query);

/// <summary>
/// A small identity provider: discovery, authorize, token, and JWKS endpoints, with switches for each way a
/// provider can misbehave. Tokens are signed with RSA keys it can rotate.
/// </summary>
public sealed class FakeIdp : IAsyncDisposable
{
    private static readonly string[] ResponseTypesSupported = ["code"];
    private static readonly string[] TokenEndpointAuthMethodsSupported = ["client_secret_basic"];
    private static readonly string[] CodeChallengeMethodsSupported = ["S256"];

    public const string ClientId = "twp-client";
    public const string ClientSecret = "client-secret-value";

    private sealed record Key(string Kid, RSA Rsa);

    private sealed record Pending(string Challenge, string Nonce, string RedirectUri);

    private readonly IWebHost _host;
    private readonly OidcClock _clock;
    private readonly object _gate = new();
    private readonly List<Key> _published = [];
    private readonly List<Key> _revoked = [];
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

    /// <summary>JWKS alone returns 503 while discovery and token keep working. Proves key refresh fails closed.</summary>
    public bool JwksDown { get; set; }

    public bool DenyAtAuthorize { get; set; }

    /// <summary>When set, the next tokens are signed with a key the JWKS does not list.</summary>
    public bool SignWithUnpublishedKey { get; set; }

    /// <summary>When set, the next tokens are signed with a key that used to be in JWKS and was revoked.</summary>
    public bool SignWithRevokedKey { get; set; }

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

    /// <summary>
    /// Drops every published key except the active one from JWKS. Tokens signed with a removed key must
    /// fail once the proxy refreshes. Revoked keys stay available for <see cref="SignWithRevokedKey"/>.
    /// </summary>
    public void RevokeInactiveKeys()
    {
        lock (_gate)
        {
            for (var i = _published.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_published[i], _active))
                {
                    _revoked.Add(_published[i]);
                    _published.RemoveAt(i);
                }
            }
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

        foreach (var key in _revoked)
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
                    ["response_types_supported"] = ResponseTypesSupported,
                    ["token_endpoint_auth_methods_supported"] = TokenEndpointAuthMethodsSupported,
                    ["code_challenge_methods_supported"] = CodeChallengeMethodsSupported,
                });
                break;
            case "/jwks":
                Interlocked.Increment(ref _jwksHits);
                if (JwksDown)
                {
                    context.Response.StatusCode = 503;
                    break;
                }

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
            if (SignWithRevokedKey)
            {
                if (_revoked.Count == 0)
                {
                    throw new InvalidOperationException("RevokeInactiveKeys before SignWithRevokedKey.");
                }

                key = _revoked[^1];
            }
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
