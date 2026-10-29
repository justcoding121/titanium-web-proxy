using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// <c>jwt.verify(token [, {iss = ..., aud = ...}])</c> against the JWKS in the plugin's <c>settings.jwks</c>. Supports
/// RS256/384/512, PS256/384/512, ES256/384/512, and HS256/384/512 (an <c>oct</c> key). <c>none</c> is always refused.
/// <c>exp</c> and <c>nbf</c> are checked with 60 seconds of skew. Returns the claims, or <c>nil, err</c>.
/// </summary>
internal sealed class LuaJwt
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly List<JsonElement> _keys = [];
    private readonly string? _error;

    private LuaJwt(string? error) => _error = error;

    public static LuaJwt FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(LuaOptions.JwksSetting, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return new LuaJwt($"jwt.verify: settings.{LuaOptions.JwksSetting} is not set");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var jwt = new LuaJwt(null);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in keys.EnumerateArray())
                {
                    jwt._keys.Add(key.Clone());
                }
            }

            return jwt._keys.Count > 0 ? jwt : new LuaJwt($"jwt.verify: settings.{LuaOptions.JwksSetting} has no keys");
        }
        catch (JsonException ex)
        {
            return new LuaJwt($"jwt.verify: settings.{LuaOptions.JwksSetting} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>The load-time problem with the JWKS, if any.</summary>
    public string? Error => _error;

    public byte[] Verify(LuaTable op) => Verify(op, DateTimeOffset.UtcNow);

    internal byte[] Verify(LuaTable op, DateTimeOffset now)
    {
        if (_error is not null)
        {
            return LuaRuntime.Values(null, _error);
        }

        var token = op.Str("token") ?? string.Empty;
        var parts = token.Split('.');
        if (parts.Length != 3 || !TryDecode(parts[0], out var headerBytes) || !TryDecode(parts[1], out var payloadBytes) ||
            !TryDecode(parts[2], out var signature))
        {
            return LuaRuntime.Values(null, "jwt.verify: malformed token");
        }

        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            using var payload = JsonDocument.Parse(payloadBytes);
            if (header.RootElement.ValueKind != JsonValueKind.Object || payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                return LuaRuntime.Values(null, "jwt.verify: malformed token");
            }

            var alg = Text(header.RootElement, "alg");
            var kid = Text(header.RootElement, "kid");
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!IsSupported(alg))
            {
                return LuaRuntime.Values(null, $"jwt.verify: unsupported alg '{alg}'");
            }

            var verified = false;
            foreach (var key in _keys)
            {
                if ((kid is not null && Text(key, "kid") != kid) || (Text(key, "alg") is { } keyAlg && keyAlg != alg))
                {
                    continue;
                }

                if (VerifyWith(key, alg!, signed, signature))
                {
                    verified = true;
                    break;
                }
            }

            if (!verified)
            {
                return LuaRuntime.Values(null, "jwt.verify: signature does not match any key");
            }

            var claims = payload.RootElement;
            var seconds = now.ToUnixTimeSeconds();
            if (Number(claims, "exp") is { } exp && seconds - Skew.TotalSeconds >= exp)
            {
                return LuaRuntime.Values(null, "jwt.verify: token expired");
            }

            if (Number(claims, "nbf") is { } nbf && seconds + Skew.TotalSeconds < nbf)
            {
                return LuaRuntime.Values(null, "jwt.verify: token not yet valid");
            }

            if (op.Str("iss") is { } iss && Text(claims, "iss") != iss)
            {
                return LuaRuntime.Values(null, "jwt.verify: issuer does not match");
            }

            if (op.Str("aud") is { } aud && !HasAudience(claims, aud))
            {
                return LuaRuntime.Values(null, "jwt.verify: audience does not match");
            }

            return LuaRuntime.Values(ToLua(claims, 0));
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException or ArgumentException)
        {
            return LuaRuntime.Values(null, "jwt.verify: " + ex.Message);
        }
    }

    private static bool IsSupported(string? alg) =>
        alg is "RS256" or "RS384" or "RS512" or "PS256" or "PS384" or "PS512" or "ES256" or "ES384" or "ES512" or "HS256" or "HS384" or "HS512";

    private static HashAlgorithmName Hash(string alg) => alg[2..] switch
    {
        "256" => HashAlgorithmName.SHA256,
        "384" => HashAlgorithmName.SHA384,
        _ => HashAlgorithmName.SHA512,
    };

    private static bool VerifyWith(JsonElement key, string alg, byte[] signed, byte[] signature)
    {
        var kty = Text(key, "kty");
        var hash = Hash(alg);
        switch (alg[0])
        {
            case 'R' or 'P' when kty == "RSA":
            {
                if (!TryBytes(key, "n", out var n) || !TryBytes(key, "e", out var e))
                {
                    return false;
                }

                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
                return rsa.VerifyData(signed, signature, hash, alg[0] == 'R' ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);
            }
            case 'E' when kty == "EC":
            {
                var curve = (alg, Text(key, "crv")) switch
                {
                    ("ES256", "P-256") => ECCurve.NamedCurves.nistP256,
                    ("ES384", "P-384") => ECCurve.NamedCurves.nistP384,
                    ("ES512", "P-521") => ECCurve.NamedCurves.nistP521,
                    _ => (ECCurve?)null,
                };
                if (curve is null || !TryBytes(key, "x", out var x) || !TryBytes(key, "y", out var y))
                {
                    return false;
                }

                using var ecdsa = ECDsa.Create(new ECParameters { Curve = curve.Value, Q = new ECPoint { X = x, Y = y } });
                return ecdsa.VerifyData(signed, signature, hash);
            }
            case 'H' when kty == "oct":
            {
                if (!TryBytes(key, "k", out var secret) || secret.Length == 0)
                {
                    return false;
                }

                var expected = alg switch
                {
                    "HS256" => HMACSHA256.HashData(secret, signed),
                    "HS384" => HMACSHA384.HashData(secret, signed),
                    _ => HMACSHA512.HashData(secret, signed),
                };
                return CryptographicOperations.FixedTimeEquals(expected, signature);
            }
            default:
                return false;
        }
    }

    private static bool HasAudience(JsonElement claims, string audience)
    {
        if (!claims.TryGetProperty("aud", out var aud))
        {
            return false;
        }

        return aud.ValueKind switch
        {
            JsonValueKind.String => aud.GetString() == audience,
            JsonValueKind.Array => aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == audience),
            _ => false,
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool TryBytes(JsonElement element, string name, out byte[] bytes)
    {
        bytes = [];
        return Text(element, name) is { } text && TryDecode(text, out bytes);
    }

    internal static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length > 64 * 1024)
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(text.Length)];
        if (!Base64Url.TryDecodeFromChars(text, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }

    /// <summary>JSON as Lua values: objects and arrays become tables; <c>null</c> becomes nil.</summary>
    internal static object? ToLua(JsonElement element, int depth)
    {
        if (depth > 30)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var table = new LuaTable();
                foreach (var property in element.EnumerateObject())
                {
                    table.Fields[property.Name] = ToLua(property.Value, depth + 1);
                }

                return table;
            }
            case JsonValueKind.Array:
            {
                var table = new LuaTable();
                foreach (var item in element.EnumerateArray())
                {
                    table.Array.Add(ToLua(item, depth + 1));
                }

                return table;
            }
            case JsonValueKind.String:
                return Encoding.UTF8.GetBytes(element.GetString()!);
            case JsonValueKind.Number:
                return element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}
