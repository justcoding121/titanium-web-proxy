using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Remaining LuaJwt edges on the PR Sonar measure: missing aud claim, non-string aud array
///     entries, oversized decode reject, and verify catch path for non-object JSON after decode.
/// </summary>
[TestClass]
public class LuaJwtAudienceAndDecodeFailBehaviorTests
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

    private static LuaTable Op(string token, string? aud = null)
    {
        var table = new LuaTable();
        table.Fields["token"] = Encoding.UTF8.GetBytes(token);
        if (aud is not null)
        {
            table.Fields["aud"] = Encoding.UTF8.GetBytes(aud);
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
    public void Verify_MissingAudClaim_AndNonStringAudArrayEntry_RejectAudience()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[] { new { kty = "oct", kid = "h", k = B64(secret) } },
        });
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks });
        Assert.IsNull(jwt.Error);
        var now = DateTimeOffset.UtcNow;

        var noAud = Token(
            "HS256",
            "h",
            new { sub = "a", exp = now.ToUnixTimeSeconds() + 60 },
            data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual(
            "jwt.verify: audience does not match",
            DecodeVerify(jwt.Verify(Op(noAud, aud: "api"), now)).Error);

        var audNumbers = Token(
            "HS256",
            "h",
            new { aud = new object[] { 1, 2 }, exp = now.ToUnixTimeSeconds() + 60 },
            data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual(
            "jwt.verify: audience does not match",
            DecodeVerify(jwt.Verify(Op(audNumbers, aud: "1"), now)).Error);
    }

    [TestMethod]
    public void TryDecode_RejectsOversize_AndVerifySurfacesCaughtDecodeErrors()
    {
        Assert.IsFalse(LuaJwt.TryDecode(new string('A', (64 * 1024) + 8), out _));
        Assert.IsFalse(LuaJwt.TryDecode("!!!not-b64!!!", out _));

        var jwt = LuaJwt.FromSettings(new Dictionary<string, string>
        {
            ["jwks"] = """{"keys":[{"kty":"oct","kid":"h","k":"AQ"}]}""",
        });
        // Header/payload decode to non-objects → catch or malformed branch.
        var bad = B64("not-json") + "." + B64("also-not") + "." + B64("sig");
        var error = DecodeVerify(jwt.Verify(Op(bad), DateTimeOffset.UtcNow)).Error;
        Assert.IsNotNull(error);
        StringAssert.StartsWith(error!, "jwt.verify:");
    }
}
