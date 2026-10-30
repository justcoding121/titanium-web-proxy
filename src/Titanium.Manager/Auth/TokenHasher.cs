using System.Security.Cryptography;
using System.Text;

namespace Titanium.Manager.Auth;

internal static class TokenHasher
{
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool Matches(string token, string hash) =>
        !string.IsNullOrEmpty(token) &&
        !string.IsNullOrEmpty(hash) &&
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            Convert.FromHexString(hash));

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
