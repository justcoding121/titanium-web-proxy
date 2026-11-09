using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Pure service helpers that raise Sonar without spinning Avalonia or spilling bodies to disk: raw HTTP export
///     edges, status-filter composition caps, protocol frame previews, PowerShell codegen for binary bodies, and
///     AppContainer loopback probes that must not crash the testhost.
/// </summary>
[TestClass]
public class InspectorPureServiceBehaviorTests
{
    // Bug class: CONNECT tunnels write a path-style target, Host is omitted when already present, or maxBodyBytes&lt;1
    // truncates to zero instead of falling back to the documented 32 MiB cap.
    [TestMethod]
    public void SessionRawHttp_TunnelHostInjectionAndBodyCapFallback()
    {
        var tunnel = new SessionSnapshot
        {
            Id = 1,
            Method = "connect",
            IsTunnel = true,
            Url = "example.test:443",
            Host = "example.test",
            StatusCode = 200,
        };
        using (var output = new MemoryStream())
        {
            var result = SessionRawHttp.Write(tunnel, output, maxBodyBytes: 0);
            var text = Encoding.ASCII.GetString(output.ToArray());
            Assert.IsFalse(result.Truncated);
            StringAssert.StartsWith(text, "CONNECT example.test:443 HTTP/1.1\r\n");
            StringAssert.Contains(text, "Host: example.test\r\n");
            StringAssert.Contains(text, "HTTP/1.1 200 OK\r\n");
        }

        var relative = new SessionSnapshot
        {
            Id = 2,
            Method = "GET",
            Url = "/only-path",
            RequestHeadersText = "Host: already.example\nX-A: 1\r",
            StatusCode = 418,
        };
        using (var output = new MemoryStream())
        {
            _ = SessionRawHttp.Write(relative, output);
            var text = Encoding.UTF8.GetString(output.ToArray());
            StringAssert.Contains(text, "GET /only-path HTTP/1.1\r\n");
            StringAssert.Contains(text, "Host: already.example\r\n");
            Assert.IsFalse(text.Contains("Host: already.example\r\nHost:", StringComparison.Ordinal));
            StringAssert.Contains(text, "HTTP/1.1 418\r\n");
        }
    }

    // Bug class: TryCompose silently drops overflow past MaxTerms / MaxChars, or FreeText / HasClass disagree with
    // Matches so the flyout and the grid filter diverge.
    [TestMethod]
    public void StatusFilter_ComposeCapsAndClassHelpers()
    {
        Assert.IsTrue(StatusFilter.HasClass("2xx,404", '2'));
        Assert.IsFalse(StatusFilter.HasClass("2xx,404", '5'));
        Assert.AreEqual(0, StatusFilter.TermCount(null));
        Assert.AreEqual("404,error", StatusFilter.FreeText("2xx,404,error"));

        Assert.IsTrue(StatusFilter.Matches(503, "error"));
        Assert.IsTrue(StatusFilter.Matches(404, "400-499"));
        Assert.IsFalse(StatusFilter.Matches(null, "400-499"));
        Assert.IsFalse(StatusFilter.Matches(200, "499-400"));

        Assert.IsTrue(StatusFilter.TryCompose(true, false, false, false, "304, nope", out var value, out var err));
        Assert.AreEqual("2xx,304", value);
        Assert.AreEqual(StatusFilter.InvalidKey, err);

        var tooMany = string.Join(',', Enumerable.Range(100, StatusFilter.MaxTerms + 2));
        Assert.IsFalse(StatusFilter.TryCompose(false, false, false, false, tooMany, out var refused, out var tooManyKey));
        Assert.AreEqual("", refused);
        Assert.AreEqual(StatusFilter.TooManyKey, tooManyKey);

        // Four-digit codes with commas exceed MaxChars (128) while staying under MaxTerms.
        var longJoined = string.Join(',', Enumerable.Range(1000, 30));
        Assert.IsTrue(longJoined.Length > StatusFilter.MaxChars, longJoined.Length.ToString());
        Assert.IsFalse(StatusFilter.TryCompose(false, false, false, false, longJoined, out var lengthRefused, out var lengthKey));
        Assert.AreEqual("", lengthRefused);
        Assert.AreEqual(StatusFilter.TooManyKey, lengthKey);
        Assert.AreEqual("200", StatusFilter.FindValue("host:a status:200 method:GET"));
        Assert.IsNull(StatusFilter.FindValue("host:a"));
    }

