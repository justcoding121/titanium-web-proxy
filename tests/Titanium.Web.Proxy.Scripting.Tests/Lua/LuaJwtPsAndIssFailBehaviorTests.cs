using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Remaining LuaJwt leftovers toward 90%: PS256 verify success, issuer mismatch after a valid
///     signature, and kid mismatch across otherwise-valid RSA keys.
/// </summary>
[TestClass]
public class LuaJwtPsAndIssFailBehaviorTests
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

    private static LuaTable Op(string token, string? iss = null)
    {
        var table = new LuaTable();
        table.Fields["token"] = Encoding.UTF8.GetBytes(token);
        if (iss is not null)
        {
            table.Fields["iss"] = Encoding.UTF8.GetBytes(iss);
        }

        return table;
    }

    private static (object? Claims, string? Error) DecodeVerify(byte[] encoded)
    {
        var table = (LuaTable?)new LuaReader(encoded).Read();
        Assert.IsNotNull(table);
        return (table[1], LuaReader.AsString(table[2]));
    }

    [TestMethod]
    public void Verify_Ps256_Succeeds_AndRejectsWrongIssuerAndKid()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new
                {
                    kty = "RSA",
                    kid = "ps",
                    alg = "PS256",
                    n = B64(parameters.Modulus!),
                    e = B64(parameters.Exponent!),
                },
                new
                {
                    kty = "RSA",
                    kid = "other",
                    alg = "PS256",
                    n = B64(parameters.Modulus!),
                    e = B64(parameters.Exponent!),
                },
            },
        });
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks });
        Assert.IsNull(jwt.Error);
        var now = DateTimeOffset.UtcNow;
        var payload = new { sub = "a", iss = "me", exp = now.ToUnixTimeSeconds() + 60 };

        var ok = Token(
            "PS256",
            "ps",
            payload,
            data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var (claims, error) = DecodeVerify(jwt.Verify(Op(ok, iss: "me"), now));
        Assert.IsNull(error);
        Assert.IsNotNull(claims);

        Assert.AreEqual(
            "jwt.verify: issuer does not match",
            DecodeVerify(jwt.Verify(Op(ok, iss: "other"), now)).Error);

        var wrongKid = Token(
            "PS256",
            "missing",
            payload,
            data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(jwt.Verify(Op(wrongKid, iss: "me"), now)).Error);
    }
}
