using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     <see cref="HttpWebClient.ClientHttpVersion" /> / <see cref="HttpWebClient.OriginHttpVersion" /> expose the two
///     protocol legs of an exchange that a translation bridge rewrites on <c>Request</c> / <c>Response</c>.
///     They must be invisible on a native exchange and must never leak into the next keep-alive request.
/// </summary>
[TestClass]
public class HttpWebClientLegVersionTests
{
    private static HttpWebClient Create(Version requestVersion)
    {
        var request = new Request { HttpVersion = requestVersion };
        return new HttpWebClient(null, request, new Lazy<int>(() => 0));
    }

    [TestMethod]
    public void Native_FallsBackToTheRawVersions()
    {
        var client = Create(HttpHeader.Version11);
        client.Response = new Response { HttpVersion = HttpHeader.Version11 };

        Assert.AreEqual(HttpHeader.Version11, client.ClientHttpVersion);
        Assert.AreEqual(HttpHeader.Version11, client.OriginHttpVersion);
        Assert.IsFalse(client.HasRecordedOriginHttpVersion);
    }

    [TestMethod]
    public void OriginLeg_BeforeAnyResponse_IsUnknown_AndDoesNotMaterializeOne()
    {
        var client = Create(HttpHeader.Version11);

        Assert.AreEqual(HttpHeader.VersionUnknown, client.OriginHttpVersion);
        Assert.IsFalse(client.HasResponse, "Reading the origin leg must not allocate a Response.");
    }

    [TestMethod]
    public void RecordedLegs_WinOverTheRawVersions_AndTrackLaterRewrites()
    {
        var client = Create(HttpHeader.Version20);
        client.Response = new Response { HttpVersion = HttpHeader.Version11 };

        client.RecordClientHttpVersion(HttpHeader.Version20);
        client.RecordOriginHttpVersion(HttpHeader.Version11);

        // The bridge rewrites the wire-facing versions; the recorded legs do not move.
        client.Request.HttpVersion = HttpHeader.Version11;
        client.Response.HttpVersion = HttpHeader.Version20;

        Assert.AreEqual(HttpHeader.Version20, client.ClientHttpVersion);
        Assert.AreEqual(HttpHeader.Version11, client.OriginHttpVersion);
        Assert.IsTrue(client.HasRecordedOriginHttpVersion);
    }

    [TestMethod]
    [DataRow(1, 0)]
    [DataRow(1, 1)]
    [DataRow(2, 0)]
    [DataRow(3, 0)]
    public void RecordedVersions_RoundTripExactly(int major, int minor)
    {
        var client = Create(HttpHeader.VersionUnknown);
        var version = new Version(major, minor);

        client.RecordClientHttpVersion(version);
        client.RecordOriginHttpVersion(version);

        Assert.AreEqual(version, client.ClientHttpVersion);
        Assert.AreEqual(version, client.OriginHttpVersion);
    }

    [TestMethod]
    public void AVersionTheProxyDoesNotSpeak_IsNotRecorded_AndFallsBackToTheRawValue()
    {
        var client = Create(HttpHeader.Version11);
        client.Response = new Response { HttpVersion = HttpHeader.Version11 };

        client.RecordClientHttpVersion(new Version(9, 9));
        client.RecordOriginHttpVersion(new Version(2, 0, 7));

        Assert.AreEqual(HttpHeader.Version11, client.ClientHttpVersion);
        Assert.AreEqual(HttpHeader.Version11, client.OriginHttpVersion);
        Assert.IsFalse(client.HasRecordedOriginHttpVersion);
    }

    [TestMethod]
    public void ResetForKeepAlive_ForgetsTheLegs_SoTheNextRequestDoesNotInheritThem()
    {
        var client = Create(HttpHeader.Version11);
        client.Response = new Response { HttpVersion = HttpHeader.Version11 };
        client.RecordClientHttpVersion(HttpHeader.Version20);
        client.RecordOriginHttpVersion(HttpHeader.Version30);

        client.ResetForKeepAlive();
        client.Request.HttpVersion = HttpHeader.Version11;
        client.Response = new Response { HttpVersion = HttpHeader.Version11 };

        Assert.AreEqual(HttpHeader.Version11, client.ClientHttpVersion);
        Assert.AreEqual(HttpHeader.Version11, client.OriginHttpVersion);
        Assert.IsFalse(client.HasRecordedOriginHttpVersion);
    }
}
