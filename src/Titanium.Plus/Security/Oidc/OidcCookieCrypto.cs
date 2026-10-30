using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Titanium.Plus.Security.Oidc;

/// <summary>
/// Seals cookie payloads with AES-256-GCM. The first key encrypts; every key decrypts, so a rotation keeps
/// sessions alive. Each purpose (session, login flow) uses its own key derived with HKDF, and the cookie name
/// and purpose are authenticated data, so one cookie cannot stand in for another.
/// </summary>
internal sealed class OidcCookieCrypto
{
    internal const int KeyBytes = 32;
    internal const int MaxKeys = 4;
    private const byte Version = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[][] _masterKeys;

    public OidcCookieCrypto(IReadOnlyList<byte[]> keys)
    {
        if (keys.Count is 0 or > MaxKeys)
        {
            throw new OidcConfigException($"cookie secret needs 1 to {MaxKeys} keys.");
        }

        _masterKeys = keys.Select(static k => (byte[])k.Clone()).ToArray();
    }

    /// <summary>
    /// Parses keys separated by commas, semicolons, or white space. Each key is 64 hex digits or base64 (standard or
    /// URL-safe) of exactly 32 bytes.
    /// </summary>
    public static IReadOnlyList<byte[]> ParseKeys(string text)
    {
        var parts = text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > MaxKeys)
        {
            throw new OidcConfigException($"cookie secret needs 1 to {MaxKeys} keys.");
        }

        var keys = new List<byte[]>(parts.Length);
        foreach (var part in parts)
        {
            keys.Add(ParseKey(part));
        }

        return keys;
    }

    private static byte[] ParseKey(string text)
    {
        if (text.Length == KeyBytes * 2 && IsHex(text))
        {
            return Convert.FromHexString(text);
        }

        try
        {
            var standard = text.Replace('-', '+').Replace('_', '/').TrimEnd('=');
            var padded = standard.PadRight(standard.Length + ((4 - (standard.Length % 4)) % 4), '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length == KeyBytes)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
            // Falls through to the shared error. The text is never echoed.
        }

        throw new OidcConfigException("cookie secret must be 32 bytes as base64 or 64 hex digits.");
    }

    private static bool IsHex(string text) => text.All(char.IsAsciiHexDigit);

    /// <summary>Encrypts <paramref name="plaintext"/> with the first key. The result is base64url text.</summary>
    public string Seal(string purpose, string cookieName, ReadOnlySpan<byte> plaintext)
    {
        var buffer = new byte[1 + NonceBytes + plaintext.Length + TagBytes];
        buffer[0] = Version;
        var nonce = buffer.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var cipher = buffer.AsSpan(1 + NonceBytes, plaintext.Length);
        var tag = buffer.AsSpan(1 + NonceBytes + plaintext.Length, TagBytes);
        using var aes = new AesGcm(Derive(_masterKeys[0], purpose), TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, Aad(purpose, cookieName));
        return Base64UrlEncoder.Encode(buffer);
    }

    /// <summary>Returns the plaintext, or null when no key authenticates the value.</summary>
    public byte[]? Open(string purpose, string cookieName, string value)
    {
        byte[] raw;
        try
        {
            raw = Base64UrlEncoder.DecodeBytes(value);
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length < 1 + NonceBytes + TagBytes || raw[0] != Version)
        {
            return null;
        }

        var cipherLength = raw.Length - 1 - NonceBytes - TagBytes;
        var nonce = raw.AsSpan(1, NonceBytes);
        var cipher = raw.AsSpan(1 + NonceBytes, cipherLength);
        var tag = raw.AsSpan(1 + NonceBytes + cipherLength, TagBytes);
        var aad = Aad(purpose, cookieName);
        var plaintext = new byte[cipherLength];
        foreach (var master in _masterKeys)
        {
            try
            {
                using var aes = new AesGcm(Derive(master, purpose), TagBytes);
                aes.Decrypt(nonce, cipher, tag, plaintext, aad);
                return plaintext;
            }
            catch (CryptographicException)
            {
                // Wrong key or a changed cookie. Try the next key.
            }
        }

        return null;
    }

    private static byte[] Derive(byte[] master, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeyBytes, salt: null,
            info: Encoding.UTF8.GetBytes("titanium-oidc/v1/" + purpose));

    private static byte[] Aad(string purpose, string cookieName) =>
        Encoding.UTF8.GetBytes("titanium-oidc/v1|" + purpose + "|" + cookieName);
}

/// <summary>A policy that cannot run as written. The message names the setting and never a secret.</summary>
public sealed class OidcConfigException(string message) : Exception(message);
