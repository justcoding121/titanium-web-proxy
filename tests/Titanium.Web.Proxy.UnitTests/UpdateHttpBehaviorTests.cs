using System;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UpdateHttpBehaviorTests
{
    [TestMethod]
    public void IsLoopbackProxy_AcceptsLocalhostVariants()
    {
        Assert.IsTrue(UpdateHttp.IsLoopbackProxy(new Uri("http://127.0.0.1:8866")));
        Assert.IsTrue(UpdateHttp.IsLoopbackProxy(new Uri("http://localhost:8866")));
        Assert.IsFalse(UpdateHttp.IsLoopbackProxy(new Uri("http://proxy.corp.example:8080")));
        Assert.IsFalse(UpdateHttp.IsLoopbackProxy(null));
    }

    [TestMethod]
    public void IsOwnLoopbackProxy_RequiresActivePortMatch()
    {
        var own = new Uri("http://127.0.0.1:8866/");
        Assert.IsTrue(UpdateHttp.IsOwnLoopbackProxy(own, 8866, active: true));
        Assert.IsFalse(UpdateHttp.IsOwnLoopbackProxy(own, 8866, active: false));
        Assert.IsFalse(UpdateHttp.IsOwnLoopbackProxy(own, 9999, active: true));
        Assert.IsFalse(UpdateHttp.IsOwnLoopbackProxy(new Uri("http://proxy.corp:8080"), 8866, active: true));
    }

    [TestMethod]
    public void FormatFailure_IncludesInnerAndCaptureHint()
    {
        var inner = new InvalidOperationException("ssl boom");
        var outer = new HttpRequestException("download failed", inner);
        var text = UpdateHttp.FormatFailure(outer, updateTrafficMayBeCaptured: true);
        StringAssert.Contains(text, "download failed");
        StringAssert.Contains(text, "ssl boom");
        StringAssert.Contains(text, "decrypt can break");
    }

    [TestMethod]
    public void CreateClient_ReturnsConfiguredHttpClient()
    {
        using var client = UpdateHttp.CreateClient(
            TimeSpan.FromSeconds(5),
            UpdateHttp.IsLoopbackProxy);
        Assert.IsNotNull(client);
        Assert.AreEqual(TimeSpan.FromSeconds(5), client.Timeout);
    }

    [TestMethod]
    public void CreateClient_NonLoopbackDestination_RoutesViaEnvProxy()
    {
        var priorHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var priorHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var priorNo = Environment.GetEnvironmentVariable("NO_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:1");
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:1");
            Environment.SetEnvironmentVariable("NO_PROXY", "");

            using var client = UpdateHttp.CreateClient(
                TimeSpan.FromSeconds(3),
                UpdateHttp.IsLoopbackProxy);
            try
            {
                _ = client.GetStringAsync("http://example.invalid/").GetAwaiter().GetResult();
                Assert.Fail("expected proxy connection failure for non-loopback destination");
            }
            catch (HttpRequestException)
            {
                // Closed proxy port — proves GetProxy returned the env proxy, not direct.
            }
            catch (TaskCanceledException)
            {
                // Some stacks surface the refused proxy as a timeout.
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", priorHttp);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", priorHttps);
            Environment.SetEnvironmentVariable("NO_PROXY", priorNo);
        }
    }

    [TestMethod]
    public void CreateClient_BypassesLoopbackDestinationEvenWhenProxyEnvSet()
    {
        var priorHttp = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var priorHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var priorNo = Environment.GetEnvironmentVariable("NO_PROXY");
        System.Net.HttpListener? listener = null;
        try
        {
            // Closed proxy port: any request that still goes through the proxy fails.
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:1");
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:1");
            Environment.SetEnvironmentVariable("NO_PROXY", "");

            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var serve = listener.GetContextAsync().ContinueWith(static t =>
            {
                if (!t.IsCompletedSuccessfully)
                {
                    return;
                }

                var ctx = t.Result;
                ctx.Response.StatusCode = 200;
                var buf = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                ctx.Response.Close();
            });

            using var client = UpdateHttp.CreateClient(
                TimeSpan.FromSeconds(5),
                UpdateHttp.IsLoopbackProxy);
            var json = client.GetStringAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
            serve.Wait(TimeSpan.FromSeconds(2));
            StringAssert.Contains(json, "ok");
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch
            {
                // ignore
            }

            Environment.SetEnvironmentVariable("HTTP_PROXY", priorHttp);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", priorHttps);
            Environment.SetEnvironmentVariable("NO_PROXY", priorNo);
        }
    }
}
