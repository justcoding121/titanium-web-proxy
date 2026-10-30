using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Lua.Native;
using static Titanium.Web.Proxy.Scripting.Tests.Lua.LuaTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

[TestClass]
public class LuaLibraryTests
{
    private static LuaPlugin _plugin = null!;

    [ClassInitialize]
    public static void Init(TestContext context)
    {
        if (!LuaNative.TryLoad(out var _))
        {
            return;
        }

        _plugin = Create("""
            function run(ctx)
              local f = _G[ctx.fn]
              return f(ctx.arg, ctx.arg2, ctx.arg3)
            end

            function md5(s) return hex.encode(crypto.md5(s)) end
            function sha1(s) return hex.encode(crypto.sha1(s)) end
            function sha256(s) return hex.encode(crypto.sha256(s)) end
            function sha512(s) return hex.encode(crypto.sha512(s)) end
            function hmac(alg, key, msg) return hex.encode(crypto.hmac(alg, key, msg)) end
            function equals(a, b) return crypto.equals(a, b) end
            function b64(s) return base64.encode(s) end
            function b64url(s) return base64.encode_url(s) end
            function unb64(s) local v, err = base64.decode(s) return v or ("ERR:" .. err) end
            function hexdec(s) local v, err = hex.decode(s) return v or ("ERR:" .. err) end
            function urlenc(s) return url.encode(s) end
            function urldec(s, plus) return url.decode(s, plus) end
            function query(s) local q = url.parse_query(s) return url.build_query(q) end
            function json_roundtrip(s) return json.encode(json.decode(s)) end
            function json_bad(s) local ok, err = pcall(json.decode, s) return ok and "OK" or err end
            function json_encode_bad() local ok, err = pcall(json.encode, { f = function() end }) return ok and "OK" or err end
            function rx_match(p, s, flags) return table.concat({ regex.match(p, s, flags) }, "|") end
            function rx_find(p, s) local a, b = regex.find(p, s) return tostring(a) .. "-" .. tostring(b) end
            function rx_test(p, s, flags) return regex.test(p, s, flags) end
            function rx_error(p, s) local ok, err = pcall(regex.test, p, s) return ok and "OK" or err end
            function cidr_match(ip, net) return cidr.match(ip, net) end
            function cidr_list(ip) return cidr.match(ip, { "10.0.0.0/8", "2001:db8::/32" }) end
            function cidr_valid(ip) return cidr.valid(ip) end
            function uuid4() return uuid.v4() end
            function rand() local b = random.bytes(32) local n = random.int(5, 7) return #b .. ":" .. n end
            function times()
              local now, ms, mono = time.now(), time.now_ms(), time.monotonic_ms()
              return math.type(now) .. ":" .. tostring(math.abs(ms - now * 1000) < 2000) .. ":" .. tostring(mono >= 0)
            end
            """);
    }

    [ClassCleanup]
    public static void Cleanup() => _plugin?.Dispose();

    private static async Task<object?> Run(string fn, object? arg = null, object? arg2 = null, object? arg3 = null)
    {
        RequireNative();
        var w = new LuaWriter();
        var mark = w.BeginTable();
        w.Field("fn", fn);
        w.String("arg");
        w.Value(arg);
        w.String("arg2");
        w.Value(arg2);
        w.String("arg3");
        w.Value(arg3);
        w.EndTable(mark, 4);
        return (await _plugin.Runtime.CallAsync("run", w.ToArray(), CancellationToken.None)).Return;
    }

    private static async Task<string?> RunString(string fn, object? arg = null, object? arg2 = null, object? arg3 = null) =>
        LuaReader.AsString(await Run(fn, arg, arg2, arg3));

