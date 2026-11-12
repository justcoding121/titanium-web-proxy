using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Fat remaining SessionRawHttp / HarBodyCopy / ProgressiveDelivery edges still thin on develop Sonar:
///     reason-phrase table, JSON string escapes in spilled HAR bodies, and progressive encode failures.
/// </summary>
[TestClass]
public class SessionRawHttpReasonAndHarEscapeBehaviorTests
{
    [TestMethod]
    [DataRow(201, "Created")]
    [DataRow(204, "No Content")]
    [DataRow(301, "Moved Permanently")]
    [DataRow(302, "Found")]
    [DataRow(304, "Not Modified")]
    [DataRow(400, "Bad Request")]
    [DataRow(401, "Unauthorized")]
    [DataRow(403, "Forbidden")]
    [DataRow(404, "Not Found")]
    [DataRow(500, "Internal Server Error")]
    [DataRow(502, "Bad Gateway")]
    [DataRow(503, "Service Unavailable")]
    [DataRow(418, "HTTP/1.1 418")] // unknown reason → status line without phrase still emitted
    public void SessionRawHttp_WritesKnownAndUnknownReasonPhrases(int status, string needle)
    {
        var snap = new SessionSnapshot
        {
            Id = status,
            Method = "GET",
            Url = "https://reason.example/",
            StatusCode = status,
            ResponseHeadersText = "Content-Type: text/plain\r\n",
            ResponseBodyText = "x",
        };

        using var output = new MemoryStream();
        _ = SessionRawHttp.Write(snap, output);
        var text = Encoding.ASCII.GetString(output.ToArray());
        StringAssert.Contains(text, needle);
    }

    // Bug class: spilled HAR bodies with JSON escapes are corrupted or throw instead of decoding escapes.
    [TestMethod]
    public void HarBodyCopy_DecodesJsonStringEscapesInPostData()
    {
        // Includes \", \\, \/, \b, \f, \n, \r, \t, and \u0048 ("H").
        const string json =
            "{\"request\":{\"postData\":{\"text\":\"say \\\"hi\\\" \\\\ \\/ \\b\\f\\n\\r\\t\\u0048\"}}}";
        using var har = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var output = new MemoryStream();

        var truncated = HarBodyCopy.CopyProperty(har, "postData", "text", output, 1024, encoding: null);

        Assert.IsFalse(truncated);
        var decoded = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(decoded, "say \"hi\"");
        StringAssert.Contains(decoded, "H");
        Assert.IsTrue(decoded.Contains('\\') || decoded.Contains('/'), decoded);
    }

    // Bug class: a multi-token Content-Encoding (gzip, br) is treated as identity and paced, so clients cannot decode.
    [TestMethod]
    public void ProgressiveDelivery_MultiTokenContentEncoding_RecordsEncodingSkip()
    {
        var plain = Encoding.UTF8.GetBytes(new string('z', ProgressiveDelivery.MinWireBytes));
        var before = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Encoding);

        var multi = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            StatusDescription = "OK",
            Body = plain,
        };
        multi.Headers.AddHeader(new HttpHeader("Content-Encoding", "gzip, br"));
        multi.Headers.AddHeader(new HttpHeader("Content-Length", plain.Length.ToString()));

        Assert.IsNull(ProgressiveDelivery.TryBuild(multi, new Request { Method = "GET" }, plain, plain.Length, false, false));
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Encoding) > before);
        _ = ProgressiveDelivery.ProgressiveCount;
        _ = ProgressiveDelivery.FallbackCount((ProgressiveSkipReason)1234);
    }

    [TestMethod]
    public void MacPreferredLanguage_NonMac_ReturnsNull()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("Non-mac early return.");
        }

        Assert.IsNull(MacPreferredLanguage.TryGetFirst());
    }
}
