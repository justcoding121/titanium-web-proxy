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

    public static bool Matches(string token, string hash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(hash))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(hash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
