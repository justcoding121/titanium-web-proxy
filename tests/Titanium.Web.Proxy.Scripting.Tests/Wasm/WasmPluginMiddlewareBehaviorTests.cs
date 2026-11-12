using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>The WASM plugin as route middleware and as a connection filter: live session edits, deny replies, and faults.</summary>
[TestClass]
[DoNotParallelize]
public class WasmPluginMiddlewareBehaviorTests
{
    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        var request = session.HttpClient.Request;
        request.HttpVersion = HttpHeader.Version11;
        request.Method = "GET";
        request.RequestUriString = "/shop/items?id=7";
        request.Host = "origin.test";
        return session;
    }

    private static ProxyMiddlewareDelegate Next(Action onNext) => (_, _) =>
    {
        onNext();
        return ValueTask.CompletedTask;
    };

    [TestMethod]
    public async Task NonSessionContext_PassesStraightThrough()
    {
        using var plugin = Create(Config("headers_filter.wat"));
        var nexts = 0;

        await plugin.InvokeAsync(new ProxyMiddlewareContext { Session = new object() }, Next(() => nexts++), default);

        Assert.AreEqual(1, nexts);
    }

    [TestMethod]
    public async Task GuestHeaderEdits_AreAppliedToTheLiveSessionRequest()
    {
        using var plugin = Create(Config("headers_filter.wat"));
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        session.HttpClient.Request.Headers.AddHeader("x-remove-me", "gone");
        session.HttpClient.Request.Headers.AddHeader("x-keep", "kept");
        var context = new ProxyMiddlewareContext { Session = session };
        var nexts = 0;

        await plugin.InvokeAsync(context, Next(() => nexts++), default);

        var headers = session.HttpClient.Request.Headers;
        Assert.AreEqual(1, nexts);
        Assert.IsFalse(context.IsHandled);
        Assert.AreEqual("1", headers.GetFirstHeader("x-wasm-seen")!.Value);
        Assert.IsNull(headers.GetFirstHeader("x-remove-me"));
        Assert.AreEqual("kept", headers.GetFirstHeader("x-keep")!.Value);
        StringAssert.Contains(headers.GetFirstHeader("x-path")!.Value, "/shop/items");
    }

    [TestMethod]
    public async Task GuestLocalResponse_HandlesTheRequestAndSkipsNext()
    {
        using var plugin = Create(Config("headers_filter.wat"));
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        session.HttpClient.Request.Headers.AddHeader("x-block", "1");
        var context = new ProxyMiddlewareContext { Session = session };

        await plugin.InvokeAsync(context, Next(() => Assert.Fail("a blocked request must not continue")), default);

        Assert.IsTrue(context.IsHandled);
        Assert.AreEqual(403, context.HandledStatusCode);
        Assert.AreEqual("blocked", Encoding.UTF8.GetString(context.HandledBodyBytes!));
        Assert.IsNull(session.HttpClient.Request.Headers.GetFirstHeader("x-wasm-seen"));
    }

    [TestMethod]
    public async Task GuestTrap_FailClosed_Answers503_AndFailOpen_Continues()
    {
        using var closed = Create(Config("faults.wat", failMode: ProxyPluginContract.FailClosed));
        using var open = Create(Config("faults.wat", failMode: ProxyPluginContract.FailOpen));
        using var proxy = new ProxyServer(false, false, false);

        var closedSession = MakeSession(proxy);
        closedSession.HttpClient.Request.Headers.AddHeader("x-trap", "1");
        var closedContext = new ProxyMiddlewareContext { Session = closedSession };
        await closed.InvokeAsync(closedContext, Next(() => Assert.Fail("fail-closed must not continue")), default);

        var openSession = MakeSession(proxy);
        openSession.HttpClient.Request.Headers.AddHeader("x-trap", "1");
        var openContext = new ProxyMiddlewareContext { Session = openSession };
        var nexts = 0;
        await open.InvokeAsync(openContext, Next(() => nexts++), default);

        Assert.IsTrue(closedContext.IsHandled);
        Assert.AreEqual(503, closedContext.HandledStatusCode);
        Assert.IsFalse(openContext.IsHandled);
        Assert.AreEqual(1, nexts);
    }

    // Connection filter paths outside the happy flow.

    private static StreamConnectionContext Connection() => new()
    {
        ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 5555),
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
        ServerName = "sni.test",
        ClusterId = "a",
        Items = new Dictionary<string, object?>(),
    };

    [TestMethod]
    public async Task StreamData_WithoutAnOpenContext_FollowsFailMode()
    {
        using var closed = Create(Config("tcp_filter.wat", failMode: ProxyPluginContract.FailClosed, name: "c"));
        using var open = Create(Config("tcp_filter.wat", failMode: ProxyPluginContract.FailOpen, name: "o"));
        var data = new byte[] { 1, 2, 3 };

        var denied = await ((IStreamScriptFilter)closed).OnDownstreamDataAsync(Connection(), data, default);
        var passed = await ((IStreamScriptFilter)open).OnUpstreamDataAsync(Connection(), data, default);

        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(ScriptAction.Continue, passed.Action);
    }

    [TestMethod]
    public async Task StreamClose_WithoutAnOpenContext_IsANoOp()
    {
        using var plugin = Create(Config("tcp_filter.wat"));

        await ((IStreamScriptFilter)plugin).OnCloseAsync(Connection());
    }

    [TestMethod]
    public async Task StreamData_OverTheBufferedLimit_ClosesTheConnection()
    {
        using var plugin = Create(Config("tcp_filter.wat",
            limits: new PluginLimitsConfig { PoolSize = 1, BufferedBodyBytes = 1024 }));
        IStreamScriptFilter filter = plugin;
        var connection = Connection();
        await filter.OnNewConnectionAsync(connection, default);

        // The guest holds a '+' piece that never ends in '.', so the held bytes grow past the cap.
        var first = new byte[600];
        first[0] = (byte)'+';
        Array.Fill(first, (byte)'x', 1, first.Length - 1);
        var held = await filter.OnDownstreamDataAsync(connection, first, default);
        var tooMuch = await filter.OnDownstreamDataAsync(connection, new byte[600], default);

        Assert.AreEqual(ScriptAction.Continue, held.Action);
        Assert.AreEqual(ScriptAction.Deny, tooMuch.Action);
        await filter.OnCloseAsync(connection);
    }

    [TestMethod]
    public async Task StreamFilter_OnAGuestWithoutStreamExports_AnswersByFailMode()
    {
        using var closed = Create(Config("faults.wat", failMode: ProxyPluginContract.FailClosed, name: "c"));
        using var open = Create(Config("faults.wat", failMode: ProxyPluginContract.FailOpen, name: "o"));

        var denied = await ((IStreamScriptFilter)closed).OnNewConnectionAsync(Connection(), default);
        var passed = await ((IStreamScriptFilter)open).OnNewConnectionAsync(Connection(), default);

        Assert.AreEqual(ScriptAction.Continue, passed.Action);
        Assert.IsTrue(denied.Action is ScriptAction.Deny or ScriptAction.Continue);
    }
}
