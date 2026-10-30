using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using static Titanium.Web.Proxy.Scripting.Tests.Lua.LuaTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

[TestClass]
public class LuaEngineTests
{
    [TestMethod]
    public void Bootstrap_registers_the_lua_engine()
    {
        ScriptingBootstrap.Register();
        Assert.IsTrue(PluginEngines.TryGet(PluginKinds.Lua, out var engine));
        Assert.IsInstanceOfType<LuaPluginEngine>(engine);
    }

    [TestMethod]
    public void Missing_file_and_bad_limits_fail_clearly()
    {
        var engine = new LuaPluginEngine();
        Assert.IsFalse(engine.TryCreate(new PluginConfig { Name = "x", Kind = "lua", File = "nope.lua" }, null, out _, out var error));
        StringAssert.Contains(error, "does not exist");

        var config = Config("function on_request_headers(ctx) end", limits: new PluginLimitsConfig { MemoryMb = 0 });
        Assert.IsFalse(engine.TryCreate(config, null, out _, out error));
        StringAssert.Contains(error, "limits.memoryMb");

        config = Config("x = 1", limits: new PluginLimitsConfig { Fuel = 10 });
        Assert.IsFalse(engine.TryCreate(config, null, out _, out error));
        StringAssert.Contains(error, "maxInstructions");
    }

    [TestMethod]
    public void Syntax_error_and_bytecode_fail_the_load()
    {
        StringAssert.Contains(CreateError(Config("function on_request_headers(ctx")), "expected");

        var config = Config("x");
        File.WriteAllBytes(config.File!, [0x1B, (byte)'L', (byte)'u', (byte)'a', 0x54, 0x00]);
        StringAssert.Contains(CreateError(config), "binary");
    }

    [TestMethod]
    public void Load_time_error_and_on_configure_error_fail_the_load()
    {
        StringAssert.Contains(CreateError(Config("error('boom at load')")), "boom at load");
        StringAssert.Contains(CreateError(Config("function on_configure() error('bad settings') end")), "bad settings");
    }

    [TestMethod]
    public async Task Sandbox_removes_escape_hatches()
    {
        using var plugin = Create("""
            function probe()
              local names = {
                io = io, package = package, debug = debug, require = require, dofile = dofile, loadfile = loadfile,
                load = load, loadstring = loadstring, ["os.execute"] = os.execute, ["os.getenv"] = os.getenv,
                ["os.exit"] = os.exit, ["os.remove"] = os.remove, ["os.rename"] = os.rename, ["os.tmpname"] = os.tmpname,
                ["string.dump"] = string.dump, ["os.setlocale"] = os.setlocale,
              }
              local present = {}
              for k, v in pairs(names) do present[#present + 1] = k end
              table.sort(present)
              return table.concat(present, ",")
            end
            """);
        Assert.AreEqual(string.Empty, await CallStringAsync(plugin, "probe"));
    }

    [TestMethod]
    public async Task Sandbox_keeps_safe_libraries()
    {
        using var plugin = Create("""
            function probe()
              return string.upper("ok") .. table.concat({1, 2}, "") .. math.floor(3.7) .. utf8.char(72) ..
                type(os.time()) .. type(coroutine.wrap)
            end
            """);
        Assert.AreEqual("OK123Hnumberfunction", await CallStringAsync(plugin, "probe"));
    }

    [TestMethod]
    public async Task Collectgarbage_only_allows_safe_options()
    {
        using var plugin = Create("""
            function stop() collectgarbage("stop") end
            function count() return collectgarbage("count") > 0 end
            """);
        StringAssert.Contains((await CallFailsAsync(plugin, "stop")).Message, "invalid option");
        Assert.AreEqual(true, await CallAsync(plugin, "count"));
    }

