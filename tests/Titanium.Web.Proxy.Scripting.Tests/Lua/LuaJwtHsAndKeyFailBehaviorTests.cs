using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Remaining LuaJwt verify edges (HS384/HS512, string audience, bad key material) plus WasmModuleCache
///     path/read/write/discard without a native guest.
/// </summary>
[TestClass]
public class LuaJwtHsAndKeyFailBehaviorTests
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
        return (table[1], LuaReader.AsString(table[2]));
    }

    [TestMethod]
    public void Verify_Hs384AndHs512_AcceptMatchingSignatures_AndStringAudience()
    {
        var secret = RandomNumberGenerator.GetBytes(48);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[] { new { kty = "oct", kid = "h", k = B64(secret) } },
        });
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks });
        Assert.IsNull(jwt.Error);
        var now = DateTimeOffset.UtcNow;
        object Claims() => new
        {
            sub = "bob",
            iss = "issuer",
            aud = "api",
            exp = now.ToUnixTimeSeconds() + 600,
            nbf = now.ToUnixTimeSeconds() - 5,
        };

        var hs384 = Token("HS384", "h", Claims(), data => HMACSHA384.HashData(secret, data));
        var (claims384, error384) = DecodeVerify(jwt.Verify(Op(hs384, "issuer", "api"), now));
        Assert.IsNull(error384);
        CollectionAssert.AreEqual("bob"u8.ToArray(), (byte[]?)((LuaTable)claims384!)["sub"]);

        var hs512 = Token("HS512", "h", Claims(), data => HMACSHA512.HashData(secret, data));
        var (_, error512) = DecodeVerify(jwt.Verify(Op(hs512, aud: "api"), now));
        Assert.IsNull(error512);

        Assert.AreEqual(
            "jwt.verify: audience does not match",
            DecodeVerify(jwt.Verify(Op(hs512, aud: "other"), now)).Error);
    }

    [TestMethod]
    public void Verify_RejectsEmptyOctSecret_MissingRsaParts_AndWrongEcCurve()
    {
        var emptyOct = LuaJwt.FromSettings(new Dictionary<string, string>
        {
            ["jwks"] = """{"keys":[{"kty":"oct","kid":"e","k":""}]}""",
        });
        Assert.IsNull(emptyOct.Error);
        var now = DateTimeOffset.UtcNow;
        var token = Token(
            "HS256",
            "e",
            new { exp = now.ToUnixTimeSeconds() + 60 },
            _ => RandomNumberGenerator.GetBytes(32));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(emptyOct.Verify(Op(token), now)).Error);

        var rsaMissing = LuaJwt.FromSettings(new Dictionary<string, string>
        {
            ["jwks"] = """{"keys":[{"kty":"RSA","kid":"r","alg":"RS256","e":"AQAB"}]}""",
        });
        var rsaToken = Token(
            "RS256",
            "r",
            new { exp = now.ToUnixTimeSeconds() + 60 },
            _ => RandomNumberGenerator.GetBytes(32));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(rsaMissing.Verify(Op(rsaToken), now)).Error);

        var ecWrong = LuaJwt.FromSettings(new Dictionary<string, string>
        {
            ["jwks"] = """{"keys":[{"kty":"EC","kid":"e","alg":"ES256","crv":"P-384","x":"AQ","y":"AQ"}]}""",
        });
        var esToken = Token(
            "ES256",
            "e",
            new { exp = now.ToUnixTimeSeconds() + 60 },
            _ => RandomNumberGenerator.GetBytes(32));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(ecWrong.Verify(Op(esToken), now)).Error);

        // Header / payload that decode but are not JSON objects.
        var arrayHeader = B64("[1]") + "." + B64("""{"sub":"x"}""") + "." + B64("sig");
        Assert.AreEqual(
            "jwt.verify: malformed token",
            DecodeVerify(emptyOct.Verify(Op(arrayHeader), now)).Error);

        // Audience property that is neither string nor array.
        var audNumber = Token(
            "HS256",
            "e",
            new { aud = 7, exp = now.ToUnixTimeSeconds() + 60 },
            data => HMACSHA256.HashData(RandomNumberGenerator.GetBytes(32), data));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(emptyOct.Verify(Op(audNumber, aud: "7"), now)).Error);
    }

    [TestMethod]
    public void WasmModuleCache_PathReadWriteDiscard_RoundTripsOnTrustedDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-wasm-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cache = new WasmModuleCache(dir);
            Assert.IsTrue(cache.PathFor("mod", "fp").EndsWith(".cwasm", StringComparison.Ordinal));
            Assert.IsNull(cache.TryRead("missing", "fp", out var missingWarning));
            Assert.IsNull(missingWarning);

            Assert.IsTrue(cache.TryWrite("mod", "fp", "compiled"u8, out var writeWarning), writeWarning);
            Assert.IsNull(writeWarning);
            var bytes = cache.TryRead("mod", "fp", out var readWarning);
            Assert.IsNull(readWarning);
            CollectionAssert.AreEqual("compiled"u8.ToArray(), bytes);

            cache.Discard("mod", "fp");
            Assert.IsNull(cache.TryRead("mod", "fp", out _));
            cache.Discard("mod", "fp");

            Assert.IsTrue(WasmModuleCache.IsTrusted(dir, out var trustReason), trustReason);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    [TestMethod]
    public void LuaCodec_StringNullAndRawEmpty_EncodeAsNil()
    {
        var writer = new LuaWriter();
        writer.String(null);
        writer.Raw(ReadOnlySpan<byte>.Empty);
        var encoded = writer.ToArray();
        Assert.IsTrue(encoded.Length >= 2);

        var reader = new LuaReader(encoded);
        Assert.IsNull(reader.Read());
        Assert.IsNull(reader.Read());
    }
}
