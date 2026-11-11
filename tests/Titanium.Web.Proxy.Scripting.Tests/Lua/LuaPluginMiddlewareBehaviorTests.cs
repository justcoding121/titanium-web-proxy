using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Scripting.Lua;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>The Lua plugin as route middleware: header edits applied to the live session, deny replies, buffered bodies.</summary>
[TestClass]
public class LuaPluginMiddlewareBehaviorTests
{
    private static SessionEventArgs MakeSession(ProxyServer proxy, string method = "POST", string host = "origin.test")
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        var request = session.HttpClient.Request;
        request.HttpVersion = HttpHeader.Version11;
        request.Method = method;
        request.RequestUriString = "/upload?a=1";
        request.Host = host;
        return session;
    }

    private static ProxyMiddlewareContext Context(SessionEventArgs session) => new() { Session = session };

    private static ProxyMiddlewareDelegate Next(Action onNext) => (_, _) =>
    {
        onNext();
        return ValueTask.CompletedTask;
    };

    private static void GiveBody(SessionEventArgs session, byte[] body)
    {
        session.HttpClient.Request.ContentLength = body.Length;
        session.SetRequestBody(body);
    }

    [TestMethod]
    public async Task NonSessionContext_PassesStraightThrough()
    {
        using var plugin = LuaTestSupport.Create("function on_request_headers(ctx) ctx.deny(500) end");
        var nexts = 0;
        var context = new ProxyMiddlewareContext { Session = new object() };

        await plugin.InvokeAsync(context, Next(() => nexts++), default);

        Assert.AreEqual(1, nexts);
        Assert.IsFalse(context.IsHandled);
    }

    [TestMethod]
    public async Task HeaderEdits_AreAppliedToTheSessionRequest_AndTheChainContinues()
    {
        using var plugin = LuaTestSupport.Create("""
            function on_request_headers(ctx)
              ctx.set_header("x-added", "yes")
              ctx.set_header("x-replaced", "new")
              ctx.remove_header("x-drop")
            end
            """);
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy, "GET");
        session.HttpClient.Request.Headers.AddHeader("x-replaced", "old");
        session.HttpClient.Request.Headers.AddHeader("x-drop", "bye");
        var nexts = 0;
        var context = Context(session);

        await plugin.InvokeAsync(context, Next(() => nexts++), default);

        var headers = session.HttpClient.Request.Headers;
        Assert.AreEqual(1, nexts);
        Assert.IsFalse(context.IsHandled);
        Assert.AreEqual("yes", headers.GetFirstHeader("x-added")!.Value);
        Assert.AreEqual("new", headers.GetFirstHeader("x-replaced")!.Value);
        Assert.IsNull(headers.GetFirstHeader("x-drop"));
    }

    [TestMethod]
    public async Task Deny_HandlesTheRequestWithStatusBodyAndHeaders_AndSkipsNext()
    {
        using var plugin = LuaTestSupport.Create("""
            function on_request_headers(ctx)
              return ctx.deny(418, "teapot", { ["x-why"] = "short" })
            end
            """);
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy, "GET");
        var nexts = 0;
        var context = Context(session);

        await plugin.InvokeAsync(context, Next(() => nexts++), default);

        Assert.AreEqual(0, nexts);
        Assert.IsTrue(context.IsHandled);
        Assert.AreEqual(418, context.HandledStatusCode);
        Assert.AreEqual("teapot", System.Text.Encoding.UTF8.GetString(context.HandledBodyBytes!));
        CollectionAssert.AreEqual(
            new[] { new KeyValuePair<string, string>("x-why", "short") },
            context.HandledHeaders!.ToArray());
    }

    [TestMethod]
    public async Task Deny_WithoutHeaders_LeavesHandledHeadersUnset()
    {
        using var plugin = LuaTestSupport.Create("function on_request_headers(ctx) return ctx.deny(429) end");
        using var proxy = new ProxyServer(false, false, false);
        var context = Context(MakeSession(proxy, "GET"));

        await plugin.InvokeAsync(context, Next(() => Assert.Fail("denied request must not continue")), default);

        Assert.AreEqual(429, context.HandledStatusCode);
        Assert.IsNull(context.HandledHeaders);
    }

    [TestMethod]
    public async Task ScriptError_Throws_SoTheRoutePluginFailModeDecides()
    {
        using var plugin = LuaTestSupport.Create("function on_request_headers(ctx) error('boom') end");
        using var proxy = new ProxyServer(false, false, false);
        var context = Context(MakeSession(proxy, "GET"));

        await Assert.ThrowsExactlyAsync<LuaScriptException>(
            () => plugin.InvokeAsync(context, Next(() => { }), default).AsTask());
    }

    [TestMethod]
    public async Task PluginWithoutRequestHooks_JustContinues()
    {
        using var plugin = LuaTestSupport.Create("function on_response_headers(ctx) end");
        using var proxy = new ProxyServer(false, false, false);
        var nexts = 0;

        await plugin.InvokeAsync(Context(MakeSession(proxy, "GET")), Next(() => nexts++), default);

        Assert.AreEqual(1, nexts);
    }

    [TestMethod]
    public async Task BufferedBody_IsRewrittenByTheScript_AndVarsFlowFromHeadersToBody()
    {
        using var plugin = LuaTestSupport.Create(LuaTestSupport.Config("""
            function on_request_headers(ctx) ctx.vars.tag = "T" end
            function on_request_body(ctx)
              ctx.set_body(ctx.vars.tag .. ":" .. string.upper(ctx.body))
              ctx.set_header("x-body-len", tostring(#ctx.body))
            end
            """, bodyMode: "buffered"));
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        GiveBody(session, "hello"u8.ToArray());
        var context = Context(session);
        var nexts = 0;

        await plugin.InvokeAsync(context, Next(() => nexts++), default);

        Assert.AreEqual(1, nexts);
        Assert.AreEqual("T:HELLO", await session.GetRequestBodyAsString());
        Assert.AreEqual("5", session.HttpClient.Request.Headers.GetFirstHeader("x-body-len")!.Value);
        Assert.AreEqual(0, context.Items.Count, "script vars must not leak past the middleware");
    }

    [TestMethod]
    public async Task BufferedBody_OverTheLimit_IsAnswered413_WithoutCallingTheScript()
    {
        using var plugin = LuaTestSupport.Create(LuaTestSupport.Config("""
            function on_request_body(ctx) ctx.set_header("x-saw", "1") end
            """, bodyMode: "buffered", limits: new PluginLimitsConfig { PoolSize = 1, BufferedBodyBytes = 1024 }));
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        GiveBody(session, new byte[4096]);
        var context = Context(session);

        await plugin.InvokeAsync(context, Next(() => Assert.Fail("an oversized body must not continue")), default);

        Assert.IsTrue(context.IsHandled);
        Assert.AreEqual(413, context.HandledStatusCode);
        StringAssert.Contains(context.HandledBody, "buffered-body limit");
        Assert.IsNull(session.HttpClient.Request.Headers.GetFirstHeader("x-saw"));
    }

    [TestMethod]
    public async Task BufferedBody_DeniedByTheScript_StopsTheChain()
    {
        using var plugin = LuaTestSupport.Create(LuaTestSupport.Config("""
            function on_request_body(ctx)
              if string.find(ctx.body, "attack", 1, true) then return ctx.deny(400, "bad body") end
            end
            """, bodyMode: "buffered"));
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        GiveBody(session, "an attack payload"u8.ToArray());
        var context = Context(session);

        await plugin.InvokeAsync(context, Next(() => Assert.Fail("a denied body must not continue")), default);

        Assert.AreEqual(400, context.HandledStatusCode);
        Assert.AreEqual("bad body", System.Text.Encoding.UTF8.GetString(context.HandledBodyBytes!));
    }

    [TestMethod]
    public async Task StreamingPlugin_DoesNotBufferTheBodyForOnRequestBody()
    {
        using var plugin = LuaTestSupport.Create("""
            function on_request_body(ctx) ctx.set_header("x-saw", "1") end
            """);
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        GiveBody(session, "data"u8.ToArray());
        var nexts = 0;

        await plugin.InvokeAsync(Context(session), Next(() => nexts++), default);

        Assert.AreEqual(1, nexts);
        Assert.IsNull(session.HttpClient.Request.Headers.GetFirstHeader("x-saw"));
    }

    [TestMethod]
    public async Task HeadersDeny_StopsBeforeTheBodyHookRuns()
    {
        using var plugin = LuaTestSupport.Create(LuaTestSupport.Config("""
            function on_request_headers(ctx) return ctx.deny(401) end
            function on_request_body(ctx) ctx.set_header("x-body-ran", "1") end
            """, bodyMode: "buffered"));
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        GiveBody(session, "x"u8.ToArray());
        var context = Context(session);

        await plugin.InvokeAsync(context, Next(() => Assert.Fail("denied")), default);

        Assert.AreEqual(401, context.HandledStatusCode);
        Assert.IsNull(session.HttpClient.Request.Headers.GetFirstHeader("x-body-ran"));
    }

    // IScriptPhaseHost entry points the engine suites do not reach.

    [TestMethod]
    public async Task PhaseHost_RoutesEveryPhaseToItsLuaFunction()
    {
        using var plugin = LuaTestSupport.Create("""
            function on_request_trailers(ctx) ctx.set_header("x-phase", "request_trailers") end
            function on_response_headers(ctx) ctx.set_header("x-phase", "response_headers") end
            function on_response_body(ctx) ctx.set_header("x-phase", "response_body") end
            function on_response_trailers(ctx) ctx.set_header("x-phase", "response_trailers") end
            """);

        var expected = new (ScriptPhases Phase, string Name)[]
        {
            (ScriptPhases.RequestTrailers, "request_trailers"),
            (ScriptPhases.ResponseHeaders, "response_headers"),
            (ScriptPhases.ResponseBody, "response_body"),
            (ScriptPhases.ResponseTrailers, "response_trailers"),
        };
        foreach (var (phase, name) in expected)
        {
            var context = LuaTestSupport.Phase(phase);
            var result = phase switch
            {
                ScriptPhases.RequestTrailers => await plugin.OnRequestTrailersAsync(context, default),
                ScriptPhases.ResponseHeaders => await plugin.OnResponseHeadersAsync(context, default),
                ScriptPhases.ResponseBody => await plugin.OnResponseBodyAsync(context, default),
                _ => await plugin.OnResponseTrailersAsync(context, default),
            };

            Assert.AreEqual(name, result.SetHeaders!["x-phase"], phase.ToString());
        }

        var resumed = await plugin.OnResumeAsync(LuaTestSupport.Phase(ScriptPhases.RequestHeaders), default);
        Assert.AreEqual(ScriptAction.Continue, resumed.Action);
    }

    [TestMethod]
    public async Task OnLog_RunsTheScriptHook_AndSwallowsItsFailure()
    {
        var logger = new Wasm.ListLogger();
        using var plugin = LuaTestSupport.Create(LuaTestSupport.Config("""
            function on_log(ctx) shared.dict("logtest"):incr("n", 1, 0) error("log boom") end
            """), logger);

        await plugin.OnLogAsync(LuaTestSupport.Phase(ScriptPhases.Log), default);

        Assert.IsTrue(logger.Lines.Any(l => l.Contains("on_log failed")), string.Join("|", logger.Lines));
    }

    [TestMethod]
    public async Task OnLog_WithoutAScriptHook_IsANoOp()
    {
        using var plugin = LuaTestSupport.Create("function on_request_headers(ctx) end");

        await plugin.OnLogAsync(LuaTestSupport.Phase(ScriptPhases.Log), default);
    }

    [TestMethod]
    public async Task PhaseFailure_FollowsFailMode()
    {
        const string Script = "function on_request_headers(ctx) error('nope') end";
        using var closed = LuaTestSupport.Create(LuaTestSupport.Config(Script, failMode: ProxyPluginContract.FailClosed));
        using var open = LuaTestSupport.Create(LuaTestSupport.Config(Script, failMode: ProxyPluginContract.FailOpen));
        var phase = LuaTestSupport.Phase(ScriptPhases.RequestHeaders);

        var denied = await closed.OnRequestHeadersAsync(phase, default);
        var passed = await open.OnRequestHeadersAsync(phase, default);

        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(503, denied.LocalResponse!.Status);
        Assert.AreEqual(ScriptAction.Continue, passed.Action);
    }
}
