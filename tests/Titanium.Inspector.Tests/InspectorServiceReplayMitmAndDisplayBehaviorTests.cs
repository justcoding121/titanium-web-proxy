using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector edges: ReplayService tunnel/edit paths, MitmBypass factory classifications,
///     Plus loader miss, display/format helpers, and Zip apply scripts.
/// </summary>
[TestClass]
public class InspectorServiceReplayMitmAndDisplayBehaviorTests
{
    // Bug class: CONNECT/empty URL still attempts HttpClient and hangs, or edited headers/body are ignored on replay.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ReplayService_RejectsTunnel_AndAppliesEditsAgainstLocalOrigin()
    {
        Assert.IsFalse((await ReplayService.ReplayAsync(new SessionSnapshot
        {
            Method = "CONNECT",
            Url = "example.com:443",
            IsTunnel = true,
        })).Ok);

        Assert.IsFalse((await ReplayService.ReplayAsync(new SessionSnapshot
        {
            Method = "GET",
            Url = "   ",
        })).Ok);

        using var backend = new HttpListener();
        var port = FreePort();
        backend.Prefixes.Add($"http://127.0.0.1:{port}/");
        backend.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await backend.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var echo = $"{ctx.Request.HttpMethod}|{ctx.Request.Headers["X-Replay"]}|{body}";
            var bytes = Encoding.UTF8.GetBytes(echo);
            ctx.Response.StatusCode = 201;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        try
        {
            var result = await ReplayService.ReplayAsync(
                new SessionSnapshot
                {
                    Method = "GET",
                    Url = $"http://127.0.0.1:{port}/orig",
                    RequestHeadersText = "Accept: text/plain\r\n",
                    RequestBodyText = "orig-body",
                },
                new ReplayRequestOptions(
                    EditedUrl: $"http://127.0.0.1:{port}/edited",
                    EditedMethod: "POST",
                    EditedBody: "edited-body",
                    EditedHeaders: "X-Replay: yes\nContent-Type: text/plain\n",
                    IgnoreServerCertificateErrors: true));
            await serve.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(201, result.StatusCode);
            StringAssert.Contains(result.ResponseBody, "POST|yes|edited-body");
        }
        finally
        {
            try
            {
                backend.Stop();
            }
            catch
            {
                // Already stopped.
            }
        }
    }