    // Bug class: binary WebSocket previews omit the ellipsis past 64 bytes, ElidedFrame loses the declared length, or
    // a compressed gRPC frame is reported as uncompressed.
    [TestMethod]
    public void ProtocolFrameInspectors_BinaryPreviewElidedAndCompressedGrpc()
    {
        var binaryPayload = new byte[80];
        for (var i = 0; i < binaryPayload.Length; i++)
        {
            binaryPayload[i] = (byte)i;
        }

        var frame = new byte[2 + binaryPayload.Length];
        frame[0] = 0x82; // fin + binary
        frame[1] = (byte)binaryPayload.Length;
        Buffer.BlockCopy(binaryPayload, 0, frame, 2, binaryPayload.Length);
        var parsed = ProtocolFrameInspectors.ParseWebSocketFrames(frame);
        Assert.AreEqual(1, parsed.Count);
        Assert.AreEqual("Binary", parsed[0].Opcode);
        StringAssert.EndsWith(parsed[0].PayloadPreview, "…");

        var elided = ProtocolFrameInspectors.ElidedFrame("Server", "Binary", 1_048_576);
        Assert.AreEqual("Server", elided.Direction);
        StringAssert.Contains(elided.PayloadPreview, "1,048,576");
        StringAssert.Contains(elided.PayloadPreview, "not captured");

        var grpc = new byte[5 + 4];
        grpc[0] = 1; // compressed
        BinaryPrimitives.WriteInt32BigEndian(grpc.AsSpan(1, 4), 4);
        grpc[5] = 0xDE;
        grpc[6] = 0xAD;
        grpc[7] = 0xBE;
        grpc[8] = 0xEF;
        var grpcFrames = ProtocolFrameInspectors.ParseGrpcFrames(grpc);
        Assert.AreEqual(1, grpcFrames.Count);
        Assert.IsTrue(grpcFrames[0].Compressed);
        Assert.AreEqual(4, grpcFrames[0].Length);
        StringAssert.Contains(grpcFrames[0].HexPreview, "DEADBEEF");
    }

    // Bug class: PowerShell codegen drops binary bodies or emits them as text, so a replay of an image upload fails.
    [TestMethod]
    public void SessionRequestCodegen_PowerShell_EmitsByteArrayBodies()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/upload",
            RequestHeadersText = "Content-Type: application/octet-stream\r\n",
            RequestBodyBytes = [1, 2, 255],
        };

        var text = SessionRequestCodegen.ToPowerShell(snap);
        StringAssert.Contains(text, "Invoke-WebRequest");
        StringAssert.Contains(text, "-Body ([byte[]](1,2,255))");
        StringAssert.Contains(text, "-ContentType");
    }

    // Bug class: ImportHarFilesAsync throws on an empty/malformed HAR instead of returning zero sessions, so a multi-
    // file import aborts after the first bad file.
    [TestMethod]
    public async Task SessionArchive_ImportHarFiles_SkipsMalformedDocuments()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-har-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var empty = Path.Combine(dir, "empty.har");
            await File.WriteAllTextAsync(empty, "{}");
            var good = Path.Combine(dir, "one.har");
            await File.WriteAllTextAsync(good, """
                {"log":{"entries":[{"startedDateTime":"2024-01-01T00:00:00.000Z","request":{"method":"GET","url":"https://a/","headers":[]},"response":{"status":200,"headers":[],"content":{"size":0}},"timings":{}}]}}
                """);

            var sessions = await SessionArchive.ImportHarFilesAsync([empty, good]);
            Assert.AreEqual(1, sessions.Count);
            Assert.AreEqual("https://a/", sessions[0].Url);
            Assert.AreEqual(200, sessions[0].StatusCode);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    // Bug class: AppContainer helpers throw on unsupported hosts or invalid SIDs and take down the settings dialog.
    [TestMethod]
    public void AppContainerLoopback_InvalidSidAndUnsupportedHost_AreTolerated()
    {
        if (!AppContainerLoopback.IsSupported)
        {
            Assert.IsFalse(AppContainerLoopback.SetExemptions(["S-1-1-0"]));
            Assert.IsFalse(AppContainerLoopback.TryProbeApis(out var unsupported));
            StringAssert.Contains(unsupported, "Windows 8");
            return;
        }

        Assert.IsTrue(AppContainerLoopback.TryProbeApis(out var message), message);
        // Invalid SIDs are skipped; an empty converted set clears exemptions without throwing.
        Assert.IsTrue(AppContainerLoopback.SetExemptions(["not-a-sid", ""]));
        var list = AppContainerLoopback.ListContainers();
        Assert.IsNotNull(list);
    }
}
