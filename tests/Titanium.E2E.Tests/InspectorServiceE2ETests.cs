using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

[TestClass]
public class InspectorServiceE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task Mitm_HttpClient_ThroughExplicitProxy_CapturesHttp()
    {
        using var origin = new EchoOrigin();
        var recorder = new RecordingSystemProxyController();
        using var interception = new InterceptionService(recorder);
        SessionSnapshot? captured = null;
        SessionSnapshot? updated = null;
        interception.SessionCaptured += (_, s) => captured = s;
        interception.SessionUpdated += (_, s) => updated = s;

        await interception.StartAsync(IPAddress.Loopback, 0);
        Assert.IsTrue(interception.IsRunning);
        Assert.IsTrue(interception.BoundPort > 0);
        Assert.AreEqual(System.Net.Quic.QuicListener.IsSupported, interception.Http3Enabled);
        var proxyPort = interception.BoundPort;

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await http.GetAsync($"http://127.0.0.1:{origin.Port}/mitm-e2e", cts.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (captured is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(captured, "SessionCaptured should fire");
        Assert.AreEqual(1, captured!.Id, "First published session should be Id 1");
        StringAssert.Contains(captured.Url, "mitm-e2e");

        deadline = DateTime.UtcNow.AddSeconds(5);
        while ((updated?.StatusCode is null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }

        Assert.AreEqual(200, updated?.StatusCode);
        Assert.IsTrue(updated!.DurationMs is >= 0, "Duration should be filled after the HTTP response.");
        StringAssert.Contains(updated.Protocol, "→");
        interception.Stop();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Mitm_HttpClient_DecryptsHttps_LocalOrigin()
    {
        using var origin = new HttpsEchoOrigin();
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.IgnoreServerCertificateErrors = true;

        SessionSnapshot? captured = null;
        SessionSnapshot? updated = null;
        interception.SessionCaptured += (_, s) => captured = s;
        interception.SessionUpdated += (_, s) => updated = s;

        await interception.StartAsync(IPAddress.Loopback, 0);
        Assert.IsTrue(interception.BoundPort > 0);
        Assert.IsFalse(string.IsNullOrEmpty(interception.RootCertificate?.Thumbprint));
        interception.DecryptHttps = true;
        var proxyPort = interception.BoundPort;

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var response = await http.GetAsync($"https://127.0.0.1:{origin.Port}/https-mitm", cts.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(cts.Token), "https-mitm");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((captured is null || updated?.StatusCode is null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(captured);
        StringAssert.Contains(captured!.Url, "https-mitm");
        Assert.AreEqual(200, updated?.StatusCode);
        interception.EnsureShutdown();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task EnsureShutdown_RestoresSystemProxy_ViaSeam()
    {
        var recorder = new RecordingSystemProxyController();
        using var interception = new InterceptionService(recorder);
        await interception.StartAsync(IPAddress.Loopback, 0);
        Assert.IsTrue(interception.SetSystemProxy(true));
        Assert.AreEqual(1, recorder.SetCount);
        interception.EnsureShutdown();
        Assert.IsTrue(recorder.RestoreCount >= 1);
        interception.EnsureShutdown();
        Assert.IsTrue(recorder.RestoreCount >= 1);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task AutoResponder_InjectsBeforeOrigin()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.AutoResponder = new AutoResponderViewModel { Enabled = true };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            MatchUrl = "*ar-e2e*",
            StatusCode = 418,
            Body = "teapot",
            ContentType = "text/plain",
            Enabled = true,
        });

        await interception.StartAsync(IPAddress.Loopback, 0);
        var proxyPort = interception.BoundPort;
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        // Use an unresolvable host so a missed AutoResponder match fails fast via timeout rather than hanging DNS.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await http.GetAsync("http://127.0.0.1:9/ar-e2e", cts.Token);
        Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        Assert.AreEqual("teapot", await response.Content.ReadAsStringAsync(cts.Token));
        interception.Stop();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task SystemProxy_WhenNotRunning_IsNoOp()
    {
        var recorder = new RecordingSystemProxyController();
        using var interception = new InterceptionService(recorder);
        Assert.IsFalse(interception.SetSystemProxy(true));
        Assert.AreEqual(0, recorder.SetCount);
        await Task.CompletedTask;
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task SystemProxy_WhenRunning_UsesControllerSeam()
    {
        var recorder = new RecordingSystemProxyController();
        using var interception = new InterceptionService(recorder);
        await interception.StartAsync(IPAddress.Loopback, 0);
        Assert.IsTrue(interception.SetSystemProxy(true));
        Assert.AreEqual(1, recorder.SetCount);
        Assert.IsTrue(recorder.LastEnabled);
        Assert.IsTrue(interception.SetSystemProxy(false));
        Assert.AreEqual(1, recorder.RestoreCount);
        interception.Stop();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Composer_Replay_CreatesSessionRow()
    {
        using var origin = new EchoOrigin();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = origin.BaseUrl + "composer",
            RequestHeadersText = "Accept: */*\n",
        };
        var result = await ReplayService.ReplayAsync(snap);
        Assert.IsTrue(result.Ok, result.Message);
        Assert.AreEqual(200, result.StatusCode);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Har_RoundTrip_PreservesUrl()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-e2e-" + Guid.NewGuid().ToString("N") + ".har");
        try
        {
            var sessions = new List<SessionSnapshot>
            {
                new()
                {
                    Method = "GET",
                    Url = "https://example.test/har",
                    StatusCode = 200,
                    DurationMs = 12,
                    RequestBodyText = "q",
                    ResponseBodyText = "r",
                },
            };
            await SessionArchive.ExportHarAsync(sessions, path);
            var imported = await SessionArchive.ImportHarAsync(path);
            Assert.AreEqual(1, imported.Count);
            Assert.AreEqual("https://example.test/har", imported[0].Url);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
