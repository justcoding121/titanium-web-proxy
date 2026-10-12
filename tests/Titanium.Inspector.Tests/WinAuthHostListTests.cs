using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Inspector.Tests;

[TestClass]
public class WinAuthHostListTests
{
    [TestMethod]
    public void EmptyList_CompilesToNull()
    {
        Assert.IsTrue(WinAuthHostList.TryCompile(null, out var matcher, out var error));
        Assert.IsNull(matcher);
        Assert.IsNull(error);
        Assert.IsTrue(WinAuthHostList.TryCompile(" \n\t", out matcher, out error));
        Assert.IsNull(matcher);
        Assert.IsNull(WinAuthHostList.Validate(""));
    }

    [TestMethod]
    public void Patterns_MatchHostAndSubdomainOnly()
    {
        Assert.IsTrue(WinAuthHostList.TryCompile(
            "intranet.example\n*.corp.example\n[::1]\n",
            out var matcher,
            out var error));
        Assert.IsNull(error);
        Assert.IsNotNull(matcher);
        Assert.IsTrue(matcher!.Matches("INTRANET.example."));
        Assert.IsTrue(matcher.Matches("::1"));
        Assert.IsTrue(matcher.Matches("api.corp.example"));
        Assert.IsFalse(matcher.Matches("corp.example"));
        Assert.IsFalse(matcher.Matches("notcorp.example"));
        Assert.IsFalse(matcher.Matches(null));
        Assert.IsFalse(matcher.Matches(""));
    }

    [TestMethod]
    public void Idn_IsComparedInAscii()
    {
        Assert.IsTrue(WinAuthHostList.TryCompile("münchen.example", out var matcher, out var error));
        Assert.IsNull(error);
        Assert.IsTrue(matcher!.Matches("xn--mnchen-3ya.example"));
        Assert.IsTrue(matcher.Matches("MÜNCHEN.example"));
    }

    [TestMethod]
    public void RejectsStar_Scheme_Port_Path_AndTooMany()
    {
        Assert.AreEqual(WinAuthHostList.Invalid, WinAuthHostList.Validate("*"));
        Assert.AreEqual(WinAuthHostList.Invalid, WinAuthHostList.Validate("*."));
        Assert.AreEqual(WinAuthHostList.Invalid, WinAuthHostList.Validate("http://intranet.example"));
        Assert.AreEqual(WinAuthHostList.Invalid, WinAuthHostList.Validate("intranet.example/path"));
        Assert.AreEqual(WinAuthHostList.Invalid, WinAuthHostList.Validate("intranet.example:443"));
        Assert.AreEqual(WinAuthHostList.Invalid, WinAuthHostList.Validate("*.*.example"));
        var many = string.Join('\n', Enumerable.Range(0, 201).Select(i => "h" + i + ".example"));
        Assert.AreEqual(WinAuthHostList.TooMany, WinAuthHostList.Validate(many));
        Assert.IsNull(WinAuthHostList.Validate(string.Join('\n', Enumerable.Range(0, 200).Select(i => "h" + i + ".example"))));
    }

    [TestMethod]
    public void ApplyTo_SetsMatcher_AndLeavesServerFlagOff()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        var settings = new InspectorSettings { WinAuthHosts = "*.corp.example" };
        InspectorNetworkSettings.ApplyTo(interception, settings);
        Assert.AreEqual("*.corp.example", interception.WinAuthHosts);

        var matches = typeof(InterceptionService)
            .GetMethod("MatchesWinAuthHost", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsTrue((bool)matches.Invoke(interception, ["a.corp.example"])!);
        Assert.IsFalse((bool)matches.Invoke(interception, ["corp.example"])!);

        interception.WinAuthHosts = "";
        Assert.IsFalse((bool)matches.Invoke(interception, ["a.corp.example"])!);
    }

