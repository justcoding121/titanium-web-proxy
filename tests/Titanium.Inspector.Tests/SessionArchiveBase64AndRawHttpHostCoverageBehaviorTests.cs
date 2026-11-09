using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Inspector leftovers toward 85%: SessionArchive bad _inspector base64 is skipped, and
///     SessionRawHttp falls back to the URL host when Host is blank.
/// </summary>
[TestClass]
public class SessionArchiveBase64AndRawHttpHostCoverageBehaviorTests
{
    [TestMethod]
    public void SessionArchive_FromHarEntry_SkipsInvalidInspectorBase64()
    {
        var har = """
            {
              "log": {
                "entries": [
                  {
                    "startedDateTime": "2026-03-09T10:00:00.000Z",
                    "request": {
                      "method": "GET",
                      "url": "https://archive.example/path",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "queryString": [],
                      "cookies": [],
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "response": {
                      "status": 200,
                      "statusText": "OK",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "cookies": [],
                      "content": { "size": 0, "mimeType": "text/plain", "text": "" },
                      "redirectURL": "",
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "cache": {},
                    "timings": { "send": 0, "wait": 1, "receive": 0 },
                    "_inspector": {
                      "upstreamRequestBodyBase64": "!!!not-base64!!!",
                      "upstreamResponseBodyBase64": "",
                      "isTranscoded": true
                    }
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(har);
        var sessions = SessionArchive.ParseHarDocument(doc.RootElement, startId: 9);
        Assert.AreEqual(1, sessions.Count);
        var snap = sessions[0];
        Assert.AreEqual(9, snap.Id);
        Assert.IsNull(snap.UpstreamRequestBodyBytes);
        Assert.IsNull(snap.UpstreamResponseBodyBytes);
        Assert.IsTrue(snap.IsTranscoded);
        Assert.AreEqual("https://archive.example/path", snap.Url);
    }

    [TestMethod]
    public void SessionRawHttp_UsesUrlHost_WhenHostBlank()
    {
        var snap = new SessionSnapshot
        {
            Id = 11,
            Method = "GET",
            Host = "   ",
            Url = "https://from-url.example/item",
            StatusCode = 200,
            ResponseHeadersText = "Content-Type: text/plain\r\n",
            ResponseBodyText = "ok",
        };

        using var output = new MemoryStream();
        _ = SessionRawHttp.Write(snap, output);
        var text = Encoding.ASCII.GetString(output.ToArray());
        StringAssert.Contains(text, "GET /item HTTP/1.1");
        StringAssert.Contains(text, "Host: from-url.example");
        StringAssert.Contains(text, "HTTP/1.1 200");
    }
}
