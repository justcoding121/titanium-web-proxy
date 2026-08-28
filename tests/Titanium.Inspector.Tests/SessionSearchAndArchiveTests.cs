using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionSearchAndArchiveTests
{
    [TestMethod]
    public void Filter_ByMethodAndIsWs()
    {
        var sessions = new[]
        {
            new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a/x", IsWebSocket = false },
            new SessionSnapshot { Id = 2, Method = "GET", Url = "https://a/ws", IsWebSocket = true },
            new SessionSnapshot { Id = 3, Method = "POST", Url = "https://a/y", IsWebSocket = false },
        };

        var filtered = SessionSearch.Filter(sessions, "method:GET is:ws").ToList();
        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual(2, filtered[0].Id);
    }

    [TestMethod]
    public async Task NativeArchive_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twp-insp-{Guid.NewGuid():N}.zip");
        try
        {
            var sessions = new List<SessionSnapshot>
            {
                new() { Id = 9, Method = "GET", Url = "https://example/" },
            };
            await SessionArchive.ExportNativeArchiveAsync(sessions, path);
            var imported = await SessionArchive.ImportNativeArchiveAsync(path);
            Assert.AreEqual(1, imported.Count);
            Assert.AreEqual(9, imported[0].Id);
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
    public async Task Har_RoundTrip_PreservesPostDataAndTimings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twp-har-{Guid.NewGuid():N}.har");
        try
        {
            var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var sessions = new List<SessionSnapshot>
            {
                new()
                {
                    Id = 1,
                    Method = "POST",
                    Url = "https://api.example/v1?q=1",
                    StartedUtc = started,
                    StatusCode = 201,
                    RequestHeadersText = "Content-Type: application/json\r\n",
                    ResponseHeadersText = "Content-Type: application/json\r\n",
                    RequestBodyText = """{"a":1}""",
                    ResponseBodyText = """{"ok":true}""",
                    ContentType = "application/json",
                    DurationMs = 100,
                    TtfbMs = 40,
                },
            };

            await SessionArchive.ExportHarAsync(sessions, path);
            var imported = await SessionArchive.ImportHarAsync(path);
            Assert.AreEqual(1, imported.Count);
            Assert.AreEqual("POST", imported[0].Method);
            Assert.AreEqual("https://api.example/v1?q=1", imported[0].Url);
            Assert.AreEqual(201, imported[0].StatusCode);
            Assert.AreEqual("""{"a":1}""", imported[0].RequestBodyText);
            Assert.AreEqual("""{"ok":true}""", imported[0].ResponseBodyText);
            Assert.AreEqual(100, imported[0].DurationMs);
            Assert.AreEqual(40, imported[0].TtfbMs);
            Assert.AreEqual("application/json", imported[0].ContentType);
            Assert.AreEqual(started, imported[0].StartedUtc);
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
    public void ProtocolFrameInspectors_ParseWebSocketFrames_Smoke()
    {
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseWebSocketFrames(null).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseWebSocketFrames([]).Count);

        var frames = ProtocolFrameInspectors.ParseWebSocketFrames("hello-ws"u8.ToArray());
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual("Text", frames[0].Opcode);
        StringAssert.Contains(frames[0].PayloadPreview, "hello-ws");
    }

    [TestMethod]
    public void ProtocolFrameInspectors_ParseGrpcFrames_Smoke()
    {
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseGrpcFrames(null).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseGrpcFrames(new byte[3]).Count);

        var payload = new byte[5 + 4];
        payload[0] = 0; // uncompressed
        payload[1] = 0;
        payload[2] = 0;
        payload[3] = 0;
        payload[4] = 4; // length
        payload[5] = 0xDE;
        payload[6] = 0xAD;
        payload[7] = 0xBE;
        payload[8] = 0xEF;

        var frames = ProtocolFrameInspectors.ParseGrpcFrames(payload);
        Assert.AreEqual(1, frames.Count);
        Assert.IsFalse(frames[0].Compressed);
        Assert.AreEqual(4, frames[0].Length);
        Assert.AreEqual("DEADBEEF", frames[0].HexPreview);
    }
}
