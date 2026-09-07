using System.Net;
using System.Text;
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
    public async Task MapLocal_InjectsFileBodyBeforeOrigin()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-map-local-e2e-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "from-disk-map-local");
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            interception.AutoResponder = new AutoResponderViewModel { Enabled = true };
            interception.AutoResponder.Rules.Add(new AutoResponderRule
            {
                MatchUrl = "*map-local-e2e*",
                StatusCode = 209,
                Body = "should-not-use",
                ContentType = "text/plain",
                LocalFilePath = path,
                Enabled = true,
            });

            await interception.StartAsync(IPAddress.Loopback, 0);
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await http.GetAsync("http://127.0.0.1:9/map-local-e2e", cts.Token);
            Assert.AreEqual((HttpStatusCode)209, response.StatusCode);
            Assert.AreEqual("from-disk-map-local", await response.Content.ReadAsStringAsync(cts.Token));
            interception.Stop();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task MapRemote_RewritesUrlBeforeOrigin()
    {
        using var origin = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf);
            var body = System.Text.Encoding.UTF8.GetBytes("remote-ok");
            var resp = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length +
                       "\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(resp));
            await stream.WriteAsync(body);
        });

        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.MapRemote = new MapRemoteViewModel { Enabled = true };
        interception.MapRemote.Rules.Add(new MapRemoteRule
        {
            MatchUrl = "*map-remote-e2e*",
            TargetUrl = $"http://127.0.0.1:{originPort}/ok",
            Enabled = true,
        });
        await interception.StartAsync(IPAddress.Loopback, 0);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.GetAsync("http://127.0.0.1:9/map-remote-e2e");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("remote-ok", await response.Content.ReadAsStringAsync());
        interception.Stop();
        origin.Stop();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task GraphQlOperation_AutoResponder_MatchesNamedOp()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        interception.AutoResponder = new AutoResponderViewModel { Enabled = true };
        interception.AutoResponder.Rules.Add(new AutoResponderRule
        {
            MatchUrl = "*graphql*",
            StatusCode = 200,
            Body = "{\"data\":{\"e2e\":true}}",
            ContentType = "application/json",
            GraphQlOperationName = "E2eOp",
            Enabled = true,
        });
        await interception.StartAsync(IPAddress.Loopback, 0);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{interception.BoundPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var content = new StringContent(
            """{"operationName":"E2eOp","query":"query E2eOp { e2e }"}""",
            Encoding.UTF8,
            "application/json");
        var response = await http.PostAsync("http://127.0.0.1:9/graphql", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "\"e2e\":true");
        interception.Stop();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task CopyAsCurl_GeneratesCurlAndFetchFromSession()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-copy-as-e2e-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            var snap = new SessionSnapshot
            {
                Id = 42,
                Method = "PATCH",
                Url = "https://e2e.example/resource",
                Host = "e2e.example",
                RequestHeadersText = "Accept: application/json\nContent-Type: application/json\n",
                RequestBodyText = "{\"id\":42}",
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);

            Assert.IsTrue(vm.CanCopyAsCurl);
            Assert.IsTrue(vm.TryBuildCopyAsCurl(out var curl));
            StringAssert.Contains(curl, "curl 'https://e2e.example/resource'");
            StringAssert.Contains(curl, "-X 'PATCH'");
            StringAssert.Contains(curl, "--data-binary '{\"id\":42}'");

            Assert.IsTrue(vm.TryBuildCopyAsFetch(out var fetch));
            StringAssert.Contains(fetch, "fetch(\"https://e2e.example/resource\"");
            StringAssert.Contains(fetch, "\"method\": \"PATCH\"");
            StringAssert.Contains(fetch, "\"body\": \"{\\\"id\\\":42}\"");

            vm.CopyAsCurlCommand.Execute(null);
            await Task.Delay(150);
            StringAssert.Contains(vm.StatusText, "Copied as curl");

            vm.CopyAsFetchCommand.Execute(null);
            await Task.Delay(150);
            StringAssert.Contains(vm.StatusText, "Copied as fetch");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task SessionDiff_ComparesTwoSelectedSessions()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-session-diff-e2e-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception);

            var a = new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://e2e.example/d",
                StatusCode = 200,
                RequestHeadersText = "Accept: text/plain\n",
                ResponseBodyText = "one",
            };
            var b = new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://e2e.example/d",
                StatusCode = 201,
                RequestHeadersText = "Accept: application/json\n",
                ResponseBodyText = "two",
            };
            vm.SeedSession(a);
            vm.SeedSession(b);
            vm.SetSelectedSessions([a, b]);

            Assert.IsTrue(vm.CanDiffSessions);
            Assert.IsTrue(vm.TryBuildSessionDiff(out var diff));
            Assert.IsTrue(diff.HasDifferences);
            StringAssert.Contains(diff.Text, "Status: 200 → 201");
            StringAssert.Contains(diff.Text, "- Accept: text/plain");
            StringAssert.Contains(diff.Text, "+ Accept: application/json");
            StringAssert.Contains(diff.Text, "- one");
            StringAssert.Contains(diff.Text, "+ two");

            vm.DiffSessionsCommand.Execute(null);
            await Task.Delay(150);
            StringAssert.Contains(vm.SessionDiffText, "- one");
            StringAssert.Contains(vm.StatusText, "Session Diff");
            Assert.IsTrue(vm.CanShowSessionDiffTab);
            Assert.AreEqual(3, vm.SelectedInspectTabIndex);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public void SessionDiff_ComparesTwoSeededSessions()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-diff-e2e-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));
            var left = new SessionSnapshot { Id = 1, Method = "GET", Url = "https://e2e/diff", StatusCode = 200, ResponseBodyText = "one" };
            var right = new SessionSnapshot { Id = 2, Method = "GET", Url = "https://e2e/diff", StatusCode = 201, ResponseBodyText = "two" };
            vm.SeedSession(left);
            vm.SeedSession(right);
            vm.SetSelectedSessions([left, right]);
            Assert.IsTrue(vm.TryBuildSessionDiff(out var diff));
            Assert.IsTrue(diff.HasDifferences);
            StringAssert.Contains(diff.Text, "Status: 200 → 201");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
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
