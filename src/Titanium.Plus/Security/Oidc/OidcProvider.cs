using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Titanium.Plus.Security.Oidc;

/// <summary>The provider could not be reached or answered something unusable. Retrying later may work.</summary>
internal sealed class OidcProviderUnavailableException(string message) : Exception(message);

/// <summary>The token endpoint refused the request (a spent or wrong code, a revoked refresh token).</summary>
internal sealed class OidcTokenRejectedException(string message) : Exception(message);

/// <summary>An ID token failed validation. The message names the check, never the token.</summary>
internal sealed class OidcTokenInvalidException(string reason) : Exception(reason);

internal sealed record OidcDiscovery(
    string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri, bool TokenAuthPost);

/// <summary>What the token endpoint returned. Never logged.</summary>
internal sealed record OidcTokens(string? IdToken, string? RefreshToken, int? ExpiresInSeconds);

/// <summary>The claims of a validated ID token.</summary>
internal sealed class OidcIdentity
{
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public bool? EmailVerified { get; init; }
    public List<string> Groups { get; init; } = [];
    public Dictionary<string, string> Claims { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// One OpenID provider: discovery document, signing keys, and token endpoint. Discovery and keys are cached;
/// a failed refresh keeps the last good copy and backs off, and a key the cache does not know triggers at most
/// one key refresh per <see cref="ForcedRefreshInterval"/>.
/// </summary>
internal sealed class OidcProvider
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    internal static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const int MaxResponseBytes = 1024 * 1024;

    private static readonly string[] AllowedAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    private readonly OidcSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly SemaphoreSlim _keysGate = new(1, 1);

    private OidcDiscovery? _discovery;
    private DateTimeOffset _discoveryAt;
    private int _discoveryFailures;
    private DateTimeOffset _discoveryRetryAt;

    private JsonWebKeySet? _keys;
    private DateTimeOffset _keysAt;
    private DateTimeOffset _keysAttemptAt;
    private int _keysFailures;
    private DateTimeOffset _keysRetryAt;

    public OidcProvider(OidcSettings settings, HttpClient http, TimeProvider time, ILogger? logger)
    {
        _settings = settings;
        _http = http;
        _time = time;
        _logger = logger;
    }

    /// <summary>How many times the keys were fetched (tests).</summary>
    internal int KeyFetches { get; private set; }

    public async Task<OidcDiscovery> GetDiscoveryAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_discovery is { } fresh && now - _discoveryAt < CacheLifetime)
        {
            return fresh;
        }

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (_discovery is { } cached && now - _discoveryAt < CacheLifetime)
            {
                return cached;
            }

            if (now < _discoveryRetryAt)
            {
                return _discovery ?? throw new OidcProviderUnavailableException("discovery is backing off");
            }

            try
            {
                var document = await FetchDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                _discovery = document;
                _discoveryAt = now;
                _discoveryFailures = 0;
                return document;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _discoveryFailures++;
                _discoveryRetryAt = now + Backoff(_discoveryFailures);
                _logger?.LogWarning("Plus OIDC: policy {Policy}: discovery failed ({Kind}); retrying later.",
                    _settings.PolicyName, ex.GetType().Name);
                return _discovery ?? throw new OidcProviderUnavailableException("discovery failed");
            }
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task<OidcDiscovery> FetchDiscoveryAsync(CancellationToken cancellationToken)
    {
        var url = _settings.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        using var json = await GetJsonAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var issuer = Str(root, "issuer");
        if (!string.Equals(issuer?.TrimEnd('/'), _settings.Issuer.TrimEnd('/'), StringComparison.Ordinal))
        {
            // A document that names another issuer is a mix-up, however it got here.
            throw new OidcProviderUnavailableException("issuer mismatch");
        }

        var scheme = new Uri(_settings.Issuer).Scheme;
        var authorize = RequireEndpoint(Str(root, "authorization_endpoint"), scheme);
        var token = RequireEndpoint(Str(root, "token_endpoint"), scheme);
        var jwks = RequireEndpoint(Str(root, "jwks_uri"), scheme);
        var post = false;
        if (root.TryGetProperty("token_endpoint_auth_methods_supported", out var methods) &&
            methods.ValueKind == JsonValueKind.Array)
        {
            var values = methods.EnumerateArray().Select(static m => m.GetString()).ToArray();
            post = values.Contains("client_secret_post", StringComparer.Ordinal) &&
                   !values.Contains("client_secret_basic", StringComparer.Ordinal);
        }

        return new OidcDiscovery(_settings.Issuer, authorize, token, jwks, post);
    }

