using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionSearch negative status, SessionRequestCodegen fetch
///     with byte bodies, and SessionRawHttp status without reason phrase.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave22BehaviorTests
{
    private static readonly List<SessionSnapshot> Sample =
    [
        new()
        {
            Id = 1,
            Method = "GET",
            Url = "https://api.example/ok",
            Host = "api.example",
            StatusCode = 200,
        },
        new()
        {
            Id = 2,
            Method = "POST",
            Url = "https://api.example/fail",
            Host = "api.example",
            StatusCode = 502,
        },
    ];

    [TestMethod]
    public void SessionSearch_NegativeStatus_ExcludesMatchingRows()
    {
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "-status:502").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "-status:5xx").Count());
        Assert.IsFalse(SessionSearch.Matches(Sample[1], "-status:5xx"));
        Assert.IsTrue(SessionSearch.Matches(Sample[0], "-status:5xx"));
    }

    [TestMethod]
    public void SessionRequestCodegen_ToFetch_UsesRequestBodyBytes()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://codegen.example/submit",
            RequestHeadersText = "Content-Type: application/json\r\n",
            RequestBodyBytes = Encoding.UTF8.GetBytes("{\"a\":1}"),
        };
        Assert.IsTrue(SessionRequestCodegen.TryGetRequest(snap, out var captured));
        CollectionAssert.AreEquivalent(
            new[] { new { Name = "Content-Type", Value = "application/json" } },
            captured.Headers.Select(h => new { h.Name, h.Value }).ToArray());
        var fetch = SessionRequestCodegen.ToFetch(snap);
        StringAssert.Contains(fetch, "codegen.example");
        StringAssert.Contains(fetch, "POST");
        StringAssert.Contains(fetch, "\"body\":");
        StringAssert.Contains(fetch, "\\\"a\\\":1");
    }

    [TestMethod]
    public void SessionRawHttp_UnknownStatusCode_OmitsReasonPhrase()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://n.example/",
            StatusCode = 599,
            ResponseHeadersText = "X-R: 1\r\n",
        };
        var result = SessionRawHttp.Write(snap, output);
        Assert.IsFalse(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "HTTP/1.1 599\r\n");
        Assert.IsFalse(text.Contains("599 OK", StringComparison.Ordinal));
    }
}
