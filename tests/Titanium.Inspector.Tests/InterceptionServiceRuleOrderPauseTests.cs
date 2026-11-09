using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// AutoResponder / Map Remote match order and breakpoint pause resume/abort through a live proxy.
/// </summary>
[TestClass]
public class InterceptionServiceRuleOrderPauseTests
{
    [TestMethod]
    public async Task AutoResponder_FirstEnabledRuleWins_OverLaterRules()
    {
        using var origin = StartOrigin("origin-body");
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = false,
            Capturing = false,
            AutoResponder = new AutoResponderViewModel { Enabled = true },
        };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            Enabled = true,
            MatchUrl = "*win-first*",
            StatusCode = 418,
            ContentType = "text/plain",
            Body = "teapot",
        });
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            Enabled = true,
            MatchUrl = "*",
            StatusCode = 200,
            ContentType = "text/plain",
            Body = "wildcard",
        });

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort);
            var specific = await http.GetAsync("http://example.test/win-first");
            Assert.AreEqual((HttpStatusCode)418, specific.StatusCode);
            Assert.AreEqual("teapot", await specific.Content.ReadAsStringAsync());

            var fallback = await http.GetAsync("http://example.test/other");
            Assert.AreEqual(HttpStatusCode.OK, fallback.StatusCode);
            Assert.AreEqual("wildcard", await fallback.Content.ReadAsStringAsync());
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task MapRemote_Rewrites_WhenAutoResponderDoesNotMatch()
    {
        using var origin = StartOrigin("rewritten-body");
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = false,
            Capturing = false,
            AutoResponder = new AutoResponderViewModel
            {
                Enabled = true,
            },
            MapRemote = new MapRemoteViewModel { Enabled = true },
        };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            Enabled = true,
            MatchUrl = "*never*",
            StatusCode = 204,
            Body = "unused",
        });
        interception.MapRemote.Rules.Add(new MapRemoteRule
        {
            Enabled = true,
            MatchUrl = "*remap*",
            TargetUrl = $"http://127.0.0.1:{origin.Port}/remap",
        });

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort);
            var resp = await http.GetAsync("http://example.test/remap-here");
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            Assert.AreEqual("rewritten-body", await resp.Content.ReadAsStringAsync());
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task AutoResponder_ToggleEnabled_AndBreakpointDisableWhilePaused()
    {
        using var origin = StartOrigin("live");
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = false,
            Capturing = true,
            AutoResponder = new AutoResponderViewModel { Enabled = false },
            Breakpoints = new BreakpointViewModel { Enabled = false, UrlFilter = "*" },
        };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            Enabled = true,
            MatchUrl = "*",
            StatusCode = 204,
            Body = "blocked",
        });

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort);

            var passthrough = await http.GetAsync($"http://127.0.0.1:{origin.Port}/passthrough");
            Assert.AreEqual(HttpStatusCode.OK, passthrough.StatusCode);
            Assert.AreEqual("live", await passthrough.Content.ReadAsStringAsync());

            interception.AutoResponder.Enabled = true;
            var mapped = await http.GetAsync("http://example.test/mapped");
            Assert.AreEqual(HttpStatusCode.NoContent, mapped.StatusCode);

            // AutoResponder answers before breakpoints; turn it off so the paused request
            // can still reach the live origin after breakpoints are disabled mid-pause.
            interception.AutoResponder.Enabled = false;
            interception.Breakpoints!.Enabled = true;
            var pending = http.GetAsync($"http://127.0.0.1:{origin.Port}/pause-me");
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (interception.Breakpoints!.Active is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.Enabled = false;
            var resumed = await pending;
            Assert.AreEqual(HttpStatusCode.OK, resumed.StatusCode);
            Assert.IsNull(interception.Breakpoints.Active);
            StringAssert.Contains(interception.Breakpoints.LastOverflowMessage, "Breakpoints disabled");
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task Breakpoint_AbortThenResume_ViaProxy()
    {
        using var origin = StartOrigin("ok");
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = false,
            Capturing = true,
            Breakpoints = new BreakpointViewModel { Enabled = true, UrlFilter = "*" },
        };

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort);
            var pendingAbort = http.GetAsync($"http://127.0.0.1:{origin.Port}/abort");
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (interception.Breakpoints!.Active is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.Abort();
            var aborted = await pendingAbort;
            Assert.AreEqual(HttpStatusCode.Forbidden, aborted.StatusCode);

            var pendingResume = http.GetAsync($"http://127.0.0.1:{origin.Port}/resume");
            deadline = DateTime.UtcNow.AddSeconds(4);
            while (interception.Breakpoints.Active is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.IsNotNull(interception.Breakpoints.Active);
            interception.Breakpoints.Continue();
            var resumed = await pendingResume;
            Assert.AreEqual(HttpStatusCode.OK, resumed.StatusCode);
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    private static HttpClient ProxyClient(int port)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static OriginHost StartOrigin(string body)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                try
                {
                    var ctx = await http.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch
                {
                    return;
                }
            }
        });
        return new OriginHost(http, port);
    }

    private sealed class OriginHost : IDisposable
    {
        private readonly HttpListener _listener;

        public int Port { get; }

        public OriginHost(HttpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
        }
    }
}
