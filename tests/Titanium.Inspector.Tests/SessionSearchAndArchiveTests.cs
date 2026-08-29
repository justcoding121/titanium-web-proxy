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
        Assert.IsTrue(SessionSearch.Matches(sessions[1], "method:GET is:ws"));
        Assert.IsFalse(SessionSearch.Matches(sessions[0], "method:GET is:ws"));
        Assert.IsTrue(SessionSearch.Matches(sessions[0], null));
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

    [TestMethod]
    public void ProtocolFrameInspectors_ParseMultipart_NullAndNonMultipart()
    {
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseMultipart(null, "x"u8.ToArray()).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseMultipart("multipart/form-data", null).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseMultipart("text/plain", "x"u8.ToArray()).Count);
        Assert.AreEqual(0, ProtocolFrameInspectors.ParseMultipart("multipart/form-data", "x"u8.ToArray()).Count);
    }

    [TestMethod]
    public void ProtocolFrameInspectors_ParseMultipart_NamedPartsWithBoundary()
    {
        const string boundary = "----twp";
        var body = string.Join("\r\n",
            $"--{boundary}",
            "Content-Disposition: form-data; name=\"field1\"",
            "Content-Type: text/plain",
            "",
            "hello",
            $"--{boundary}",
            "Content-Disposition: form-data; name=\"file\"; filename=\"a.txt\"",
            "",
            "bytes",
            $"--{boundary}--",
            "");
        var parts = ProtocolFrameInspectors.ParseMultipart(
            $"multipart/form-data; boundary=\"{boundary}\"",
            System.Text.Encoding.UTF8.GetBytes(body));
        Assert.AreEqual(2, parts.Count);
        Assert.AreEqual("field1", parts[0].Name);
        Assert.AreEqual("text/plain", parts[0].ContentType);
        StringAssert.Contains(parts[0].Preview!, "hello");
        Assert.AreEqual("file", parts[1].Name);
    }

    [TestMethod]
    public void ProtocolFrameInspectors_ParseMultipart_NoHeaderSeparatorAndDispositionWithoutName()
    {
        const string boundary = "b";
        var noSep = $"--{boundary}\r\njust-content\r\n--{boundary}--\r\n";
        var parts = ProtocolFrameInspectors.ParseMultipart(
            $"multipart/mixed; boundary={boundary}",
            System.Text.Encoding.UTF8.GetBytes(noSep));
        Assert.AreEqual(1, parts.Count);
        Assert.IsNull(parts[0].Name);
        StringAssert.Contains(parts[0].Preview!, "just-content");

        // Disposition without name= (avoid filename= which contains the substring name=")
        var noName = string.Join("\r\n",
            $"--{boundary}",
            "Content-Disposition: form-data",
            "",
            "data",
            $"--{boundary}--",
            "");
        var unnamed = ProtocolFrameInspectors.ParseMultipart(
            $"multipart/form-data; boundary={boundary}",
            System.Text.Encoding.UTF8.GetBytes(noName));
        Assert.AreEqual(1, unnamed.Count);
        Assert.IsNull(unnamed[0].Name);
    }

    [TestMethod]
    public void SessionSearch_TokenArms_StatusHostBodyIsAndBareUrl()
    {
        var s = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/v1/items?q=1",
            StatusCode = 201,
            IsGrpc = true,
            IsTunnel = false,
            IsMultipart = true,
            RequestBodyText = "needle-req",
            ResponseBodyText = "other",
        };

        Assert.IsTrue(SessionSearch.Matches(s, "status:201"));
        Assert.IsFalse(SessionSearch.Matches(s, "status:404"));
        Assert.IsTrue(SessionSearch.Matches(s, "host:api.example"));
        Assert.IsTrue(SessionSearch.Matches(s, "url:items"));
        Assert.IsTrue(SessionSearch.Matches(s, "body:needle"));
        Assert.IsFalse(SessionSearch.Matches(s, "body:missing"));
        Assert.IsTrue(SessionSearch.Matches(s, "is:grpc"));
        Assert.IsTrue(SessionSearch.Matches(s, "is:multipart"));
        Assert.IsFalse(SessionSearch.Matches(s, "is:tunnel"));
        Assert.AreEqual(s.IsWebSocket, SessionSearch.Matches(s, "is:websocket"));
        Assert.IsTrue(SessionSearch.Matches(s, "is:unknownflag")); // unknown is: → true
        Assert.IsTrue(SessionSearch.Matches(s, "api.example")); // bare token → url
        Assert.IsTrue(SessionSearch.Matches(s, "weirdkey:api.example")); // unknown key → url contains value

        var ws = new SessionSnapshot { Url = "wss://x", IsWebSocket = true };
        Assert.IsTrue(SessionSearch.Matches(ws, "is:ws"));
        Assert.IsTrue(SessionSearch.Matches(ws, "is:websocket"));

        var tunnel = new SessionSnapshot { Url = "https://t", IsTunnel = true };
        Assert.IsTrue(SessionSearch.Matches(tunnel, "is:tunnel"));
    }
}
