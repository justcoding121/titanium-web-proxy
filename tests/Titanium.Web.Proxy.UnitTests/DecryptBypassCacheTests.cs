using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class DecryptBypassCacheTests
{
    [TestMethod]
    public void RecordFailure_ReachesThreshold_ThenShouldBypass()
    {
        var cache = new DecryptBypassCache { StrikeThreshold = 2, Ttl = TimeSpan.FromMinutes(30) };
        Assert.IsFalse(cache.ShouldBypass("www.expedia.com"));
        Assert.IsFalse(cache.RecordFailure("www.expedia.com"));
        Assert.IsFalse(cache.ShouldBypass("www.expedia.com"));
        Assert.IsTrue(cache.RecordFailure("www.expedia.com"));
        Assert.IsTrue(cache.ShouldBypass("www.expedia.com"));
        Assert.IsTrue(cache.ShouldBypass("WWW.EXPEDIA.COM"));
    }

    [TestMethod]
    public void MarkBypassed_ForcesActive()
    {
        var cache = new DecryptBypassCache { StrikeThreshold = 5 };
        cache.MarkBypassed("cdn.example.com");
        Assert.IsTrue(cache.ShouldBypass("cdn.example.com"));
    }

    [TestMethod]
    public void Remove_And_Clear()
    {
        var cache = new DecryptBypassCache { StrikeThreshold = 1 };
        cache.RecordFailure("a.example.com");
        cache.RecordFailure("b.example.com");
        Assert.IsTrue(cache.Remove("a.example.com"));
        Assert.IsFalse(cache.ShouldBypass("a.example.com"));
        cache.Clear();
        Assert.IsFalse(cache.ShouldBypass("b.example.com"));
    }

    [TestMethod]
    public void Lru_EvictsOldestWhenOverCapacity()
    {
        var cache = new DecryptBypassCache { MaxEntries = 2, StrikeThreshold = 1, Ttl = TimeSpan.FromHours(1) };
        cache.RecordFailure("one.example.com");
        cache.RecordFailure("two.example.com");
        // Touch two so one is more recent
        Assert.IsTrue(cache.ShouldBypass("two.example.com"));
        cache.RecordFailure("three.example.com");
        Assert.IsFalse(cache.ShouldBypass("one.example.com"));
        Assert.IsTrue(cache.ShouldBypass("two.example.com") || cache.ShouldBypass("three.example.com"));
        Assert.IsTrue(cache.Count <= 2);
    }

    [TestMethod]
    public void Normalize_StripsPort()
    {
        Assert.AreEqual("example.com", DecryptBypassCache.Normalize("example.com:443"));
        Assert.AreEqual("example.com", DecryptBypassCache.Normalize("EXAMPLE.COM"));
    }

    [TestMethod]
    public void IsBypassActive_DoesNotRequireShouldBypassHit()
    {
        var cache = new DecryptBypassCache { StrikeThreshold = 1 };
        Assert.IsFalse(cache.IsBypassActive("x.example.com"));
        cache.RecordFailure("x.example.com");
        Assert.IsTrue(cache.IsBypassActive("x.example.com"));
        Assert.IsTrue(cache.ShouldBypass("x.example.com"));
    }

    [TestMethod]
    public void DecryptFailureLearning_IgnoresAlpnAndSocket()
    {
        var alpn = new AuthenticationException("No common application protocol exists");
        Assert.IsFalse(DecryptFailureLearning.IsLearnableOriginTlsFailure(alpn));

        var socket = new SocketException();
        Assert.IsFalse(DecryptFailureLearning.IsLearnableOriginTlsFailure(socket));

        var auth = new AuthenticationException("The message received was unexpected or badly formatted.");
        Assert.IsTrue(DecryptFailureLearning.IsLearnableOriginTlsFailure(auth));
    }
}