    private static string RequireEndpoint(string? value, string scheme)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != scheme ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OidcProviderUnavailableException("endpoint is missing or not on the issuer scheme");
        }

        return value!;
    }

    /// <summary>
    /// Validates signature, issuer, audience (and <c>azp</c> for several audiences), lifetime with a 60 second skew,
    /// and the nonce when one is expected.
    /// </summary>
    public async Task<OidcIdentity> ValidateIdTokenAsync(string idToken, string? expectedNonce, CancellationToken cancellationToken)
    {
        JsonWebToken parsed;
        try
        {
            parsed = new JsonWebToken(idToken);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            throw new OidcTokenInvalidException("malformed");
        }

        var keys = await GetKeysAsync(parsed.Kid, cancellationToken).ConfigureAwait(false);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _settings.Issuer,
            ValidateAudience = true,
            ValidAudience = _settings.ClientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = ClockSkew,
            LifetimeValidator = LifetimeIsValid,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidAlgorithms = AllowedAlgorithms,
        };

        var result = await _handler.ValidateTokenAsync(idToken, parameters).ConfigureAwait(false);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            throw new OidcTokenInvalidException(Reason(result.Exception));
        }

        var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        using (payload)
        {
            var root = payload.RootElement;
            if (token.Audiences.Count() > 1 && !string.Equals(Str(root, "azp"), _settings.ClientId, StringComparison.Ordinal))
            {
                throw new OidcTokenInvalidException("azp");
            }

            if (expectedNonce is not null && !FixedEquals(Str(root, "nonce"), expectedNonce))
            {
                throw new OidcTokenInvalidException("nonce");
            }

            return ReadIdentity(root, token);
        }
    }

    private OidcIdentity ReadIdentity(JsonElement root, JsonWebToken token)
    {
        var groups = new List<string>();
        if (OidcClaimReader.Find(root, _settings.GroupsClaim) is { } groupClaim)
        {
            OidcClaimReader.AddAll(groupClaim, groups);
        }

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _settings.PassClaims)
        {
            if (OidcClaimReader.Find(root, pair.Key) is { } claim && OidcClaimReader.Flatten(claim) is { Length: > 0 } text)
            {
                claims[pair.Key] = text;
            }
        }

        bool? verified = null;
        if (root.TryGetProperty("email_verified", out var ev))
        {
            verified = ev.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => string.Equals(ev.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _ => null,
            };
        }

        return new OidcIdentity
        {
            Subject = Str(root, "sub"),
            Email = Str(root, "email")?.Trim().ToLowerInvariant(),
            EmailVerified = verified,
            Groups = groups,
            Claims = claims,
            ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(token.ValidTo, DateTimeKind.Utc)),
        };
    }

    private bool LifetimeIsValid(
        DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        if (expires is null)
        {
            return false;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        return DateTime.SpecifyKind(expires.Value, DateTimeKind.Utc) + ClockSkew > now &&
               (notBefore is null || DateTime.SpecifyKind(notBefore.Value, DateTimeKind.Utc) - ClockSkew <= now);
    }

    private static string Reason(Exception? ex) => ex switch
    {
        SecurityTokenExpiredException => "expired",
        SecurityTokenNotYetValidException => "not yet valid",
        SecurityTokenInvalidAudienceException => "audience",
        SecurityTokenInvalidIssuerException => "issuer",
        SecurityTokenSignatureKeyNotFoundException => "signing key",
        SecurityTokenInvalidSignatureException => "signature",
        SecurityTokenInvalidAlgorithmException => "algorithm",
        SecurityTokenInvalidLifetimeException => "lifetime",
        _ => "invalid",
    };

    private async Task<JsonWebKeySet> GetKeysAsync(string? kid, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var cached = _keys;
        if (cached is not null && now - _keysAt < CacheLifetime && (kid is null || Has(cached, kid)))
        {
            return cached;
        }

        await _keysGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            cached = _keys;
            var fresh = cached is not null && now - _keysAt < CacheLifetime;
            if (cached is not null && fresh && (kid is null || Has(cached, kid)))
            {
                return cached;
            }

            // An unknown key id may be a rotation, or a forged token. Either way the provider is asked at most
            // once per interval, and never while a failed fetch is backing off.
            if (cached is not null && fresh && now - _keysAttemptAt < ForcedRefreshInterval)
            {
                return cached;
            }

            if (now < _keysRetryAt)
            {
                return cached ?? throw new OidcProviderUnavailableException("keys are backing off");
            }

            _keysAttemptAt = now;
            try
            {
                var discovery = await GetDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                using var jwks = await GetJsonAsync(new Uri(discovery.JwksUri), cancellationToken).ConfigureAwait(false);
                var set = new JsonWebKeySet(jwks.RootElement.GetRawText());
                _keys = set;
                _keysAt = now;
                _keysFailures = 0;
                KeyFetches++;
                return set;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _keysFailures++;
                _keysRetryAt = now + Backoff(_keysFailures);
                _logger?.LogWarning("Plus OIDC: policy {Policy}: signing keys unavailable ({Kind}); retrying later.",
                    _settings.PolicyName, ex.GetType().Name);
                return cached ?? throw new OidcProviderUnavailableException("keys unavailable");
            }
        }
        finally
        {
            _keysGate.Release();
        }
    }

    private static bool Has(JsonWebKeySet set, string kid)
    {
        foreach (var key in set.Keys)
        {
            if (string.Equals(key.Kid, kid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Exchanges an authorization code. The PKCE verifier proves this is the client that started the login.</summary>
    public async Task<OidcTokens> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", verifier),
        };
        return await PostTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OidcTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
        };
        return await PostTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OidcTokens> PostTokenAsync(List<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        var discovery = await GetDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_settings.ClientSecret is { Length: > 0 } secret)
        {
            if (discovery.TokenAuthPost)
            {
                form.Add(new("client_id", _settings.ClientId));
                form.Add(new("client_secret", secret));
            }
            else
            {
                // RFC 6749 section 2.3.1: the id and secret are form-encoded before they are joined.
                var credentials = Uri.EscapeDataString(_settings.ClientId) + ":" + Uri.EscapeDataString(secret);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
            }
        }
        else
        {
            form.Add(new("client_id", _settings.ClientId));
        }

        request.Content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new OidcProviderUnavailableException("token endpoint unreachable");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status >= 500)
            {
                throw new OidcProviderUnavailableException("token endpoint error");
            }

            JsonDocument document;
            try
            {
                document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw status is >= 200 and < 300
                    ? new OidcProviderUnavailableException("token endpoint answer unreadable")
                    : new OidcTokenRejectedException("rejected");
            }

            using (document)
            {
                if (status is < 200 or >= 300)
                {
                    throw new OidcTokenRejectedException(Str(document.RootElement, "error") ?? "rejected");
                }

                var root = document.RootElement;
                int? expires = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number &&
                               e.TryGetInt32(out var seconds)
                    ? seconds
                    : null;
                return new OidcTokens(Str(root, "id_token"), Str(root, "refresh_token"), expires);
            }
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OidcProviderUnavailableException("provider answered " + (int)response.StatusCode);
        }

        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes)
                {
                    throw new InvalidOperationException("response too large");
                }
            }

            return JsonDocument.Parse(buffer.ToArray());
        }
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 10))));

    private static string? Str(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Equal-time compare, whatever the lengths: both sides are hashed first.</summary>
    internal static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    }
}

