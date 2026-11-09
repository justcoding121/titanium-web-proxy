using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Remaining LuaJwt VerifyWith leftovers toward 90%: ES512 key advertised with the wrong
///     curve (P-256) must not verify, and HS key with non-oct kty falls through to false.
/// </summary>
[TestClass]
public class LuaJwtEs512CurveMismatchCoverageBehaviorTests
{
    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64(string text) => B64(Encoding.UTF8.GetBytes(text));

    private static string Token(string alg, string kid, object payload, Func<byte[], byte[]> sign)
    {
        var header = $$"""{"alg":"{{alg}}","typ":"JWT","kid":"{{kid}}"}""";
        var signed = B64(header) + "." + B64(JsonSerializer.Serialize(payload));
        return signed + "." + B64(sign(Encoding.ASCII.GetBytes(signed)));
    }

    private static LuaTable Op(string token)
    {
        var table = new LuaTable();
        table.Fields["token"] = Encoding.UTF8.GetBytes(token);
        return table;
    }

    private static string? VerifyError(byte[] encoded)
    {
        var table = (LuaTable?)new LuaReader(encoded).Read();
        Assert.IsNotNull(table);
        return LuaReader.AsString(table[2]);
    }

    [TestMethod]
    public void Verify_Es512WithP256Curve_AndHsWithRsaKty_RejectSignature()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new
                {
                    kty = "EC",
                    kid = "es",
                    alg = "ES512",
                    crv = "P-256",
                    x = B64(parameters.Q.X!),
                    y = B64(parameters.Q.Y!),
                },
                new
                {
                    kty = "RSA",
                    kid = "hs-wrong",
                    alg = "HS256",
                    n = B64(RandomNumberGenerator.GetBytes(256)),
                    e = B64([1, 0, 1]),
                },
            },
        });
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks });
        Assert.IsNull(jwt.Error);
        var now = DateTimeOffset.UtcNow;
        var payload = new { sub = "a", exp = now.ToUnixTimeSeconds() + 60 };

        var esToken = Token(
            "ES512",
            "es",
            payload,
            data => ecdsa.SignData(data, HashAlgorithmName.SHA512));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            VerifyError(jwt.Verify(Op(esToken), now)));

        var secret = RandomNumberGenerator.GetBytes(32);
        var hsToken = Token(
            "HS256",
            "hs-wrong",
            payload,
            data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            VerifyError(jwt.Verify(Op(hsToken), now)));
    }
}