[TestClass]
public class DecryptFailureBypassProxyApiTests
{
    [TestMethod]
    public void FlagOff_DoesNotRecordOrBypass()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.EnableDecryptFailureBypass);
        Assert.IsFalse(proxy.TryRecordDecryptFailure("www.example.com",
            new AuthenticationException("fail")));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("www.example.com"));
    }

    [TestMethod]
    public void FlagOn_RecordsAndBypassesAfterThreshold()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 2
        };
        var ex = new AuthenticationException("handshake failed");
        Assert.IsFalse(proxy.TryRecordDecryptFailure("host.example.com", ex));
        Assert.IsTrue(proxy.TryRecordDecryptFailure("host.example.com", ex));
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("host.example.com"));
        Assert.IsTrue(proxy.GetDecryptFailureBypassEntries().Count >= 1);
        Assert.IsTrue(proxy.RemoveDecryptFailureBypass("host.example.com"));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("host.example.com"));
    }

    [TestMethod]
    public void ForceBypass_MarksImmediately()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 10
        };
        Assert.IsTrue(proxy.TryRecordDecryptFailure("probe.example.com", null, forceBypass: true));
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("probe.example.com"));
    }

    [TestMethod]
    public void HttpStatus_RequiresTwoStrikes_IgnoresSyntheticAndNonBlock()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 2
        };

        Assert.IsFalse(proxy.TryRecordDecryptFailureFromHttpStatus("www.expedia.com", 403));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("www.expedia.com"));
        Assert.IsTrue(proxy.TryRecordDecryptFailureFromHttpStatus("www.expedia.com", 429));
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("www.expedia.com"));

        Assert.IsFalse(proxy.TryRecordDecryptFailureFromHttpStatus("ok.example.com", 200));
        Assert.IsFalse(proxy.TryRecordDecryptFailureFromHttpStatus("synth.example.com", 403, isSynthetic: true));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("synth.example.com"));
    }

    [TestMethod]
    public void HttpStatus_Document_ForceImmediate_ActivatesOnFirst()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 2
        };
        Assert.IsTrue(proxy.TryRecordDecryptFailureFromHttpStatus("www.expedia.com", 403,
            forceImmediate: true));
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("www.expedia.com"));
    }

    [TestMethod]
    public void IsDocumentNavigation_SecFetchDestAndAccept()
    {
        var doc = new Request();
        doc.Headers.AddHeader("Sec-Fetch-Dest", "document");
        Assert.IsTrue(DecryptFailureLearning.IsDocumentNavigation(doc));

        var xhr = new Request();
        xhr.Headers.AddHeader("Sec-Fetch-Dest", "empty");
        Assert.IsFalse(DecryptFailureLearning.IsDocumentNavigation(xhr));

        var acceptHtml = new Request();
        acceptHtml.Headers.AddHeader(KnownHeaders.Accept, "text/html,application/xhtml+xml");
        Assert.IsTrue(DecryptFailureLearning.IsDocumentNavigation(acceptHtml));

        var acceptJson = new Request();
        acceptJson.Headers.AddHeader(KnownHeaders.Accept, "application/json");
        Assert.IsFalse(DecryptFailureLearning.IsDocumentNavigation(acceptJson));
    }

    [TestMethod]
    public void BuildMetaRefreshHtml_ContainsRefreshAndEncodedUrl()
    {
        var html = DecryptFailureLearning.BuildMetaRefreshHtml("https://www.expedia.com/a?b=1&c=2");
        Assert.IsTrue(html.Contains("meta http-equiv=\"refresh\"", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("content=\"0;url=", StringComparison.Ordinal));
        Assert.IsTrue(html.Contains("https://www.expedia.com/a?b=1&amp;c=2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IsSafeMetaRefreshUrl_AllowsHttpHttpsOnly()
    {
        Assert.IsTrue(DecryptFailureLearning.IsSafeMetaRefreshUrl("https://www.expedia.com/"));
        Assert.IsTrue(DecryptFailureLearning.IsSafeMetaRefreshUrl("http://example.com/x"));
        Assert.IsFalse(DecryptFailureLearning.IsSafeMetaRefreshUrl("javascript:alert(1)"));
        Assert.IsFalse(DecryptFailureLearning.IsSafeMetaRefreshUrl("data:text/html,hi"));
        Assert.IsFalse(DecryptFailureLearning.IsSafeMetaRefreshUrl("/relative"));
        Assert.IsFalse(DecryptFailureLearning.IsSafeMetaRefreshUrl(null));
    }

    [TestMethod]
    public void HttpStatus_DoesNotReRaiseAfterAlreadyActive()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 1
        };

        var raises = 0;
        proxy.DecryptFailureBypassChanged += (_, _) => raises++;

        Assert.IsTrue(proxy.TryRecordDecryptFailureFromHttpStatus("once.example.com", 403));
        Assert.AreEqual(1, raises);
        Assert.IsTrue(proxy.TryRecordDecryptFailureFromHttpStatus("once.example.com", 429));
        Assert.AreEqual(1, raises);
    }

    [TestMethod]
    public void ForceBypass_DoesNotReRaiseWhenAlreadyActive()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 10
        };

        var raises = 0;
        proxy.DecryptFailureBypassChanged += (_, _) => raises++;

        Assert.IsTrue(proxy.TryRecordDecryptFailure("probe.example.com", null, forceBypass: true));
        Assert.AreEqual(1, raises);
        Assert.IsTrue(proxy.TryRecordDecryptFailure("probe.example.com", null, forceBypass: true));
        Assert.AreEqual(1, raises);
    }

    [TestMethod]
    public void Http2NegotiationResult_LearnableFailure_AllowsNullRetainedPrefetch()
    {
        // Contract: NegotiateHttp2Async skips session prefetch when LearnableOriginTlsFailure is set.
        var result = new Titanium.Web.Proxy.Http2.Http2NegotiationResult(
            originSupportsHttp2: false,
            retainedConnectionTask: null,
            learnableOriginTlsFailure: true);
        Assert.IsTrue(result.LearnableOriginTlsFailure);
        Assert.IsNull(result.RetainedConnectionTask);
    }

    [TestMethod]
    public void TrySeamlessDecryptBypassRetry_Document_RewritesToMetaRefresh()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 2
        };
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433),
            new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var args = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);

        args.HttpClient.Request.Method = "GET";
        args.HttpClient.Request.RequestUriString = "https://www.expedia.com/";
        args.HttpClient.Request.IsHttps = true;
        args.HttpClient.Request.Headers.AddHeader("Sec-Fetch-Dest", "document");
        args.HttpClient.Response.StatusCode = 403;
        args.HttpClient.Response.IsSynthetic = false;

        Assert.IsTrue(proxy.TrySeamlessDecryptBypassRetry(args));
        Assert.IsTrue(args.HttpClient.Response.Locked);
        Assert.IsTrue(args.CloseClientConnectionAfterResponse);
        Assert.AreEqual(200, args.HttpClient.Response.StatusCode);
        var body = args.HttpClient.Response.BodyString;
        Assert.IsTrue(body.Contains("meta http-equiv=\"refresh\"", StringComparison.Ordinal));
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("www.expedia.com"));
    }

    [TestMethod]
    public void TrySeamlessDecryptBypassRetry_NonDocument_DoesNotRewrite()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 2
        };
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433),
            new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var args = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);

        args.HttpClient.Request.Method = "POST";
        args.HttpClient.Request.RequestUriString = "https://www.expedia.com/graphql";
        args.HttpClient.Request.IsHttps = true;
        args.HttpClient.Request.Headers.AddHeader("Sec-Fetch-Dest", "empty");
        args.HttpClient.Response.StatusCode = 429;

        Assert.IsFalse(proxy.TrySeamlessDecryptBypassRetry(args));
        Assert.IsFalse(args.HttpClient.Response.Locked);
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("www.expedia.com"));
        // Second strike activates without rewrite
        Assert.IsFalse(proxy.TrySeamlessDecryptBypassRetry(args));
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost("www.expedia.com"));
        Assert.IsFalse(args.HttpClient.Response.Locked);
    }
}
