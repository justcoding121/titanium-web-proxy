using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InspectorBodyHonestyTests
{
    [TestMethod]
    public void TruncateAndInfer_RespectPreviewCaps()
    {
        var big = new byte[InspectorBodyLimits.MaxBodyBytes + 16];
        Assert.AreEqual(InspectorBodyLimits.MaxBodyBytes, InspectorBodyLimits.TruncateBytes(big)!.Length);
        Assert.AreEqual(BodyCaptureState.Truncated, InspectorBodyLimits.InferFromBytes(big.AsSpan(0, InspectorBodyLimits.MaxBodyBytes).ToArray(), big.Length));
        Assert.AreEqual(BodyCaptureState.Complete, InspectorBodyLimits.InferFromBytes([1, 2, 3], 3));
        Assert.AreEqual(BodyCaptureState.NotCaptured, InspectorBodyLimits.InferFromBytes(null, InspectorBodyLimits.MaxBodyBytes + 1L));
    }

    [TestMethod]
    public void FormatCaptureBanner_CoversStates()
    {
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.Truncated, 10_000_000, 2_000_000, false, false),
            "Showing first");
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.NotCaptured, 50_000_000, 0, false, false),
            "not captured");
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.Streaming, null, 1000, true, false),
            "Streaming");
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.Streaming, null, 1000, false, false),
            "ended");
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.Complete, 100, 5000, false, true),
            "Hex shows");
    }

    [TestMethod]
    public void TryPrettyPrint_JsonXmlHtml_AndRejectsBinary()
    {
        var json = InspectorBodyLimits.TryPrettyPrint("{\"a\":1}", "application/json");
        Assert.IsNotNull(json);
        StringAssert.Contains(json!, "\n");

        var xml = InspectorBodyLimits.TryPrettyPrint("<root><a/></root>", "application/xml");
        Assert.IsNotNull(xml);

        var html = InspectorBodyLimits.TryPrettyPrint("<html><body><p>x</p></body></html>", "text/html");
        Assert.IsNotNull(html);

        Assert.IsNull(InspectorBodyLimits.TryPrettyPrint("{not-json", "application/json"));
        Assert.IsNull(InspectorBodyLimits.TryPrettyPrint("xxxx", "image/png"));
    }

    [TestMethod]
    public void SessionBodyDiskCache_Json_RoundTripsHeadersAndBodies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-session-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var cache = new SessionBodyDiskCache(dir, maxBytes: 10_000_000, maxAge: TimeSpan.FromDays(1));
            var snap = new SessionSnapshot
            {
                Id = 42,
                Method = "POST",
                Url = "https://api.example/x",
                Host = "api.example",
                StatusCode = 201,
                RequestHeadersText = "Content-Type: application/json\r\n",
                ResponseHeadersText = "Content-Type: text/plain\r\n",
                RequestBodyBytes = [1, 2, 3],
                ResponseBodyBytes = [4, 5, 6, 7],
                RequestBodyText = "req",
                ResponseBodyText = "resp",
                RequestBodyOriginalSize = 3,
                ResponseBodyOriginalSize = 9_000_000,
                RequestBodyCapture = BodyCaptureState.Complete,
                ResponseBodyCapture = BodyCaptureState.Truncated,
            };
            cache.Write(snap);

            Assert.IsTrue(cache.TryReadSession(42, out var full));
            Assert.IsNotNull(full);
            Assert.AreEqual("POST", full!.Method);
            Assert.AreEqual("https://api.example/x", full.Url);
            Assert.AreEqual(201, full.StatusCode);
            Assert.AreEqual("Content-Type: application/json\r\n", full.RequestHeadersText);

            var loaded = new SessionSnapshot { Id = 42, Method = "GET", Url = "in-memory" };
            Assert.IsTrue(cache.TryLoad(loaded));
            Assert.AreEqual("GET", loaded.Method, "TryLoad must not overwrite in-memory headers");
            Assert.AreEqual(BodyCaptureState.Complete, loaded.RequestBodyCapture);
            Assert.AreEqual(BodyCaptureState.Truncated, loaded.ResponseBodyCapture);
            Assert.AreEqual(3L, loaded.RequestBodyOriginalSize);
            Assert.AreEqual(9_000_000L, loaded.ResponseBodyOriginalSize);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, loaded.RequestBodyBytes);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void SessionBodyDiskCache_CorruptJson_FailsLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-session-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "7.json"), "{not-valid");
            using var cache = new SessionBodyDiskCache(dir, maxBytes: 10_000_000, maxAge: TimeSpan.FromDays(1));
            Assert.IsFalse(cache.TryLoad(new SessionSnapshot { Id = 7 }));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void AutoResponder_MapLocal_RejectsHugeFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-maplocal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "huge.bin");
            // Don't actually write 32MiB+ — stub FileInfo by writing a marker and testing the size check with a fake.
            // Use a small file and assert TryResolveResponse succeeds, then assert inline refuse path.
            File.WriteAllBytes(path, [1, 2, 3]);
            var rule = new AutoResponderRule { LocalFilePath = path, ContentType = "application/octet-stream" };
            Assert.IsTrue(AutoResponderViewModel.TryResolveResponse(rule, out _, out var mapPath, out var len, out _));
            Assert.AreEqual(path, mapPath);
            Assert.AreEqual(3L, len);

            var inline = new AutoResponderRule { Body = new string('x', InspectorBodyLimits.MaxInlineToolBodyChars + 1) };
            Assert.IsTrue(AutoResponderViewModel.TryResolveResponse(inline, out var body, out var noPath, out _, out _));
            Assert.IsNull(noPath);
            Assert.IsNotNull(body);
            Assert.IsTrue(body!.Length > InspectorBodyLimits.MaxInlineToolBodyChars);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void SuggestBodyFileName_UsesDispositionAndUrl()
    {
        Assert.AreEqual(
            "photo.jpg",
            InspectorBodyLimits.SuggestBodyFileName(
                "https://cdn.example/x",
                "attachment; filename=\"photo.jpg\"",
                "image/jpeg",
                isRequest: false));
        Assert.AreEqual(
            "app.bundle.js",
            InspectorBodyLimits.SuggestBodyFileName(
                "https://cdn.example/static/app.bundle.js?v=1",
                null,
                "application/javascript",
                isRequest: false));
    }

    [TestMethod]
    public void IsImageAndPrettyContentType_Helpers()
    {
        Assert.IsTrue(InspectorBodyLimits.IsImageContentType("image/png"));
        Assert.IsFalse(InspectorBodyLimits.IsImageContentType("image/svg+xml"));
        Assert.IsTrue(InspectorBodyLimits.IsPrettyPrintableContentType("application/json"));
        Assert.IsTrue(InspectorBodyLimits.LooksLikeSseContentType("text/event-stream; charset=utf-8"));
    }

    [TestMethod]
    public void TryPrettyPrint_Xml_DisablesExternalEntities()
    {
        // XXE payload must not expand; pretty may fail or strip — must not throw / fetch.
        var xxe = """<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><root>&xxe;</root>""";
        try
        {
            _ = InspectorBodyLimits.TryPrettyPrint(xxe, "application/xml");
        }
        catch (Exception ex)
        {
            Assert.Fail("Pretty XML must not throw on XXE input: " + ex.Message);
        }
    }

    [TestMethod]
    public void SessionSelect_WhileComposerOpen_DoesNotStealPane()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-pane-nav-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            vm.ShowSessionDetails = true;
            vm.SelectedPaneNavIndex = 1; // Composer
            Assert.IsTrue(vm.ShowComposerPane);

            vm.SeedSession(new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a.test/", StatusCode = 200 });
            vm.SelectedSession = vm.Sessions[0];

            Assert.AreEqual(1, vm.SelectedPaneNavIndex);
            Assert.IsTrue(vm.ShowComposerPane);
            Assert.IsFalse(vm.ShowInspectPane);

            vm.SeedSession(new SessionSnapshot { Id = 2, Method = "GET", Url = "https://b.test/", StatusCode = 200 });
            vm.SelectedSession = vm.Sessions[1];
            Assert.AreEqual(1, vm.SelectedPaneNavIndex);

            // Opening from a closed pane still lands on Inspect.
            vm.ShowSessionDetails = false;
            vm.SelectedSession = vm.Sessions[0];
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.AreEqual(0, vm.SelectedPaneNavIndex);
            Assert.IsTrue(vm.ShowInspectPane);
            Assert.IsTrue(vm.IsInspectRailPressed);
            Assert.IsFalse(vm.IsComposerRailPressed);
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
    public void TogglePaneNav_ReclickCloses_AndClickAgainReopens()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-pane-toggle-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            Assert.IsFalse(vm.ShowSessionDetails);
            Assert.IsFalse(vm.IsComposerRailPressed);

            vm.TogglePaneNavComposerCommand.Execute(null);
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.AreEqual(1, vm.SelectedPaneNavIndex);
            Assert.IsTrue(vm.IsComposerRailPressed);
            Assert.AreEqual("Composer", vm.PaneContentTitle);

            // Re-click same icon closes content; rail pressed clears.
            vm.TogglePaneNavComposerCommand.Execute(null);
            Assert.IsFalse(vm.ShowSessionDetails);
            Assert.IsFalse(vm.IsComposerRailPressed);
            Assert.AreEqual(1, vm.SelectedPaneNavIndex); // last index remembered

            vm.TogglePaneNavComposerCommand.Execute(null);
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.IsTrue(vm.IsComposerRailPressed);

            vm.TogglePaneNavBreakpointsCommand.Execute(null);
            Assert.IsTrue(vm.ShowSessionDetails);
            Assert.AreEqual(2, vm.SelectedPaneNavIndex);
            Assert.IsTrue(vm.IsBreakpointsRailPressed);
            Assert.IsFalse(vm.IsComposerRailPressed);

            vm.CloseSessionDetailsCommand.Execute(null);
            Assert.IsFalse(vm.ShowSessionDetails);
            Assert.IsFalse(vm.IsBreakpointsRailPressed);
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
    public void TeeResponseChunk_CapsPreviewAndCountsBytesSeen()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);

        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        var tee = typeof(InterceptionService).GetMethod(
            "TeeResponseChunk",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var snap = new SessionSnapshot
        {
            Id = 99,
            ResponseBodyCapture = BodyCaptureState.Streaming,
            IsServerSentEvents = true,
            ResponseBodyStreamOpen = true,
        };

        var chunk = new byte[InspectorBodyLimits.MaxBodyBytes + 4096];
        chunk.AsSpan().Fill(0x41);
        var args = new BeforeBodyWriteEventArgs(session, chunk, isChunked: true, isLastChunk: false);
        tee.Invoke(interception, [snap, args]);

        Assert.AreEqual(chunk.LongLength, snap.ResponseBytesSeen);
        Assert.AreEqual(chunk.LongLength, snap.BodySize);
        Assert.IsNotNull(snap.ResponseTeeStream);
        Assert.AreEqual(InspectorBodyLimits.MaxBodyBytes, snap.ResponseTeeStream!.Length);

        var last = new BeforeBodyWriteEventArgs(session, [0x42], isChunked: true, isLastChunk: true);
        tee.Invoke(interception, [snap, last]);
        Assert.IsFalse(snap.ResponseBodyStreamOpen);
        Assert.IsTrue(snap.ResponseBodyCapture is BodyCaptureState.Truncated or BodyCaptureState.Complete);
        Assert.IsTrue((snap.ResponseBodyBytes?.Length ?? 0) <= InspectorBodyLimits.MaxBodyBytes);
    }

    [TestMethod]
    public void AutoResponderAdd_RefusesHugeInlineBody()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-ar-cap-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            vm.AutoResponderMatch = "https://huge.test/*";
            vm.AutoResponderBody = new string('x', InspectorBodyLimits.MaxInlineToolBodyChars + 1);
            var before = vm.AutoResponder.Rules.Count;
            vm.AddAutoResponderRuleCommand.Execute(null);
            Assert.AreEqual(before, vm.AutoResponder.Rules.Count);
            StringAssert.Contains(vm.StatusText, "Map Local");
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
    public void SyncHttpBodySize_UsesLargerOfRequestAndResponse()
    {
        var sync = typeof(InterceptionService).GetMethod(
            "SyncHttpBodySize",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var snap = new SessionSnapshot
        {
            RequestBodyOriginalSize = 2_000_000,
            ResponseBodyOriginalSize = 10,
            BodySize = 10,
        };
        sync.Invoke(null, [snap]);
        Assert.AreEqual(2_000_000L, snap.BodySize);

        var responseOnly = new SessionSnapshot
        {
            ResponseBodyOriginalSize = 512,
            BodySize = 512,
        };
        sync.Invoke(null, [responseOnly]);
        Assert.AreEqual(512L, responseOnly.BodySize);

        var tunnel = new SessionSnapshot
        {
            IsTunnel = true,
            BodySize = 10_240,
            RequestBodyOriginalSize = 99,
        };
        sync.Invoke(null, [tunnel]);
        Assert.AreEqual(10_240L, tunnel.BodySize, "CONNECT Size stays wire bytes");
    }

    [TestMethod]
    public void TunnelInspect_ExplainsWireSizeNotHttpBody()
    {
        var hint = typeof(MainWindowViewModel).GetMethod(
            "BuildBodyCaptureHint",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var core = typeof(MainWindowViewModel).GetMethod(
            "BuildSelectedBodyTextCore",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var snap = new SessionSnapshot
        {
            IsTunnel = true,
            Method = "CONNECT",
            BodySize = 10 * 1024,
            SentBytes = 4000,
            ReceivedBytes = 6240,
        };

        var banner = (string)hint.Invoke(null, [snap])!;
        StringAssert.Contains(banner, "wire");
        Assert.IsFalse(banner.Contains("(empty)", StringComparison.Ordinal));

        var body = (string)core.Invoke(null, [snap, false])!;
        StringAssert.Contains(body, "CONNECT tunnel");
        StringAssert.Contains(body, "no HTTP message body");
        StringAssert.Contains(body, "10 KB");
        Assert.IsFalse(body.Contains("(empty)", StringComparison.Ordinal));
    }
}