/// <summary>Reads claims out of a validated ID token payload. A dotted name walks into nested objects.</summary>
internal static class OidcClaimReader
{
    private const int MaxClaimChars = 1024;

    public static JsonElement? Find(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty(name, out var direct))
        {
            return direct;
        }

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var part in name.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    public static void AddAll(JsonElement claim, List<string> into)
    {
        switch (claim.ValueKind)
        {
            case JsonValueKind.String when claim.GetString() is { Length: > 0 } single:
                into.Add(single);
                break;
            case JsonValueKind.Array:
                foreach (var item in claim.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    {
                        into.Add(text);
                    }
                }

                break;
        }
    }

    /// <summary>A header-safe text for a claim: arrays join with commas, control characters go, length is capped.</summary>
    public static string? Flatten(JsonElement claim)
    {
        string? text;
        switch (claim.ValueKind)
        {
            case JsonValueKind.String:
                text = claim.GetString();
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                text = claim.GetRawText();
                break;
            case JsonValueKind.Array:
                var items = new List<string>();
                AddAll(claim, items);
                text = string.Join(",", items);
                break;
            default:
                return null;
        }

        return text is null ? null : Sanitize(text);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxClaimChars));
        foreach (var c in value)
        {
            if (builder.Length >= MaxClaimChars)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Trim();
    }
}
