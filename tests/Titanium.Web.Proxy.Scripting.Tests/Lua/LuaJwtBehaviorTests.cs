using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
/// Pure-C# coverage for <see cref="LuaJwt"/> (no native Lua). Settings load, base64url decode, JSON→Lua, and HS256
/// verify / claim checks — the paths Sonar still misses when Wasm/Lua natives are absent in CI collectors.
/// </summary>
[TestClass]
public class LuaJwtBehaviorTests
{
    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64(string text) => B64(Encoding.UTF8.GetBytes(text));

    private static string Token(string alg, string? kid, object payload, Func<byte[], byte[]> sign)
    {
        var header = kid is null ? $$"""{"alg":"{{alg}}","typ":"JWT"}""" : $$"""{"alg":"{{alg}}","kid":"{{kid}}"}""";
        var signed = B64(header) + "." + B64(JsonSerializer.Serialize(payload));
        return signed + "." + B64(sign(Encoding.ASCII.GetBytes(signed)));
    }

    private static LuaTable Op(string token, string? iss = null, string? aud = null)
    {
        var table = new LuaTable();
        table.Fields["token"] = Encoding.UTF8.GetBytes(token);
        if (iss is not null)
        {
            table.Fields["iss"] = Encoding.UTF8.GetBytes(iss);
        }

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
        var error = LuaReader.AsString(table[2]);
        var claims = table[1];
        return (claims, error);
    }

    // Bug class: missing / blank / empty JWKS is treated as "no keys" or throws instead of a stable settings error.
    [TestMethod]
    public void FromSettings_MissingBlankInvalidAndEmptyJwks_ReportWhy()
    {
        Assert.AreEqual(
            "jwt.verify: settings.jwks is not set",
            LuaJwt.FromSettings(new Dictionary<string, string>()).Error);
        Assert.AreEqual(
            "jwt.verify: settings.jwks is not set",
            LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = "   " }).Error);

        var invalid = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = "{not-json" });
        StringAssert.StartsWith(invalid.Error, "jwt.verify: settings.jwks is not valid JSON:");

        Assert.AreEqual(
            "jwt.verify: settings.jwks has no keys",
            LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = """{"keys":[]}""" }).Error);
        Assert.AreEqual(
            "jwt.verify: settings.jwks has no keys",
            LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = """{"keys":"nope"}""" }).Error);
    }

    // Bug class: base64url decode accepts oversize inputs or rejects valid JWT segments.
    [TestMethod]
    public void TryDecode_RejectsOversizeAndGarbage_AcceptsUrlSafePayload()
    {
        Assert.IsFalse(LuaJwt.TryDecode(new string('A', (64 * 1024) + 1), out _));
        Assert.IsFalse(LuaJwt.TryDecode("!!!", out _));
        Assert.IsTrue(LuaJwt.TryDecode(B64("hello"), out var bytes));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("hello"), bytes);
    }

    // Bug class: JSON→Lua drops arrays, booleans, or nests forever (stack overflow / unbounded tables).
    [TestMethod]
    public void ToLua_MapsObjectsArraysScalarsAndCapsDepth()
    {
        using var doc = JsonDocument.Parse("""{"s":"hi","n":7,"f":1.5,"t":true,"f2":false,"z":null,"a":[1,"x"],"o":{"k":2}}""");
        var table = (LuaTable?)LuaJwt.ToLua(doc.RootElement, 0);
        Assert.IsNotNull(table);
        CollectionAssert.AreEqual("hi"u8.ToArray(), (byte[]?)table["s"]);
        Assert.AreEqual(7L, table["n"]);
        Assert.AreEqual(1.5, table["f"]);
        Assert.AreEqual(true, table["t"]);
        Assert.AreEqual(false, table["f2"]);
        Assert.IsNull(table["z"]);
        var array = (LuaTable?)table["a"];
        Assert.IsNotNull(array);
        Assert.AreEqual(1L, array[1]);
        CollectionAssert.AreEqual("x"u8.ToArray(), (byte[]?)array[2]);
        Assert.AreEqual(2L, ((LuaTable?)table["o"])!["k"]);

        // Depth > 30 must stop nesting rather than recurse forever.
        var nested = new StringBuilder("{");
        for (var i = 0; i < 40; i++)
        {
            nested.Append("\"c\":{");
        }

        nested.Append("\"v\":1");
        nested.Append('}', 40);
        nested.Append('}');
        using var deep = JsonDocument.Parse(nested.ToString());
        Assert.IsNull(LuaJwt.ToLua(deep.RootElement, 31));
    }

    // Bug class: HS256 verify accepts wrong issuer/audience/expired tokens, or refuses a valid token.
    [TestMethod]
    public void Verify_Hs256_ChecksSignatureIssuerAudienceExpiryAndNbf()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[] { new { kty = "oct", kid = "h", k = B64(secret) } },
        });
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks });
        Assert.IsNull(jwt.Error);

        var now = DateTimeOffset.UtcNow;
        object Good(long? exp = null, long? nbf = null, string sub = "alice") => new
        {
            sub,
            iss = "me",
            aud = new[] { "api", "web" },
            exp = exp ?? now.ToUnixTimeSeconds() + 600,
            nbf = nbf ?? now.ToUnixTimeSeconds() - 10,
            n = 7,
        };
        var token = Token("HS256", "h", Good(), data => HMACSHA256.HashData(secret, data));

        var (claims, error) = DecodeVerify(jwt.Verify(Op(token, "me", "web"), now));
        Assert.IsNull(error);
        Assert.IsInstanceOfType<LuaTable>(claims);
        CollectionAssert.AreEqual("alice"u8.ToArray(), (byte[]?)((LuaTable)claims!)["sub"]);

        Assert.AreEqual(
            "jwt.verify: issuer does not match",
            DecodeVerify(jwt.Verify(Op(token, "other"), now)).Error);
        Assert.AreEqual(
            "jwt.verify: audience does not match",
            DecodeVerify(jwt.Verify(Op(token, aud: "admin"), now)).Error);

        var expired = Token(
            "HS256",
            "h",
            Good(exp: now.ToUnixTimeSeconds() - 120),
            data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual("jwt.verify: token expired", DecodeVerify(jwt.Verify(Op(expired), now)).Error);

        var future = Token(
            "HS256",
            "h",
            Good(nbf: now.ToUnixTimeSeconds() + 120),
            data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual("jwt.verify: token not yet valid", DecodeVerify(jwt.Verify(Op(future), now)).Error);

        Assert.AreEqual("jwt.verify: malformed token", DecodeVerify(jwt.Verify(Op("not-a-jwt"), now)).Error);
        Assert.AreEqual(
            "jwt.verify: unsupported alg 'none'",
            DecodeVerify(jwt.Verify(Op(Token("none", null, Good(), _ => [])), now)).Error);

        var parts = token.Split('.');
        var tampered = parts[0] + "." + B64(JsonSerializer.Serialize(Good(sub: "mallory"))) + "." + parts[2];
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(jwt.Verify(Op(tampered), now)).Error);
    }

    // Bug class: FromSettings error short-circuits Verify with a stale / empty message.
    [TestMethod]
    public void Verify_WhenJwksMissing_ReturnsTheLoadError()
    {
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string>());
        var (_, error) = DecodeVerify(jwt.Verify(Op("a.b.c")));
        Assert.AreEqual("jwt.verify: settings.jwks is not set", error);
    }
}