    [TestMethod]
    public async Task StartedProxy_StillHasServerWinAuthOff_WhenHostsAreListed()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            WinAuthHosts = "127.0.0.1",
        };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            var proxy = typeof(InterceptionService)
                .GetField("_proxy", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(interception);
            Assert.IsFalse((bool)proxy!.GetType().GetProperty("EnableWinAuth")!.GetValue(proxy)!);
        }
        finally
        {
            interception.Stop();
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    public async Task UnlistedHost_HasNoWinAuthEvent_ListedHost_DoesOnWindows()
    {
        using var origin = new HttpListener();
        var port = FreePort();
        origin.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            origin.Start();
        }
        catch (HttpListenerException ex)
        {
            Assert.Inconclusive("Loopback listener is not available: " + ex.Message);
            return;
        }

        var sawAuthorization = 0;
        _ = Task.Run(async () =>
        {
            while (origin.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await origin.GetContextAsync();
                }
                catch
                {
                    return;
                }

                if (ctx.Request.Headers["Authorization"] is not null)
                {
                    Interlocked.Increment(ref sawAuthorization);
                }

                var bytes = "ok"u8.ToArray();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            Capturing = true,
            WinAuthHosts = "auth.example",
        };
        var finished = new List<SessionSnapshot>();
        var gate = new object();
        interception.SessionUpdated += (_, snap) =>
        {
            if (snap.DurationMs is null)
            {
                return;
            }

            lock (gate)
            {
                finished.Add(snap);
            }
        };

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
                UseDefaultCredentials = false,
            })
            { Timeout = TimeSpan.FromSeconds(8) };

            using var post = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/unlisted")
            {
                Content = new StringContent("hello-body"),
            };
            var unlistedResponse = await http.SendAsync(post);
            Assert.AreEqual(HttpStatusCode.OK, unlistedResponse.StatusCode);
            var unlisted = await Wait(finished, gate, "/unlisted");
            Assert.IsTrue(unlisted.Events is null || unlisted.Events.All(n => n.Key != "stats.event.winAuth"));
            Assert.AreEqual(0, Volatile.Read(ref sawAuthorization));

            interception.WinAuthHosts = "localhost\n127.0.0.1";
            var listedResponse = await http.GetAsync($"http://localhost:{port}/listed");
            Assert.AreEqual(HttpStatusCode.OK, listedResponse.StatusCode);
            var listed = await Wait(finished, gate, "/listed");
            if (OperatingSystem.IsWindows())
            {
                // Duration is filled in BeforeResponse; the event list is attached in AfterResponse.
                var eventDeadline = DateTime.UtcNow.AddSeconds(3);
                while (listed.Events is null && DateTime.UtcNow < eventDeadline)
                {
                    await Task.Delay(30);
                }

                Assert.IsNotNull(listed.Events, (listed.Host ?? "") + " " + listed.Url);
                Assert.IsTrue(listed.Events!.Any(n => n.Key == "stats.event.winAuth"));
                Assert.AreEqual(1, Distinct(finished, "/listed"));
            }
            else
            {
                Assert.IsTrue(listed.Events is null || listed.Events.All(n => n.Key != "stats.event.winAuth"));
            }
        }
        finally
        {
            interception.EnsureShutdown();
            origin.Stop();
        }
    }

    [TestMethod]
    public async Task NtlmListener_OneFinalRow_AndTwoConcurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows only");
            return;
        }

        using var origin = new HttpListener();
        var port = FreePort();
        origin.Prefixes.Add($"http://localhost:{port}/");
        origin.AuthenticationSchemes = AuthenticationSchemes.Ntlm;
        try
        {
            origin.Start();
        }
        catch (HttpListenerException ex)
        {
            Assert.Inconclusive("Loopback NTLM is not available: " + ex.Message);
            return;
        }

        _ = Task.Run(async () =>
        {
            while (origin.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await origin.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var ok = ctx.User?.Identity?.IsAuthenticated == true;
                var bytes = Encoding.UTF8.GetBytes(ok ? "ok" : "no");
                ctx.Response.StatusCode = ok ? 200 : 401;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            Capturing = true,
            WinAuthHosts = "localhost",
        };
        var finished = new List<SessionSnapshot>();
        var gate = new object();
        interception.SessionUpdated += (_, snap) =>
        {
            if (snap.DurationMs is null || snap.IsTunnel)
            {
                return;
            }

            lock (gate)
            {
                finished.Add(snap);
            }
        };

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
                UseDefaultCredentials = false,
            })
            { Timeout = TimeSpan.FromSeconds(15) };

            var first = await http.GetAsync($"http://localhost:{port}/ntlm");
            if (first.StatusCode == HttpStatusCode.Unauthorized)
            {
                Assert.Inconclusive("Loopback NTLM did not accept the Inspector account");
                return;
            }

            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            await Wait(finished, gate, "/ntlm");
            Assert.AreEqual(1, Distinct(finished, "/ntlm"));
            Assert.AreEqual(200, finished.Last(s => s.Url.Contains("/ntlm", StringComparison.Ordinal)).StatusCode);

            var pair = await Task.WhenAll(
                http.GetAsync($"http://localhost:{port}/pair-a"),
                http.GetAsync($"http://localhost:{port}/pair-b"));
            Assert.IsTrue(pair.All(r => r.StatusCode == HttpStatusCode.OK));
            await Wait(finished, gate, "/pair-a");
            await Wait(finished, gate, "/pair-b");
            Assert.AreEqual(1, Distinct(finished, "/pair-a"));
            Assert.AreEqual(1, Distinct(finished, "/pair-b"));
        }
        finally
        {
            interception.EnsureShutdown();
            origin.Stop();
        }
    }

    [TestMethod]
    public void Tunnel_ListedHost_ForcesHttp11_UnlistedStaysAuto()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            WinAuthHosts = "*.corp.example",
        };
        var beforeTunnel = typeof(InterceptionService).GetMethod(
            "OnBeforeTunnelConnect", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var listed = new TunnelConnectSessionEventArgs(
            proxy, endPoint, new ConnectRequest("api.corp.example:443".GetByteString()), clientStream, cts);
        ((Task)beforeTunnel.Invoke(interception, [interception, listed])!).GetAwaiter().GetResult();
        Assert.AreEqual(UpstreamHttpProtocol.Http11, listed.UpstreamHttpProtocol);

        var apex = new TunnelConnectSessionEventArgs(
            proxy, endPoint, new ConnectRequest("corp.example:443".GetByteString()), clientStream, cts);
        ((Task)beforeTunnel.Invoke(interception, [interception, apex])!).GetAwaiter().GetResult();
        Assert.AreEqual(UpstreamHttpProtocol.Auto, apex.UpstreamHttpProtocol);
    }

    [TestMethod]
    public async Task MapRemote_AuthenticatesTheHostAfterRewrite()
    {
        using var origin = new HttpListener();
        var port = FreePort();
        origin.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            origin.Start();
        }
        catch (HttpListenerException ex)
        {
            Assert.Inconclusive("Loopback listener is not available: " + ex.Message);
            return;
        }

        _ = Task.Run(async () =>
        {
            while (origin.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await origin.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var bytes = "ok"u8.ToArray();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var map = new MapRemoteViewModel { Enabled = true };
        map.AddRule(new MapRemoteRule
        {
            Enabled = true,
            MatchUrl = $"http://listed.example:{port}/*",
            TargetUrl = $"http://localhost:{port}/*",
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            Capturing = true,
            MapRemote = map,
            WinAuthHosts = "listed.example",
        };
        var finished = new List<SessionSnapshot>();
        var gate = new object();
        interception.SessionUpdated += (_, snap) =>
        {
            if (snap.DurationMs is null)
            {
                return;
            }

            lock (gate)
            {
                finished.Add(snap);
            }
        };

        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
                UseDefaultCredentials = false,
            })
            { Timeout = TimeSpan.FromSeconds(8) };

            var off = await http.GetAsync($"http://listed.example:{port}/rewritten-off");
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode);
            var offSnap = await Wait(finished, gate, "/rewritten-off");
            Assert.IsTrue(offSnap.Events is null || offSnap.Events.All(n => n.Key != "stats.event.winAuth"),
                "Credentials must follow the host after Map Remote, not the host the browser typed.");

            interception.WinAuthHosts = "localhost";
            var on = await http.GetAsync($"http://listed.example:{port}/rewritten-on");
            Assert.AreEqual(HttpStatusCode.OK, on.StatusCode);
            var onSnap = await Wait(finished, gate, "/rewritten-on");
            if (OperatingSystem.IsWindows())
            {
                var eventDeadline = DateTime.UtcNow.AddSeconds(3);
                while (onSnap.Events is null && DateTime.UtcNow < eventDeadline)
                {
                    await Task.Delay(30);
                }

                Assert.IsNotNull(onSnap.Events);
                Assert.IsTrue(onSnap.Events!.Any(n => n.Key == "stats.event.winAuth"));
            }
        }
        finally
        {
            interception.EnsureShutdown();
            origin.Stop();
        }
    }

    private static int Distinct(List<SessionSnapshot> finished, string path)
    {
        lock (finished)
        {
            return finished.Where(s => s.Url?.Contains(path, StringComparison.Ordinal) == true)
                .Select(s => s.Id)
                .Distinct()
                .Count();
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<SessionSnapshot> Wait(List<SessionSnapshot> finished, object gate, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            SessionSnapshot? found;
            lock (gate)
            {
                found = finished.LastOrDefault(snap => snap.Url?.Contains(path, StringComparison.Ordinal) == true);
            }

            if (found is not null)
            {
                return found;
            }

            await Task.Delay(40);
        }

        Assert.Fail("No finished session for " + path);
        return null!;
    }
}
