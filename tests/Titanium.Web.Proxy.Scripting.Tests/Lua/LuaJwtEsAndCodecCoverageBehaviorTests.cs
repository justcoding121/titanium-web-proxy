using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Remaining LuaJwt ES384/ES512 / array-audience edges and LuaCodec AsString/AsInt /
///     integral-double table keys / truncated-tag rejects still thin on Sonar.
/// </summary>
[TestClass]
public class LuaJwtEsAndCodecCoverageBehaviorTests
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

    private static (string Jwks, Func<byte[], byte[]> Sign) EcKey(string alg, ECCurve curve)
    {
        using var ecdsa = ECDsa.Create(curve);
        var pub = ecdsa.ExportParameters(includePrivateParameters: false);
        var priv = ecdsa.ExportParameters(includePrivateParameters: true);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new
                {
                    kty = "EC",
                    kid = "ec",
                    crv = alg switch
                    {
                        "ES256" => "P-256",
                        "ES384" => "P-384",
                        _ => "P-521",
                    },
                    x = B64(pub.Q.X!),
                    y = B64(pub.Q.Y!),
                },
            },
        });
        var hash = alg switch
        {
            "ES256" => HashAlgorithmName.SHA256,
            "ES384" => HashAlgorithmName.SHA384,
            _ => HashAlgorithmName.SHA512,
        };
        return (jwks, data =>
        {
            using var signer = ECDsa.Create(priv);
            return signer.SignData(data, hash);
        });
    }

    [TestMethod]
    public void Verify_Es384AndEs512_AcceptMatchingSignatures_AndArrayAudience()
    {
        var now = DateTimeOffset.UtcNow;
        object Claims() => new
        {
            sub = "carol",
            iss = "issuer",
            aud = new[] { "api", "web" },
            exp = now.ToUnixTimeSeconds() + 600,
            nbf = now.ToUnixTimeSeconds() - 5,
        };

        var (jwks384, sign384) = EcKey("ES384", ECCurve.NamedCurves.nistP384);
        var jwt384 = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks384 });
        Assert.IsNull(jwt384.Error);
        var es384 = Token("ES384", "ec", Claims(), sign384);
        var (claims384, error384) = DecodeVerify(jwt384.Verify(Op(es384, "issuer", "api"), now));
        Assert.IsNull(error384);
        CollectionAssert.AreEqual("carol"u8.ToArray(), (byte[]?)((LuaTable)claims384!)["sub"]);

        var (jwks512, sign512) = EcKey("ES512", ECCurve.NamedCurves.nistP521);
        var jwt512 = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks512 });
        Assert.IsNull(jwt512.Error);
        var es512 = Token("ES512", "ec", Claims(), sign512);
        Assert.IsNull(DecodeVerify(jwt512.Verify(Op(es512, aud: "web"), now)).Error);
        Assert.AreEqual(
            "jwt.verify: audience does not match",
            DecodeVerify(jwt512.Verify(Op(es512, aud: "other"), now)).Error);

        // Two-part token and garbage signature alphabet.
        Assert.AreEqual(
            "jwt.verify: malformed token",
            DecodeVerify(jwt384.Verify(Op("a.b"), now)).Error);
        Assert.AreEqual(
            "jwt.verify: malformed token",
            DecodeVerify(jwt384.Verify(Op("a.b.!!!"), now)).Error);
    }

    [TestMethod]
    public void Verify_MismatchedKeyAlg_AndNumericAudience_Reject()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new { kty = "oct", kid = "h", alg = "HS512", k = B64(secret) },
            },
        });
        var jwt = LuaJwt.FromSettings(new Dictionary<string, string> { ["jwks"] = jwks });
        var now = DateTimeOffset.UtcNow;
        var hs256 = Token(
            "HS256",
            "h",
            new { aud = 9, exp = now.ToUnixTimeSeconds() + 60 },
            data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual(
            "jwt.verify: signature does not match any key",
            DecodeVerify(jwt.Verify(Op(hs256, aud: "9"), now)).Error);
    }

    [TestMethod]
    public void LuaCodec_AsStringAsInt_IntegralDoubleKeys_AndUnknownTag()
    {
        Assert.IsNull(LuaReader.AsString(null));
        Assert.AreEqual("true", LuaReader.AsString(true));
        Assert.AreEqual("false", LuaReader.AsString(false));
        Assert.AreEqual("42", LuaReader.AsString(42L));
        Assert.AreEqual("3.5", LuaReader.AsString(3.5));
        CollectionAssert.AreEqual("hi"u8.ToArray(), Encoding.UTF8.GetBytes(LuaReader.AsString("hi"u8.ToArray())!));
        Assert.IsNull(LuaReader.AsString(new object()));

        Assert.AreEqual(7L, LuaReader.AsInt(7L));
        Assert.AreEqual(8L, LuaReader.AsInt(8.0));
        Assert.AreEqual(9L, LuaReader.AsInt("9"u8.ToArray()));
        Assert.IsNull(LuaReader.AsInt(3.14));
        Assert.IsNull(LuaReader.AsInt(true));

        var writer = new LuaWriter();
        var mark = writer.BeginTable();
        writer.Double(2.0);
        writer.String("second");
        writer.Int(1);
        writer.String("first");
        writer.EndTable(mark, 2);
        var table = (LuaTable?)new LuaReader(writer.ToArray()).Read();
        Assert.IsNotNull(table);
        Assert.AreEqual(2, table!.Array.Count);
        CollectionAssert.AreEqual("first"u8.ToArray(), (byte[]?)table.Array[0]);
        CollectionAssert.AreEqual("second"u8.ToArray(), (byte[]?)table.Array[1]);

        Assert.ThrowsExactly<FormatException>(() => _ = new LuaReader([(byte)'Z']).Read());
        Assert.ThrowsExactly<FormatException>(() => _ = new LuaReader([(byte)'i', 1]).Read());
        Assert.AreEqual(-1, new LuaReader([(byte)'n']).ReadTableHeader());
        var raw = new LuaWriter();
        raw.Bool(true);
        Assert.AreEqual(0, new LuaReader(raw.ToArray()).Position);
        var reader = new LuaReader(raw.ToArray());
        CollectionAssert.AreEqual(raw.ToArray(), reader.ReadRaw().ToArray());
    }
}