    [TestMethod]
    public async Task Digests_match_published_vectors()
    {
        Assert.AreEqual("d41d8cd98f00b204e9800998ecf8427e", await RunString("md5", ""));
        Assert.AreEqual("900150983cd24fb0d6963f7d28e17f72", await RunString("md5", "abc"));
        Assert.AreEqual("57edf4a22be3c955ac49da2e2107b67a",
            await RunString("md5", "12345678901234567890123456789012345678901234567890123456789012345678901234567890"));
        Assert.AreEqual("a9993e364706816aba3e25717850c26c9cd0d89d", await RunString("sha1", "abc"));
        Assert.AreEqual("84983e441c3bd26ebaae4aa1f95129e5e54670f1",
            await RunString("sha1", "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"));
        Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await RunString("sha256", "abc"));
        Assert.AreEqual("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
            await RunString("sha256", "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"));
        Assert.AreEqual(
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            await RunString("sha512", "abc"));
    }

    [TestMethod]
    public async Task Digests_match_dotnet_at_every_padding_boundary()
    {
        var random = new Random(7);
        foreach (var length in Enumerable.Range(0, 260).Concat([1000, 4096, 100_000]))
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.AreEqual(Hex(MD5.HashData(data)), await RunString("md5", data), $"md5 {length}");
            Assert.AreEqual(Hex(SHA1.HashData(data)), await RunString("sha1", data), $"sha1 {length}");
            Assert.AreEqual(Hex(SHA256.HashData(data)), await RunString("sha256", data), $"sha256 {length}");
            Assert.AreEqual(Hex(SHA512.HashData(data)), await RunString("sha512", data), $"sha512 {length}");
        }
    }

    [TestMethod]
    public async Task Hmac_matches_rfc_vectors_and_dotnet()
    {
        const string msg = "what do ya want for nothing?";
        Assert.AreEqual("750c783e6ab0b503eaa86e310a5db738", await RunString("hmac", "md5", "Jefe", msg));
        Assert.AreEqual("effcdf6ae5eb2fa2d27416d5f184df9c259a7c79", await RunString("hmac", "sha1", "Jefe", msg));
        Assert.AreEqual("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", await RunString("hmac", "sha256", "Jefe", msg));
        Assert.AreEqual(
            "164b7a7bfcf819e2e395fbe73b56e0a387bd64222e831fd610270cd7ea2505549758bf75c05a994a6d034f65f8f0e6fdcaeab1a34d4a6b4b636e070a38bce737",
            await RunString("hmac", "sha512", "Jefe", msg));

        // A key longer than the block is hashed first (RFC 4231 test case 6).
        var longKey = Enumerable.Repeat((byte)0xaa, 131).ToArray();
        var data = Encoding.ASCII.GetBytes("Test Using Larger Than Block-Size Key - Hash Key First");
        Assert.AreEqual("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54", await RunString("hmac", "sha256", longKey, data));
        Assert.AreEqual(Hex(HMACSHA512.HashData(longKey, data)), await RunString("hmac", "sha512", longKey, data));
        Assert.AreEqual(Hex(HMACMD5.HashData(longKey, data)), await RunString("hmac", "md5", longKey, data));
        Assert.AreEqual(Hex(HMACSHA1.HashData(longKey, data)), await RunString("hmac", "sha1", longKey, data));
    }

    [TestMethod]
    public async Task Constant_time_equals_compares_bytes_and_lengths()
    {
        Assert.AreEqual(true, await Run("equals", "secret", "secret"));
        Assert.AreEqual(false, await Run("equals", "secret", "secreT"));
        Assert.AreEqual(false, await Run("equals", "secret", "secret1"));
        Assert.AreEqual(true, await Run("equals", "", ""));
    }

    [TestMethod]
    public async Task Base64_matches_rfc4648()
    {
        string[] plain = ["", "f", "fo", "foo", "foob", "fooba", "foobar"];
        string[] encoded = ["", "Zg==", "Zm8=", "Zm9v", "Zm9vYg==", "Zm9vYmE=", "Zm9vYmFy"];
        for (var i = 0; i < plain.Length; i++)
        {
            Assert.AreEqual(encoded[i], await RunString("b64", plain[i]));
            Assert.AreEqual(plain[i], await RunString("unb64", encoded[i]));
            Assert.AreEqual(encoded[i].TrimEnd('='), await RunString("b64url", plain[i]));
            Assert.AreEqual(plain[i], await RunString("unb64", encoded[i].TrimEnd('=')));
        }

        var bytes = Enumerable.Range(0, 256).Select(b => (byte)b).ToArray();
        Assert.AreEqual(Convert.ToBase64String(bytes), await RunString("b64", bytes));
        Assert.AreEqual(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), await RunString("b64url", bytes));
        StringAssert.StartsWith(await RunString("unb64", "Zm9v!"), "ERR:");
    }

    [TestMethod]
    public async Task Hex_round_trips_and_rejects_bad_input()
    {
        var bytes = Enumerable.Range(0, 256).Select(b => (byte)b).ToArray();
        CollectionAssert.AreEqual(bytes, (byte[])(await Run("hexdec", Hex(bytes)))!);
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD }, (byte[])(await Run("hexdec", "AbCd"))!);
        Assert.AreEqual("ERR:odd-length hex", await RunString("hexdec", "abc"));
        Assert.AreEqual("ERR:invalid hex", await RunString("hexdec", "zz"));
    }

    [TestMethod]
    public async Task Url_encoding_follows_rfc3986()
    {
        Assert.AreEqual("a%20b%2Bc%2F%3F%26%3D~-._%C3%A9", await RunString("urlenc", "a b+c/?&=~-._é"));
        Assert.AreEqual("a b+c/é", await RunString("urldec", "a%20b+c%2F%C3%A9"));
        Assert.AreEqual("a b c", await RunString("urldec", "a%20b+c", true));
        Assert.AreEqual("a=1&b=x%20y&c=", await RunString("query", "b=x+y&a=1&c=&a=2"));
    }

    [TestMethod]
    public async Task Json_round_trips_and_reports_errors()
    {
        Assert.AreEqual("[1,2,3]", await RunString("json_roundtrip", "[1, 2, 3]"));
        Assert.AreEqual("{\"a\":\"x\\ny\\u0001\"}", await RunString("json_roundtrip", "{\"a\":\"x\\ny\\u0001\"}"));
        Assert.AreEqual("\"\u00e9\U0001F600\"", await RunString("json_roundtrip", "\"\\u00e9\\ud83d\\ude00\""));
        Assert.AreEqual("[true,false,null,1.5,-2]", await RunString("json_roundtrip", "[true,false,null,1.5,-2]"));
        Assert.AreEqual("{}", await RunString("json_roundtrip", "{}"));
        StringAssert.Contains(await RunString("json_bad", "{\"a\":}"), "json");
        StringAssert.Contains(await RunString("json_bad", "[1] x"), "trailing");
        StringAssert.Contains(await RunString("json_bad", new string('[', 100) + new string(']', 100)), "deep");
        Assert.AreNotEqual("OK", await RunString("json_encode_bad"));
    }

    [TestMethod]
    public async Task Regex_matches_with_groups_flags_and_caps()
    {
        Assert.AreEqual("2026|10|29", await RunString("rx_match", @"(\d{4})-(\d\d)-(\d\d)", "on 2026-10-29 at"));
        Assert.AreEqual("4-13", await RunString("rx_find", @"\d{4}-\d\d-\d\d", "on 2026-10-29 at"));
        Assert.AreEqual(true, await Run("rx_test", "^hello$", "HELLO", "i"));
        Assert.AreEqual(false, await Run("rx_test", "^hello$", "HELLO"));
        Assert.AreEqual(true, await Run("rx_test", "^(a|b)+c?$", "abab"));
        Assert.AreEqual(false, await Run("rx_test", "^[^0-9]+$", "ab1"));

        // A Pike VM: no catastrophic backtracking on the classic pathological pattern.
        Assert.AreEqual(false, await Run("rx_test", "^(a+)+$", new string('a', 5000) + "b"));
        StringAssert.Contains(await RunString("rx_error", new string('a', 2000), "a"), "1024");
        StringAssert.Contains(await RunString("rx_error", "a", new string('b', 1024 * 1024 + 1)), "input is longer than 1 MiB");
        Assert.AreEqual(true, await Run("rx_test", "b$", new string('b', 1024 * 1024)));
        StringAssert.Contains(await RunString("rx_error", "(", "a"), "regex");
    }

    [TestMethod]
    public async Task Cidr_matches_v4_v6_and_mapped_addresses()
    {
        Assert.AreEqual(true, await Run("cidr_match", "10.1.2.3", "10.0.0.0/8"));
        Assert.AreEqual(false, await Run("cidr_match", "11.1.2.3", "10.0.0.0/8"));
        Assert.AreEqual(true, await Run("cidr_match", "192.168.1.77", "192.168.1.64/26"));
        Assert.AreEqual(false, await Run("cidr_match", "192.168.1.128", "192.168.1.64/26"));
        Assert.AreEqual(true, await Run("cidr_match", "::ffff:10.0.0.1", "10.0.0.0/8"));
        Assert.AreEqual(true, await Run("cidr_match", "2001:db8::1", "2001:db8::/32"));
        Assert.AreEqual(false, await Run("cidr_match", "2001:db9::1", "2001:db8::/32"));
        Assert.AreEqual(true, await Run("cidr_match", "::1", "::1"));
        Assert.AreEqual(true, await Run("cidr_list", "2001:db8:ffff::5"));
        Assert.AreEqual(false, await Run("cidr_list", "172.16.0.1"));
        Assert.AreEqual(true, await Run("cidr_valid", "fe80::1:2"));
        Assert.AreEqual(false, await Run("cidr_valid", "300.1.1.1"));
        Assert.AreEqual(false, await Run("cidr_valid", "1:2:3:4:5:6:7:8:9"));
    }

    [TestMethod]
    public async Task Uuid_random_and_time_behave()
    {
        var ids = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var id = await RunString("uuid4");
            StringAssert.Matches(id, new System.Text.RegularExpressions.Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"));
            Assert.IsTrue(ids.Add(id!));
        }

        StringAssert.Matches(await RunString("rand"), new System.Text.RegularExpressions.Regex("^32:[5-7]$"));
        Assert.AreEqual("float:true:true", await RunString("times"));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