    // Bug class: factory identity/pinning hosts in the user skip list are labeled UserSkipList, so the grid
    // cannot tell a seeded Microsoft host from a custom bypass.
    [TestMethod]
    public void MitmBypass_ResolveOpaqueReason_PrefersFactoryLabelsFromSkipList()
    {
        Assert.AreEqual(OpaqueTunnelReason.None, MitmBypass.ResolveOpaqueReason(null, true, null, null));
        Assert.AreEqual(OpaqueTunnelReason.None, MitmBypass.ResolveOpaqueReason("", true, null, null));

        Assert.AreEqual(
            OpaqueTunnelReason.BuiltInIdentity,
            MitmBypass.ResolveOpaqueReason("login.live.com", true, ["login.live.com"], null));

        Assert.AreEqual(
            OpaqueTunnelReason.BuiltInPinning,
            MitmBypass.ResolveOpaqueReason("api.dropbox.com", true, ["*.dropbox.com"], null));

        Assert.AreEqual(
            OpaqueTunnelReason.UserSkipList,
            MitmBypass.ResolveOpaqueReason("custom.example", true, ["custom.example"], null));
        Assert.AreEqual(
            OpaqueTunnelReason.UserOnlyList,
            MitmBypass.ResolveOpaqueReason("other.example", true, null, ["allowed.example"]));
        Assert.AreEqual(
            OpaqueTunnelReason.None,
            MitmBypass.ResolveOpaqueReason("allowed.example", true, null, ["allowed.example"]));

        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt("example.com"));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("login.live.com", ["login.live.com"], null));
    }

    // Bug class: missing Plus dll throws instead of returning empty panels, taking down Inspector startup.
    [TestMethod]
    public void PlusInspectorLoader_MissingDll_ReturnsEmptyWithoutWarningCrash()
    {
        var panels = PlusInspectorLoader.TryLoadPanels(out var warning);
        Assert.AreEqual(0, panels.Count);
        // Either no dll (null warning) or a load failure string — never throw.
        Assert.IsTrue(warning is null || warning.Length > 0);
    }

    // Bug class: GetScheme / FormatStarted / status class helpers mis-label CONNECT tunnels and overnight starts.
    [TestMethod]
    public void SessionDisplayFormat_SchemeStartedAndStatusClassEdges()
    {
        Assert.AreEqual("", SessionDisplayFormat.GetScheme(null));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("example.com:443"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("ht tp://bad"));
        Assert.AreEqual("https", SessionDisplayFormat.GetScheme("HTTPS://Example.com/x"));
        Assert.AreEqual("wss", SessionDisplayFormat.GetScheme("wss://ws.test/"));
        Assert.AreEqual("", SessionDisplayFormat.GetScheme("toolongschemenamehere://x"));

        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(
            now.AddMinutes(-5).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture),
            SessionDisplayFormat.FormatStarted(now.AddMinutes(-5), now));
        Assert.AreEqual(
            now.AddDays(-1).ToLocalTime().ToString("MMM d, HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture),
            SessionDisplayFormat.FormatStarted(now.AddDays(-1), now));
        StringAssert.Contains(SessionDisplayFormat.FormatStartedFull(now), "2026-03-10");

        Assert.AreEqual("", SessionDisplayFormat.FormatContentType(null));
        Assert.AreEqual("application/json", SessionDisplayFormat.FormatContentType("application/json; charset=utf-8"));
        Assert.AreEqual(0.5, SessionDisplayFormat.RoundMs(0.49));

        Assert.AreEqual(HttpStatusClass.Pending, SessionDisplayFormat.GetStatusClass(null));
        Assert.AreEqual(HttpStatusClass.Informational, SessionDisplayFormat.GetStatusClass(101));
        Assert.AreEqual(HttpStatusClass.Success, SessionDisplayFormat.GetStatusClass(204));
        Assert.AreEqual(HttpStatusClass.Redirection, SessionDisplayFormat.GetStatusClass(302));
        Assert.AreEqual(HttpStatusClass.ClientError, SessionDisplayFormat.GetStatusClass(404));
        Assert.AreEqual(HttpStatusClass.ServerError, SessionDisplayFormat.GetStatusClass(503));
        Assert.AreEqual("10 MB", SessionDisplayFormat.FormatByteSize(10 * 1024 * 1024));
    }

    // Bug class: Zip apply script still emits msiexec, so portable installs try to elevate MSI.
    [TestMethod]
    public void UpdateApplyHelper_BuildWindowsScript_ZipExpandsArchive()
    {
        var script = UpdateApplyHelper.BuildWindowsScript(
            pid: 42,
            kind: UpdateApplyKind.Zip,
            packagePath: @"C:\pkg\inspector.zip",
            installDir: @"C:\Titanium\Inspector",
            relaunchPath: @"C:\Titanium\Inspector\TitaniumInspector.exe",
            version: "8.1.0",
            channel: "stable");
        StringAssert.Contains(script, "Expand-Archive");
        Assert.IsFalse(script.Contains("msiexec", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(script, "Updated Inspector to 8.1.0");

        var unix = UpdateApplyHelper.BuildUnixScript(
            7,
            "/tmp/pkg.zip",
            "/opt/titanium",
            "/opt/titanium/TitaniumInspector",
            "8.1.0",
            "beta");
        StringAssert.Contains(unix, "unzip");
        StringAssert.Contains(unix, "8.1.0");
    }

    // Bug class: comment:/mark: filters never match after import, so saved views look empty.
    [TestMethod]
    public void SessionSearch_CommentAndMarkFilters()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, Comment = "login flow", Mark = SessionMark.Orange, Url = "https://a/" },
            new SessionSnapshot { Id = 2, Comment = "other", Mark = SessionMark.Blue, Url = "https://b/" },
            new SessionSnapshot { Id = 3, Comment = "", Mark = SessionMark.None, Protocol = "HTTP/2", Url = "https://c/" },
        };
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "comment:login").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "mark:Orange").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "mark:Blue").Count());
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "-mark:none").Count());
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "protocol:HTTP/2").Count());
        Assert.AreEqual(2, SessionSearch.Filter(sessions, "-protocol:HTTP/2").Count());
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