    [TestMethod]
    public async Task Coroutine_running_never_exposes_the_phase_coroutine()
    {
        using var plugin = Create("""
            function probe()
              local co, main = coroutine.running()
              return tostring(co) .. ":" .. tostring(main)
            end
            function yield_at_phase() coroutine.yield() end
            function own_coroutine()
              local co = coroutine.wrap(function(a) local b = coroutine.yield(a + 1) return b * 2 end)
              return co(1) + co(10)
            end
            """);
        Assert.AreEqual("nil:false", await CallStringAsync(plugin, "probe"));
        StringAssert.Contains((await CallFailsAsync(plugin, "yield_at_phase")).Message, "coroutine.yield cannot be used");
        Assert.AreEqual(22L, await CallAsync(plugin, "own_coroutine"));
    }

    [TestMethod]
    public async Task String_guards_cap_rep_format_and_patterns()
    {
        using var plugin = Create("""
            function rep() return #string.rep("x", 1024 * 1024 * 1024) end
            function rep_method() return #(("x"):rep(64, ",")) end
            function long_pattern() return string.find("abc", string.rep("a", 5000)) end
            function slow_pattern() return string.find(string.rep("a", 20000), ".-.-.-.-.-.-.-b") end
            function format_width() return string.format("%100d", 1) end
            function format_long() return string.format(string.rep("%d", 5000), 1) end
            function format_ok() return string.format("%5.2f|%s|%d", 3.14159, "s", 42) end
            """);
        StringAssert.Contains((await CallFailsAsync(plugin, "rep")).Message, "string.rep");
        Assert.AreEqual(64L * 2 - 1, await CallAsync(plugin, "rep_method"));
        StringAssert.Contains((await CallFailsAsync(plugin, "long_pattern")).Message, "pattern");
        var slow = await CallFailsAsync(plugin, "slow_pattern");
        Assert.IsTrue(slow.Failure is LuaFailure.Runtime or LuaFailure.Instructions, slow.Message);
        StringAssert.Contains((await CallFailsAsync(plugin, "format_width")).Message, "invalid conversion");
        StringAssert.Contains((await CallFailsAsync(plugin, "format_long")).Message, "8192");
        Assert.AreEqual(" 3.14|s|42", await CallStringAsync(plugin, "format_ok"));
    }

    [TestMethod]
    public async Task Infinite_loop_trips_the_instruction_limit_and_the_state_is_replaced()
    {
        using var plugin = Create(Config("""
            function spin() while true do end end
            function spin_under_pcall()
              for i = 1, 100 do pcall(function() while true do end end) end
              return "escaped"
            end
            function ok() return "ok" end
            """, limits: new PluginLimitsConfig { PoolSize = 1, MaxInstructions = 2_000_000 }, timeoutMs: 10_000));

        var watch = Stopwatch.StartNew();
        Assert.AreEqual(LuaFailure.Instructions, (await CallFailsAsync(plugin, "spin")).Failure);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());

