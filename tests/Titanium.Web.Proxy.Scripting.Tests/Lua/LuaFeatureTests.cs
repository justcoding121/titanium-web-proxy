using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using static Titanium.Web.Proxy.Scripting.Tests.Lua.LuaTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

[TestClass]
public class LuaFeatureTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Cancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await send(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
        }
    }

    private static Task<HttpResponseMessage> Hang(CancellationToken token) =>
        Task.Delay(Timeout.Infinite, token).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException(token), TaskScheduler.Default);

    private static LuaPlugin CreateWith(PluginConfig config, HttpMessageHandler handler)
    {
        RequireNative();
        Assert.IsTrue(LuaOptions.TryParse(config, out var options, out var error), error);
        return new LuaPlugin(LuaRuntime.Create(options!, null, handler));
    }

    private static byte[] Ctx(params (string Key, object? Value)[] fields)
    {
        var w = new LuaWriter();
        var mark = w.BeginTable();
        foreach (var (key, value) in fields)
        {
            w.String(key);
            w.Value(value);
        }

        w.EndTable(mark, fields.Length);
        return w.ToArray();
    }

    private static async Task<string?> Call(LuaPlugin plugin, string function, CancellationToken token = default, params (string, object?)[] ctx) =>
        LuaReader.AsString((await plugin.Runtime.CallAsync(function, Ctx(ctx), token)).Return);

    private static string UniqueDict() => "d" + Guid.NewGuid().ToString("N")[..12];

    // ---- callouts ------------------------------------------------------------------------------------------------------

    private const string CalloutScript = """
        function go(ctx)
          local r, err = http.call(ctx.url, { method = "POST", body = "hi", timeout_ms = ctx.timeout,
            headers = { ["x-k"] = "v", host = "evil.test", ["x-bad"] = "a\r\nb" } })
          if not r then return "ERR:" .. err end
          return r.status .. ":" .. r.body .. ":" .. tostring(r.headers["x-r"])
        end
        function quick(ctx) return "quick" end
        function nap(ctx) time.sleep(ctx.ms) return "awake" end
        """;

    [TestMethod]
    public async Task Callout_sends_request_and_returns_response()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new FakeHandler(async (request, token) =>
        {
            seen = request;
            body = await request.Content!.ReadAsStringAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("ok") };
            response.Headers.Add("x-r", "1");
            return response;
        });

        using var plugin = CreateWith(Config(CalloutScript, calloutHosts: ["api.test", "*.svc.test", "port.test:8443"]), handler);
        Assert.AreEqual("201:ok:1", await Call(plugin, "go", default, ("url", "http://api.test/x?y=1")));
        Assert.AreEqual(HttpMethod.Post, seen!.Method);
        Assert.AreEqual("hi", body);
        Assert.AreEqual("v", seen.Headers.GetValues("x-k").Single());
        Assert.IsFalse(seen.Headers.Contains("x-bad"));
        Assert.AreEqual("api.test", seen.RequestUri!.Host);
        Assert.IsNull(seen.Headers.Host);

        Assert.AreEqual("201:ok:1", await Call(plugin, "go", default, ("url", "https://a.b.svc.test/")));
        Assert.AreEqual("201:ok:1", await Call(plugin, "go", default, ("url", "https://port.test:8443/")));
    }

    [TestMethod]
    public async Task Callout_outside_the_allow_list_is_refused()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using (var plugin = CreateWith(Config(CalloutScript, calloutHosts: ["api.test", "*.svc.test", "port.test:8443"]), handler))
        {
            StringAssert.Contains(await Call(plugin, "go", default, ("url", "http://169.254.169.254/latest")), "not in calloutHosts");
            StringAssert.Contains(await Call(plugin, "go", default, ("url", "http://api.test.evil/")), "not in calloutHosts");
            StringAssert.Contains(await Call(plugin, "go", default, ("url", "http://svc.test/")), "not in calloutHosts");
            StringAssert.Contains(await Call(plugin, "go", default, ("url", "https://port.test/")), "not in calloutHosts");
            StringAssert.Contains(await Call(plugin, "go", default, ("url", "http://user:pw@api.test/")), "without credentials");
            StringAssert.Contains(await Call(plugin, "go", default, ("url", "file:///etc/passwd")), "absolute http");
        }

        using (var none = CreateWith(Config(CalloutScript), handler))
        {
            StringAssert.Contains(await Call(none, "go", default, ("url", "http://api.test/")), "not in calloutHosts");
        }

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Callout_timeout_returns_an_error_to_the_script()
    {
        var handler = new FakeHandler((_, token) => Hang(token));
        using var plugin = CreateWith(Config(CalloutScript, calloutHosts: ["api.test"]), handler);
        var watch = Stopwatch.StartNew();
        Assert.AreEqual("ERR:http.call: timed out after 100 ms", await Call(plugin, "go", default, ("url", "http://api.test/"), ("timeout", 100L)));
        Assert.IsTrue(watch.ElapsedMilliseconds < 900, $"took {watch.ElapsedMilliseconds} ms");
        Assert.AreEqual(1, handler.Cancelled);
        Assert.AreEqual(0, plugin.Runtime.CalloutsInFlight);
    }

    [TestMethod]
    public async Task Cancelled_call_frees_its_state_and_the_callout()
    {
        var handler = new FakeHandler((_, token) => Hang(token));
        using var plugin = CreateWith(Config(CalloutScript, calloutHosts: ["api.test"], timeoutMs: 30_000), handler);
        using var cts = new CancellationTokenSource();
        var pending = Call(plugin, "go", cts.Token, ("url", "http://api.test/"), ("timeout", 60_000L));
        await WaitForAsync(() => plugin.Runtime.CalloutsInFlight == 1);

        // The only state is free for other calls while the first one waits on the network.
        Assert.AreEqual("quick", await Call(plugin, "quick"));

        cts.Cancel();
        try
        {
            await pending;
            Assert.Fail("the cancelled call finished");
        }
        catch (OperationCanceledException)
        {
        }

        await WaitForAsync(() => plugin.Runtime.CalloutsInFlight == 0);
        Assert.AreEqual(1, handler.Cancelled);
        Assert.AreEqual("quick", await Call(plugin, "quick"));
        Assert.IsTrue(plugin.Runtime.Pool.Live.All(i => i.InFlight == 0 && !i.IsRetired));
    }

    [TestMethod]
    public async Task Wall_timeout_abandons_a_waiting_call()
    {
        var handler = new FakeHandler((_, token) => Hang(token));
        using var plugin = CreateWith(Config(CalloutScript, calloutHosts: ["api.test"], timeoutMs: 300), handler);
        var ex = await Assert.ThrowsExactlyAsync<LuaScriptException>(() => Call(plugin, "go", default, ("url", "http://api.test/"),
            ("timeout", 60_000L)));
        Assert.AreEqual(LuaFailure.Timeout, ex.Failure);
        await WaitForAsync(() => plugin.Runtime.CalloutsInFlight == 0);
        Assert.AreEqual("quick", await Call(plugin, "quick"));
    }

    [TestMethod]
    public async Task Callout_concurrency_limit_refuses_extra_calls()
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHandler(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") };
        });
        var config = Config(CalloutScript, calloutHosts: ["api.test"], limits: new PluginLimitsConfig { PoolSize = 2, CalloutConcurrency = 1 });
        using var plugin = CreateWith(config, handler);
        var first = Call(plugin, "go", default, ("url", "http://api.test/"));
        await WaitForAsync(() => plugin.Runtime.CalloutsInFlight == 1);
        StringAssert.Contains(await Call(plugin, "go", default, ("url", "http://api.test/")), "1 callouts already in flight");
        release.SetResult();
        Assert.AreEqual("200:done:nil", await first);
    }

    [TestMethod]
    public async Task Callout_response_larger_than_the_buffer_limit_is_refused()
    {
        var handler = new FakeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) }));
        var config = Config(CalloutScript, calloutHosts: ["api.test"], limits: new PluginLimitsConfig { PoolSize = 1, BufferedBodyBytes = 4096 });
        using var plugin = CreateWith(config, handler);
        StringAssert.Contains(await Call(plugin, "go", default, ("url", "http://api.test/")), "larger than 4096 bytes");
    }

    [TestMethod]
    public async Task Sleep_yields_without_holding_the_state()
    {
        using var plugin = CreateWith(Config(CalloutScript), new FakeHandler((_, token) => Hang(token)));
        var watch = Stopwatch.StartNew();
        var nap = Call(plugin, "nap", default, ("ms", 200L));
        Assert.AreEqual("quick", await Call(plugin, "quick"));
        Assert.IsTrue(watch.ElapsedMilliseconds < 150, $"quick waited {watch.ElapsedMilliseconds} ms behind sleep");
        Assert.AreEqual("awake", await nap);
        Assert.IsTrue(watch.ElapsedMilliseconds >= 180);
    }

    // ---- shared dict ---------------------------------------------------------------------------------------------------

    private const string DictScript = """
        local d = shared.dict(settings.dict)
        function api(ctx)
          local out = {}
          local function put(v) out[#out + 1] = tostring(v) end
          put(d:set("a", "x")); put(d:get("a"))
          put(d:add("a", "y")); put(select(2, d:add("a", "y")))
          put(d:add("b", 1)); put(d:incr("b", 2)); put(d:incr("b", 0.5))
          put(select(2, d:incr("missing", 1))); put(d:incr("c", 1, 10))
          put(select(2, d:incr("a", 1)))
          put(d:set("f", true)); put(d:get("f")); put(math.type(d:get("c")))
          put(d:count()); put(d:delete("a")); put(d:get("a")); put(d:count())
          put(d:ttl("b")); put(d:ttl("nope")); put(d:set("b", nil)); put(d:get("b"))
          return table.concat(out, ",")
        end
        function set_ttl(ctx) d:set(ctx.k, "v", ctx.ttl) return d:ttl(ctx.k) end
        function get(ctx) return d:get(ctx.k) end
        function set(ctx) return d:set(ctx.k, ctx.v) end
        function incr(ctx) for i = 1, ctx.n do d:incr("hits", 1, 0) end end
        function add(ctx) local ok = d:add("winner", ctx.id) return ok and "won" or "lost" end
        """;

    private static PluginConfig DictConfig(string dict, int maxItems = 1000, int poolSize = 1) =>
        Config(DictScript, settings: new Dictionary<string, string> { ["dict"] = dict, ["sharedDicts"] = $"{dict}:{maxItems}" },
            limits: new PluginLimitsConfig { PoolSize = poolSize });

    [TestMethod]
    public async Task Shared_dict_api_behaves_like_ngx_shared()
    {
        using var plugin = Create(DictConfig(UniqueDict()));
        Assert.AreEqual(
            "true,x,nil,exists,true,3,3.5,not found,11,not a number,true,true,integer,4,true,nil,3,0.0,nil,true,nil",
            await CallStringAsync(plugin, "api"));
    }

    [TestMethod]
    public async Task Shared_dict_entries_expire()
    {
        using var plugin = Create(DictConfig(UniqueDict()));
        var left = LuaReader.AsString((await plugin.Runtime.CallAsync("set_ttl", Ctx(("k", "t"), ("ttl", 0.2)), default)).Return);
        Assert.IsTrue(double.Parse(left!, System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 0.2, left);
        Assert.AreEqual("v", await Call(plugin, "get", default, ("k", "t")));
        await Task.Delay(400);
        Assert.IsNull(await Call(plugin, "get", default, ("k", "t")));
    }

    [TestMethod]
    public async Task Shared_dict_evicts_least_recently_used()
    {
        using var plugin = Create(DictConfig(UniqueDict(), maxItems: 3));
        foreach (var k in new[] { "a", "b", "c" })
        {
            await Call(plugin, "set", default, ("k", k), ("v", k));
        }

        Assert.AreEqual("a", await Call(plugin, "get", default, ("k", "a")));
        await Call(plugin, "set", default, ("k", "d"), ("v", "d"));
        Assert.IsNull(await Call(plugin, "get", default, ("k", "b")));
        Assert.AreEqual("a", await Call(plugin, "get", default, ("k", "a")));
        Assert.AreEqual("c", await Call(plugin, "get", default, ("k", "c")));
        Assert.AreEqual("d", await Call(plugin, "get", default, ("k", "d")));
    }

    [TestMethod]
    public async Task Shared_dict_is_shared_across_states_and_plugins_without_lost_updates()
    {
        var dict = UniqueDict();
        using var a = Create(DictConfig(dict, poolSize: 4));
        using var b = Create(DictConfig(dict, poolSize: 4));
        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() => Call(i % 2 == 0 ? a : b, "incr", default, ("n", 250L))));
        await Task.WhenAll(tasks);
        Assert.AreEqual("8000", await Call(a, "get", default, ("k", "hits")));
    }

    [TestMethod]
    public async Task Shared_dict_add_has_exactly_one_winner()
    {
        using var plugin = Create(DictConfig(UniqueDict(), poolSize: 8));
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => Call(plugin, "add", default, ("id", (long)i)))));
        Assert.AreEqual(1, results.Count(r => r == "won"));
        Assert.AreEqual(63, results.Count(r => r == "lost"));
    }

    // ---- jwt -----------------------------------------------------------------------------------------------------------

    private const string JwtScript = """
        function check(ctx)
          local claims, err = jwt.verify(ctx.token, { iss = ctx.iss, aud = ctx.aud })
          if not claims then return "ERR:" .. err end
          return claims.sub .. ":" .. tostring(claims.n) .. ":" .. tostring(claims.list and claims.list[2])
        end
        """;

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64(string text) => B64(Encoding.UTF8.GetBytes(text));

    private static string Token(string alg, string? kid, object payload, Func<byte[], byte[]> sign)
    {
        var header = kid is null ? $$"""{"alg":"{{alg}}","typ":"JWT"}""" : $$"""{"alg":"{{alg}}","kid":"{{kid}}"}""";
        var signed = B64(header) + "." + B64(JsonSerializer.Serialize(payload));
        return signed + "." + B64(sign(Encoding.ASCII.GetBytes(signed)));
    }

    [TestMethod]
    public async Task Jwt_verifies_hs_rs_es_and_checks_claims()
    {
        RequireNative();
        var secret = RandomNumberGenerator.GetBytes(32);
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rp = rsa.ExportParameters(false);
        var ep = ec.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new object[]
            {
                new { kty = "oct", kid = "h", k = B64(secret) },
                new { kty = "RSA", kid = "r", n = B64(rp.Modulus!), e = B64(rp.Exponent!) },
                new { kty = "EC", kid = "e", crv = "P-256", x = B64(ep.Q.X!), y = B64(ep.Q.Y!) },
            },
        });
        using var plugin = Create(Config(JwtScript, settings: new Dictionary<string, string> { ["jwks"] = jwks }));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var good = new { sub = "alice", n = 7, list = new[] { "x", "y" }, iss = "me", aud = new[] { "api", "web" }, exp = now + 600 };

        async Task<string?> Check(string token, string? iss = null, string? aud = null) =>
            await Call(plugin, "check", default, ("token", token), ("iss", iss), ("aud", aud));

        var hs = Token("HS256", "h", good, data => HMACSHA256.HashData(secret, data));
        Assert.AreEqual("alice:7:y", await Check(hs, "me", "web"));
        Assert.AreEqual("alice:7:y", await Check(Token("HS512", null, good, data => HMACSHA512.HashData(secret, data))));
        Assert.AreEqual("alice:7:y", await Check(Token("RS256", "r", good, data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))));
        Assert.AreEqual("alice:7:y", await Check(Token("PS384", "r", good, data => rsa.SignData(data, HashAlgorithmName.SHA384, RSASignaturePadding.Pss))));
        Assert.AreEqual("alice:7:y", await Check(Token("ES256", "e", good, data => ec.SignData(data, HashAlgorithmName.SHA256))));

        Assert.AreEqual("ERR:jwt.verify: issuer does not match", await Check(hs, "other"));
        Assert.AreEqual("ERR:jwt.verify: audience does not match", await Check(hs, null, "admin"));
        var expired = good with { exp = now - 120 };
        Assert.AreEqual("ERR:jwt.verify: token expired", await Check(Token("HS256", "h", expired, data => HMACSHA256.HashData(secret, data))));
        var skewed = good with { exp = now - 10 };
        Assert.AreEqual("alice:7:y", await Check(Token("HS256", "h", skewed, data => HMACSHA256.HashData(secret, data))));

        var parts = hs.Split('.');
        var tampered = parts[0] + "." + B64(JsonSerializer.Serialize(good with { sub = "mallory" })) + "." + parts[2];
        Assert.AreEqual("ERR:jwt.verify: signature does not match any key", await Check(tampered));
        Assert.AreEqual("ERR:jwt.verify: unsupported alg 'none'", await Check(Token("none", null, good, _ => [])));
        Assert.AreEqual("ERR:jwt.verify: signature does not match any key",
            await Check(Token("RS256", "h", good, data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))));

        // An RS256 key must not be usable as an HMAC secret (algorithm confusion).
        var confused = Token("HS256", "r", good, data => HMACSHA256.HashData(rp.Modulus!, data));
        Assert.AreEqual("ERR:jwt.verify: signature does not match any key", await Check(confused));
        Assert.AreEqual("ERR:jwt.verify: malformed token", await Check("not-a-jwt"));
    }

    [TestMethod]
    public async Task Jwt_without_jwks_reports_why()
    {
        using var plugin = Create(Config(JwtScript));
        Assert.AreEqual("ERR:jwt.verify: settings.jwks is not set", await Call(plugin, "check", default, ("token", "a.b.c")));
    }

    // ---- timers --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Timers_run_once_or_repeatedly_and_stop_on_dispose()
    {
        var dict = UniqueDict();
        var config = Config($$"""
            local d = shared.dict("{{dict}}")
            function on_configure(ctx) timer.every(50, "tick", "t") end
            function tick(ctx) d:incr("ticks:" .. ctx.arg .. ":" .. ctx.phase, 1, 0) end
            function once(ctx) d:set("once", ctx.arg) end
            function schedule(ctx) timer.at(30, "once", "fired") end
            function read(ctx) return tostring(d:get("ticks:t:timer")) .. "/" .. tostring(d:get("once")) end
            """, poolSize: 3);

        var plugin = Create(config);
        await Call(plugin, "schedule");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? seen;
        while ((seen = await Call(plugin, "read")) is not { } r || !r.EndsWith("/fired") || r.StartsWith("nil") || long.Parse(r.Split('/')[0]) < 3)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"timers did not run: {seen}");
            await Task.Delay(20);
        }

        // Every state ran on_configure, but only the first one's timer was kept.
        await WaitForAsync(() => plugin.Runtime.ActiveTimers == 1);

        plugin.Dispose();
        await WaitForAsync(() => plugin.Runtime.ActiveTimers == 0);
        using var reader = Create(Config($$"""function read(ctx) return shared.dict("{{dict}}"):get("ticks:t:timer") end"""));
        var stopped = await Call(reader, "read");
        await Task.Delay(200);
        Assert.AreEqual(stopped, await Call(reader, "read"));
    }

    // ---- metrics -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Script_metrics_are_published_on_the_scripting_meter()
    {
        var measurements = new ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy.Scripting" && instrument.Name.StartsWith(LuaMetrics.UserPrefix + "fx_", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        using var plugin = Create(Config("""
            function go(ctx)
              metrics.counter("fx_hits")
              metrics.counter("fx_hits", 2, { route = "a" })
              metrics.gauge("fx_depth", 5)
              metrics.histogram("fx_latency", 0.25, { code = "200" })
              metrics.gauge("fx_hits", 1)
              local ok = pcall(metrics.counter, "bad name")
              return tostring(ok)
            end
            """, name: "metrics-test"));
        Assert.AreEqual("false", await CallStringAsync(plugin, "go"));

        var all = measurements.ToArray();
        var hits = all.Where(m => m.Name == LuaMetrics.UserPrefix + "fx_hits").ToArray();
        Assert.AreEqual(2, hits.Length, "the gauge with a counter's name is dropped");
        Assert.AreEqual(3, hits.Sum(m => m.Value));
        Assert.IsTrue(hits.All(m => (string?)m.Tags["plugin"] == "metrics-test"));
        Assert.AreEqual("a", hits[1].Tags["route"]);
        Assert.AreEqual(5, all.Single(m => m.Name == LuaMetrics.UserPrefix + "fx_depth").Value);
        Assert.AreEqual("200", all.Single(m => m.Name == LuaMetrics.UserPrefix + "fx_latency").Tags["code"]);
    }

    // ---- L4 ------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Stream_phases_filter_connections_and_data()
    {
        using var plugin = Create(Config("""
            function on_connect(ctx)
              if ctx.client_ip == "10.9.9.9" then return ctx.deny() end
              ctx.vars.bytes = 0
              if ctx.server_name == "blue.test" then ctx.choose_cluster("blue") end
            end
            function on_data(ctx)
              ctx.vars.bytes = ctx.vars.bytes + #ctx.data
              if ctx.data == "QUIT" then return ctx.deny() end
              if ctx.direction == "upstream" then ctx.set_data(string.upper(ctx.data)) end
            end
            function on_close(ctx) shared.dict("l4test"):set("bytes", ctx.vars.bytes) end
            """));

        StreamConnectionContext Connection(string ip, string? sni = null) => new()
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Parse(ip), 4000),
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            ServerName = sni,
            Items = new Dictionary<string, object?>(),
        };

        Assert.AreEqual(ScriptAction.Deny, (await plugin.OnNewConnectionAsync(Connection("10.9.9.9"), default)).Action);

        var connection = Connection("10.1.1.1", "blue.test");
        var connect = await plugin.OnNewConnectionAsync(connection, default);
        Assert.AreEqual(ScriptAction.Continue, connect.Action);
        Assert.AreEqual("blue", connect.ClusterId);

        var down = await plugin.OnDownstreamDataAsync(connection, "hello"u8.ToArray(), default);
        Assert.AreEqual(ScriptAction.Continue, down.Action);
        Assert.IsNull(down.ReplaceData);
        var up = await plugin.OnUpstreamDataAsync(connection, "world!"u8.ToArray(), default);
        CollectionAssert.AreEqual("WORLD!"u8.ToArray(), up.ReplaceData);
        Assert.AreEqual(ScriptAction.Deny, (await plugin.OnDownstreamDataAsync(connection, "QUIT"u8.ToArray(), default)).Action);

        await plugin.OnCloseAsync(connection);
        Assert.AreEqual(0, connection.Items.Count);
        using var reader = Create(Config("""function read(ctx) return shared.dict("l4test"):get("bytes") end"""));
        Assert.AreEqual("15", await CallStringAsync(reader, "read"));
    }

    // ---- chain ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Chain_runs_scripts_in_order_and_first_deny_wins()
    {
        using var a = Create(Config("""
            function on_request_headers(ctx) ctx.set_header("x-order", "a") ctx.choose_cluster("from-a") end
            """, name: "a"));
        using var b = Create(Config("""
            function on_request_headers(ctx)
              ctx.set_header("x-order", (ctx.get_header("x-order") or "") .. ",b")
              ctx.remove_header("x-drop")
              if ctx.get_header("x-deny") then return ctx.deny(418, "teapot") end
            end
            """, name: "b"));
        using var c = Create(Config("""
            function on_request_headers(ctx)
              ctx.set_header("x-order", ctx.get_header("x-order") .. ",c")
              ctx.set_header("x-dropped", tostring(ctx.get_header("x-drop")))
              ctx.choose_cluster("from-c")
            end
            """, name: "c"));

        var chain = new ScriptPhaseHostChain([a, b, c]);
        Assert.IsTrue(chain.Phases.HasFlag(ScriptPhases.RequestHeaders));
        var result = await chain.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, headers: [H("x-drop", "1")]), default);
        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual("a,b,c", result.SetHeaders!["x-order"]);
        Assert.AreEqual("nil", result.SetHeaders["x-dropped"]);
        CollectionAssert.Contains(result.RemoveHeaders!.ToList(), "x-drop");
        Assert.AreEqual("from-c", result.UpstreamClusterId);

        var denied = await chain.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, headers: [H("x-deny", "1")]), default);
        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(418, denied.LocalResponse!.Status);
    }
}