        // The tripped state left the pool; its replacement serves the next call.
        Assert.AreEqual("ok", await CallStringAsync(plugin, "ok"));
        Assert.AreEqual(LuaFailure.Instructions, (await CallFailsAsync(plugin, "spin_under_pcall")).Failure);
        Assert.AreEqual("ok", await CallStringAsync(plugin, "ok"));
    }

    [TestMethod]
    public async Task Cpu_deadline_stops_a_loop_before_the_wall_timeout()
    {
        using var plugin = Create(Config("function spin() while true do end end",
            limits: new PluginLimitsConfig { PoolSize = 1, MaxInstructions = 100_000_000_000, EpochDeadlineMs = 100 }, timeoutMs: 5_000));
        var watch = Stopwatch.StartNew();
        Assert.AreEqual(LuaFailure.Deadline, (await CallFailsAsync(plugin, "spin")).Failure);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
    }

    [TestMethod]
    public async Task Memory_bomb_hits_the_allocator_cap()
    {
        using var plugin = Create(Config("""
            function table_bomb()
              local t = {}
              for i = 1, 1e9 do t[i] = string.rep("x", 1000) .. i end
            end
            function concat_bomb()
              local s = "x"
              while true do s = s .. s end
            end
            function caught_bomb()
              local ok = pcall(table_bomb)
              local t = {}
              for i = 1, 1e9 do t[i] = {} end
            end
            function ok() return collectgarbage("count") end
            """, limits: new PluginLimitsConfig { PoolSize = 1, MemoryMb = 8, MaxInstructions = 1_000_000_000 }, timeoutMs: 20_000));

        Assert.AreEqual(LuaFailure.Memory, (await CallFailsAsync(plugin, "table_bomb")).Failure);
        var concat = await CallFailsAsync(plugin, "concat_bomb");
        Assert.IsTrue(concat.Failure is LuaFailure.Memory or LuaFailure.Runtime, concat.Message);
        Assert.AreEqual(LuaFailure.Memory, (await CallFailsAsync(plugin, "caught_bomb")).Failure);
        var kb = await CallAsync(plugin, "ok");
        Assert.IsTrue(Convert.ToDouble(kb) < 8 * 1024, $"{kb} KB");
    }

    [TestMethod]
    public async Task Fail_closed_denies_and_fail_open_continues()
    {
        const string source = "function on_request_headers(ctx) error('script bug') end";
        using var closed = Create(Config(source));
        var result = await closed.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders), CancellationToken.None);
        Assert.AreEqual(ScriptAction.Deny, result.Action);
        Assert.AreEqual(503, result.LocalResponse!.Status);

        using var open = Create(Config(source, failMode: "open"));
        result = await open.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders), CancellationToken.None);
        Assert.AreSame(ScriptPhaseResult.Continue, result);

        using var spinning = Create(Config("function on_request_headers(ctx) while true do end end", failMode: "open",
            limits: new PluginLimitsConfig { PoolSize = 1, MaxInstructions = 100_000 }));
        result = await spinning.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders), CancellationToken.None);
        Assert.AreSame(ScriptPhaseResult.Continue, result);
    }

    [TestMethod]
    public async Task Phases_see_ctx_and_return_edits()
    {
        using var plugin = Create("""
            function on_request_headers(ctx)
              if ctx.get_header("X-Block") == "1" then return ctx.deny(451, "blocked", { ["x-why"] = "test" }) end
              ctx.vars.user = ctx.headers["x-user"]
              ctx.set_header("x-seen", ctx.method .. " " .. ctx.path .. " " .. ctx.client_ip .. " " .. ctx.route_id)
              ctx:remove_header("x-drop")
              ctx.choose_cluster("blue")
            end
            function on_response_headers(ctx)
              ctx.set_header("x-user", ctx.vars.user or "none")
              ctx.set_header("x-status", tostring(ctx.status))
            end
            function on_response_body(ctx)
              ctx.set_body(string.upper(ctx.body))
            end
            """);
        Assert.AreEqual(ScriptPhases.RequestHeaders | ScriptPhases.ResponseHeaders | ScriptPhases.ResponseBody, plugin.Phases);

        var items = new Dictionary<string, object?>();
        var request = await plugin.OnRequestHeadersAsync(
            Phase(ScriptPhases.RequestHeaders, items, [H("X-User", "alice"), H("x-drop", "1")]), CancellationToken.None);
        Assert.AreEqual(ScriptAction.Continue, request.Action);
        Assert.AreEqual("GET /foo?x=1 10.1.2.3 r1", request.SetHeaders!["x-seen"]);
        CollectionAssert.AreEqual(new[] { "x-drop" }, request.RemoveHeaders!.ToArray());
        Assert.AreEqual("blue", request.UpstreamClusterId);

        var response = await plugin.OnResponseHeadersAsync(Phase(ScriptPhases.ResponseHeaders, items, status: 201), CancellationToken.None);
        Assert.AreEqual("alice", response.SetHeaders!["x-user"]);
        Assert.AreEqual("201", response.SetHeaders["x-status"]);

        var body = await plugin.OnResponseBodyAsync(
            Phase(ScriptPhases.ResponseBody, items, chunk: Encoding.UTF8.GetBytes("hello"), last: true), CancellationToken.None);
        Assert.AreEqual("HELLO", Encoding.UTF8.GetString(body.ReplaceBody!));

        var denied = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, null, [H("x-block", "1")]), CancellationToken.None);
        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(451, denied.LocalResponse!.Status);
        Assert.AreEqual("blocked", Encoding.UTF8.GetString(denied.LocalResponse.Body!));
        Assert.AreEqual("test", denied.LocalResponse.Headers!.Single(h => h.Key == "x-why").Value);
    }

    [TestMethod]
    public async Task Header_injection_is_refused()
    {
        using var plugin = Create("""
            function crlf(ctx) ctx.set_header("x-a", "v\r\nInjected: 1") end
            function bad_name(ctx) ctx.set_header("bad name", "v") end
            """);
        StringAssert.Contains((await CallFailsAsync(plugin, "crlf")).Message, "CR");
        StringAssert.Contains((await CallFailsAsync(plugin, "bad_name")).Message, "not allowed");
    }

    [TestMethod]
    public async Task Settings_are_visible_and_on_configure_runs_in_every_state()
    {
        using var plugin = Create(Config("""
            configured = 0
            function on_configure(ctx) configured = configured + 1; phase = ctx.phase end
            function probe(ctx) return settings.greeting .. ":" .. ctx.settings.greeting .. ":" .. configured .. ":" .. phase end
            """, poolSize: 3, settings: new Dictionary<string, string> { ["greeting"] = "hi" }));
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => CallStringAsync(plugin, "probe")));
        Assert.IsTrue(results.All(r => r == "hi:hi:1:configure"), string.Join(",", results.Distinct()));
        Assert.AreEqual(3, plugin.Runtime.Pool.Live.Count());
    }

    [TestMethod]
    public async Task Concurrent_calls_share_the_pool_without_crossing_state()
    {
        using var plugin = Create("""
            function on_request_headers(ctx)
              ctx.vars.n = tonumber(ctx.headers["x-n"])
              local s = 0
              for i = 1, 2000 do s = s + i end
              ctx.set_header("x-echo", tostring(ctx.vars.n * 2))
            end
            """, poolSize: 4);
        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var r = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, null, [H("x-n", i.ToString())]), CancellationToken.None);
            Assert.AreEqual((i * 2).ToString(), r.SetHeaders!["x-echo"]);
        });
        await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task Reload_under_load_swaps_atomically()
    {
        static string Version(int v) => $"function on_request_headers(ctx) ctx.set_header('x-version', '{v}') end";
        var current = Create(Version(1), poolSize: 2);
        using var stop = new CancellationTokenSource();
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var errors = 0;

        var load = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var plugin = Volatile.Read(ref current);
                try
                {
                    var r = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders), CancellationToken.None);
                    seen.Add(r.SetHeaders?["x-version"] ?? "deny");
                }
                catch (ObjectDisposedException)
                {
                    // A caller that read the old plugin after it was retired; the host drains before disposing.
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        })).ToArray();

        await Task.Delay(100);
        // A broken candidate never replaces the running plugin.
        Assert.IsNotNull(CreateError(Config("function on_request_headers(ctx")));
        var next = Create(Version(2), poolSize: 2);
        var old = Interlocked.Exchange(ref current, next);
        await Task.Delay(100);
        old.Dispose();
        await Task.Delay(100);
        seen.Clear();
        await Task.Delay(100);
        stop.Cancel();
        await Task.WhenAll(load);
        next.Dispose();

        Assert.AreEqual(0, errors);
        Assert.IsTrue(seen.Count > 0);
        Assert.IsTrue(seen.All(v => v == "2"), string.Join(",", seen.Distinct()));
    }
}
