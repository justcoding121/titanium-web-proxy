using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class ResponseCompressionTests
{
    private static readonly ResponseCompressionConfig Enabled = new() { Enabled = true };

    private static ResponseCompressionPolicy Policy(ResponseCompressionConfig? config = null) =>
        ResponseCompressionPolicy.Create(config ?? Enabled)!;

    private static Request NewRequest(string acceptEncoding = "br, gzip", string method = "GET")
    {
        var request = new Request { Method = method, HttpVersion = HttpHeader.Version11 };
        if (acceptEncoding.Length > 0)
        {
            request.Headers.AddHeader("Accept-Encoding", acceptEncoding);
        }

        return request;
    }

    private static Response NewResponse(int length = 2048, string contentType = "text/html; charset=utf-8")
    {
        var response = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        response.Headers.AddHeader("Content-Length", length.ToString());
        response.Headers.AddHeader("Content-Type", contentType);
        return response;
    }

    private static string Describe(HttpCompression value) => value.ToString();

    // ---- eligibility: one row per rule --------------------------------------------------------------

    public static IEnumerable<object[]> IneligibleCases()
    {
        yield return Case("status 206", (_, resp) => resp.StatusCode = 206);
        yield return Case("status 404", (_, resp) => resp.StatusCode = 404);
        yield return Case("status 304", (_, resp) => resp.StatusCode = 304);
        yield return Case("HEAD request", (req, _) => req.Method = "HEAD");
        yield return Case("Range request", (req, _) => req.Headers.AddHeader("Range", "bytes=0-9"));
        yield return Case("already Content-Encoding", (_, resp) => resp.Headers.AddHeader("Content-Encoding", "gzip"));
        yield return Case("no-transform", (_, resp) => resp.Headers.AddHeader("Cache-Control", "public, No-Transform"));
        yield return Case("no Content-Length", (_, resp) => resp.Headers.RemoveHeader("Content-Length"));
        yield return Case("chunked", (_, resp) =>
        {
            resp.Headers.RemoveHeader("Content-Length");
            resp.Headers.AddHeader("Transfer-Encoding", "chunked");
        });
        yield return Case("below minBytes", (_, resp) => SetLength(resp, 1023));
        yield return Case("above maxBytes", (_, resp) => SetLength(resp, 1_048_577));
        yield return Case("event-stream", (_, resp) => SetType(resp, "text/event-stream"));
        yield return Case("grpc", (_, resp) => SetType(resp, "application/grpc"));
        yield return Case("grpc+proto", (_, resp) => SetType(resp, "application/grpc+proto"));
        yield return Case("grpc-web", (_, resp) => SetType(resp, "application/grpc-web+proto"));
        yield return Case("png", (_, resp) => SetType(resp, "image/png"));
        yield return Case("no Content-Type", (_, resp) => resp.Headers.RemoveHeader("Content-Type"));
        yield return Case("websocket upgrade", (req, _) =>
        {
            req.Headers.AddHeader("Upgrade", "websocket");
            req.Headers.AddHeader("Connection", "Upgrade");
        });
        yield return Case("no Accept-Encoding", (req, _) => req.Headers.RemoveHeader("Accept-Encoding"));
        yield return Case("identity only", (req, _) =>
        {
            req.Headers.RemoveHeader("Accept-Encoding");
            req.Headers.AddHeader("Accept-Encoding", "identity");
        });
        yield return Case("deflate only", (req, _) =>
        {
            req.Headers.RemoveHeader("Accept-Encoding");
            req.Headers.AddHeader("Accept-Encoding", "deflate");
        });
    }

    private static object[] Case(string name, Action<Request, Response> mutate) => [name, mutate];

    private static void SetLength(Response response, int length)
    {
        response.Headers.RemoveHeader("Content-Length");
        response.Headers.AddHeader("Content-Length", length.ToString());
    }

    private static void SetType(Response response, string type)
    {
        response.Headers.RemoveHeader("Content-Type");
        response.Headers.AddHeader("Content-Type", type);
    }

    [TestMethod]
    public void Baseline_IsEligible_AndPicksBrotli()
    {
        Assert.IsTrue(Policy().TrySelect(NewRequest(), NewResponse(), out var algorithm));
        Assert.AreEqual(HttpCompression.Brotli, algorithm);
    }

    [TestMethod]
    [DynamicData(nameof(IneligibleCases))]
    public void EachRule_RefusesCompression(string name, Action<Request, Response> mutate)
    {
        var request = NewRequest();
        var response = NewResponse();
        mutate(request, response);

        Assert.IsFalse(Policy().TrySelect(request, response, out var algorithm),
            $"'{name}' must not be compressed (picked {Describe(algorithm)})");
    }

    [TestMethod]
    [DataRow(1024, true)]
    [DataRow(1025, true)]
    [DataRow(1_048_576, true)]
    [DataRow(1023, false)]
    [DataRow(1_048_577, false)]
    [DataRow(0, false)]
    public void ContentLength_MustBeBetweenMinAndMax(int length, bool expected)
    {
        Assert.AreEqual(expected, Policy().TrySelect(NewRequest(), NewResponse(length), out _));
    }

    [TestMethod]
    public void ConfiguredMinAndMax_AreHonored()
    {
        var policy = Policy(new ResponseCompressionConfig { Enabled = true, MinBytes = 10, MaxBytes = 100 });
        Assert.IsFalse(policy.TrySelect(NewRequest(), NewResponse(9), out _));
        Assert.IsTrue(policy.TrySelect(NewRequest(), NewResponse(10), out _));
        Assert.IsTrue(policy.TrySelect(NewRequest(), NewResponse(100), out _));
        Assert.IsFalse(policy.TrySelect(NewRequest(), NewResponse(101), out _));
    }

    // ---- media types ---------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("text/html", true)]
    [DataRow("text/plain; charset=utf-8", true)]
    [DataRow("TEXT/CSS", true)]
    [DataRow("application/json", true)]
    [DataRow("application/json; charset=utf-8", true)]
    [DataRow("application/javascript", true)]
    [DataRow("application/xml", true)]
    [DataRow("image/svg+xml", true)]
    [DataRow("image/png", false)]
    [DataRow("application/octet-stream", false)]
    [DataRow("application/zip", false)]
    [DataRow("video/mp4", false)]
    public void DefaultMimeTypes(string contentType, bool expected)
    {
        Assert.AreEqual(expected, Policy().TrySelect(NewRequest(), NewResponse(2048, contentType), out _));
    }

    [TestMethod]
    public void ConfiguredMimeTypes_ReplaceTheDefaults()
    {
        var policy = Policy(new ResponseCompressionConfig
        {
            Enabled = true,
            MimeTypes = ["application/wasm", "font/*"],
        });
        Assert.IsTrue(policy.TrySelect(NewRequest(), NewResponse(2048, "application/wasm"), out _));
        Assert.IsTrue(policy.TrySelect(NewRequest(), NewResponse(2048, "font/woff2"), out _));
        Assert.IsFalse(policy.TrySelect(NewRequest(), NewResponse(2048, "text/html"), out _));
    }

    [TestMethod]
    public void ConfiguredMimeTypes_NeverReEnableEventStreamOrGrpc()
    {
        var policy = Policy(new ResponseCompressionConfig { Enabled = true, MimeTypes = ["text/*", "application/*"] });
        Assert.IsFalse(policy.TrySelect(NewRequest(), NewResponse(2048, "text/event-stream"), out _));
        Assert.IsFalse(policy.TrySelect(NewRequest(), NewResponse(2048, "application/grpc"), out _));
    }

    // ---- Accept-Encoding and algorithm choice -------------------------------------------------------

    [TestMethod]
    [DataRow("gzip", "gzip")]
    [DataRow("br", "br")]
    [DataRow("br, gzip", "br")]
    [DataRow("gzip, br", "br")]
    [DataRow("gzip, deflate, br", "br")]
    [DataRow("BR, GZIP", "br")]
    [DataRow("br;q=0, gzip", "gzip")]
    [DataRow("br;q=0.0, gzip;q=1.0", "gzip")]
    [DataRow("*", "br")]
    [DataRow("gzip;q=0, *", "br")]
    [DataRow("br;q=0, *;q=0.5", "gzip")]
    [DataRow("identity", "none")]
    [DataRow("gzip;q=0, br;q=0", "none")]
    [DataRow("*;q=0", "none")]
    [DataRow("deflate", "none")]
    public void AcceptEncoding_SelectsTheEncoding(string acceptEncoding, string expected)
    {
        var selected = Policy().TrySelect(NewRequest(acceptEncoding), NewResponse(), out var algorithm);
        Assert.AreEqual(expected != "none", selected);
        Assert.AreEqual(Parse(expected), algorithm);
    }

    private static HttpCompression Parse(string name) => name switch
    {
        "br" => HttpCompression.Brotli,
        "gzip" => HttpCompression.Gzip,
        _ => HttpCompression.Unsupported
    };

    [TestMethod]
    public void AcceptEncoding_AcrossSeveralHeaderLines()
    {
        var request = NewRequest("gzip");
        request.Headers.AddHeader("Accept-Encoding", "br");
        Assert.IsTrue(Policy().TrySelect(request, NewResponse(), out var algorithm));
        Assert.AreEqual(HttpCompression.Brotli, algorithm);
    }

    [TestMethod]
    public void Brotli_IsPreferredByDefault_AndGzipWhenListedFirst()
    {
        Assert.IsTrue(Policy().TrySelect(NewRequest("gzip, br"), NewResponse(), out var byDefault));
        Assert.AreEqual(HttpCompression.Brotli, byDefault);

        var gzipFirst = Policy(new ResponseCompressionConfig { Enabled = true, Algorithms = ["gzip", "br"] });
        Assert.IsTrue(gzipFirst.TrySelect(NewRequest("br, gzip"), NewResponse(), out var configured));
        Assert.AreEqual(HttpCompression.Gzip, configured);
    }

    [TestMethod]
    public void GzipOnly_NeverPicksBrotli()
    {
        var policy = Policy(new ResponseCompressionConfig { Enabled = true, Algorithms = ["gzip"] });
        Assert.IsTrue(policy.TrySelect(NewRequest("br, gzip"), NewResponse(), out var algorithm));
        Assert.AreEqual(HttpCompression.Gzip, algorithm);
        Assert.IsFalse(policy.TrySelect(NewRequest("br"), NewResponse(), out _));
    }

    // ---- effects and round trip ----------------------------------------------------------------------

    private static byte[] Decode(HttpCompression algorithm, byte[] wire)
    {
        using var input = new MemoryStream(wire);
        using Stream decoder = algorithm == HttpCompression.Brotli
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Payload(int length = 4096) =>
        Encoding.UTF8.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("<p>hello compression</p>", length / 24 + 1)))[..length];

    [TestMethod]
    [DataRow("br")]
    [DataRow("gzip")]
    public void RoundTrip_DecodesToTheOriginalBytes(string encodingName)
    {
        var algorithm = Parse(encodingName);
        var body = Payload();
        var response = NewResponse(body.Length);
        var policy = Policy();

        var compressed = policy.Compress(algorithm, body);
        ResponseCompressionPolicy.Apply(response, algorithm, compressed);

        Assert.IsTrue(compressed.Length < body.Length);
        CollectionAssert.AreEqual(body, Decode(algorithm, compressed));
        Assert.AreEqual(encodingName, response.ContentEncoding);
        Assert.AreEqual(compressed.Length, response.ContentLength);
        Assert.AreEqual(1, response.Headers.GetHeaders("Content-Length")!.Count);
        Assert.IsTrue(response.BodyIsWireEncoded);
        CollectionAssert.AreEqual(compressed, response.Body);
    }

    [TestMethod]
    [DataRow("fastest")]
    [DataRow("optimal")]
    [DataRow("smallestSize")]
    [DataRow(null)]
    public void EveryLevel_RoundTrips(string? level)
    {
        var body = Payload();
        var policy = Policy(new ResponseCompressionConfig { Enabled = true, Level = level });
        foreach (var algorithm in new[] { HttpCompression.Brotli, HttpCompression.Gzip })
        {
            CollectionAssert.AreEqual(body, Decode(algorithm, policy.Compress(algorithm, body)));
        }
    }

    [TestMethod]
    public void DefaultLevel_IsFastest()
    {
        Assert.AreEqual(CompressionLevel.Fastest, Policy().Level);
        Assert.AreEqual(CompressionLevel.Optimal,
            Policy(new ResponseCompressionConfig { Enabled = true, Level = "optimal" }).Level);
    }

    [TestMethod]
    public void Apply_AddsVaryAcceptEncoding()
    {
        var response = NewResponse();
        ResponseCompressionPolicy.Apply(response, HttpCompression.Gzip, Policy().Compress(HttpCompression.Gzip, Payload()));

        var vary = response.Headers.GetHeaders("Vary")!;
        Assert.AreEqual(1, vary.Count);
        Assert.AreEqual("Accept-Encoding", vary[0].Value);
    }

    [TestMethod]
    public void Apply_KeepsAnExistingVary_AndAddsAcceptEncodingOnce()
    {
        var response = NewResponse();
        response.Headers.AddHeader("Vary", "Origin");
        var compressed = Policy().Compress(HttpCompression.Gzip, Payload());
        ResponseCompressionPolicy.Apply(response, HttpCompression.Gzip, compressed);

        var values = new List<string>();
        foreach (var header in response.Headers.GetHeaders("Vary")!)
        {
            values.Add(header.Value);
        }

        CollectionAssert.Contains(values, "Origin");
        CollectionAssert.Contains(values, "Accept-Encoding");
    }

    [TestMethod]
    [DataRow("Accept-Encoding")]
    [DataRow("origin, accept-encoding")]
    [DataRow("*")]
    public void Apply_DoesNotDuplicateVary(string existing)
    {
        var response = NewResponse();
        response.Headers.AddHeader("Vary", existing);
        ResponseCompressionPolicy.Apply(response, HttpCompression.Gzip, Policy().Compress(HttpCompression.Gzip, Payload()));

        var vary = response.Headers.GetHeaders("Vary")!;
        Assert.AreEqual(1, vary.Count);
        Assert.AreEqual(existing, vary[0].Value);
    }

    [TestMethod]
    public void Apply_WeakensAStrongETag()
    {
        var response = NewResponse();
        response.Headers.AddHeader("ETag", "\"abc123\"");
        ResponseCompressionPolicy.Apply(response, HttpCompression.Brotli, Policy().Compress(HttpCompression.Brotli, Payload()));

        Assert.AreEqual("W/\"abc123\"", response.Headers.GetFirstHeader("ETag")!.Value);
        Assert.AreEqual(1, response.Headers.GetHeaders("ETag")!.Count);
    }

    [TestMethod]
    public void Apply_LeavesAWeakETagAndAMissingETagAlone()
    {
        var weak = NewResponse();
        weak.Headers.AddHeader("ETag", "W/\"abc\"");
        ResponseCompressionPolicy.Apply(weak, HttpCompression.Brotli, Policy().Compress(HttpCompression.Brotli, Payload()));
        Assert.AreEqual("W/\"abc\"", weak.Headers.GetFirstHeader("ETag")!.Value);

        var none = NewResponse();
        ResponseCompressionPolicy.Apply(none, HttpCompression.Brotli, Policy().Compress(HttpCompression.Brotli, Payload()));
        Assert.IsFalse(none.Headers.HeaderExists("ETag"));
    }

    // ---- route override and off-by-default -----------------------------------------------------------

    [TestMethod]
    [DataRow("off", true)]
    [DataRow("OFF", true)]
    [DataRow(null, false)]
    [DataRow("", false)]
    public void RouteCompressionOff_OptsOut(string? mode, bool expectedOff)
    {
        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/" },
            Compression = mode,
        };
        Assert.AreEqual(expectedOff, ResponseCompressionPolicy.IsRouteOff(route));
        Assert.IsFalse(ResponseCompressionPolicy.IsRouteOff(null));
    }

    [TestMethod]
    public void Policy_IsNullUnlessEnabled()
    {
        Assert.IsNull(ResponseCompressionPolicy.Create(null));
        Assert.IsNull(ResponseCompressionPolicy.Create(new ResponseCompressionConfig()));
        Assert.IsNull(ResponseCompressionPolicy.Create(new ResponseCompressionConfig { Enabled = false }));
        Assert.IsNotNull(ResponseCompressionPolicy.Create(new ResponseCompressionConfig { Enabled = true }));
    }

    [TestMethod]
    public void ProxyServer_IsOffByDefault_AndDoesNotForceInterception()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsNull(proxy.ResponseCompression);
        Assert.IsNull(proxy.CompressionPolicy);
        Assert.IsFalse(proxy.NeedsHttpInterception());

        proxy.ResponseCompression = new ResponseCompressionConfig { Enabled = false };
        Assert.IsNull(proxy.CompressionPolicy);
        Assert.IsFalse(proxy.NeedsHttpInterception());
    }

    [TestMethod]
    public void ProxyServer_WhenEnabled_UsesTheSessionPath_AndRefusesTerminateLite()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
            ForwardCleartext = true,
        };
        var get = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, get, false, false, false, null));

        proxy.ResponseCompression = Enabled;
        Assert.IsNotNull(proxy.CompressionPolicy);
        Assert.IsTrue(proxy.NeedsHttpInterception());
        Assert.IsFalse(proxy.CanUseH1TerminateLite(ep, get, false, false, false, null));

        proxy.ResponseCompression = null;
        Assert.IsNull(proxy.CompressionPolicy);
        Assert.IsTrue(proxy.CanUseH1TerminateLite(ep, get, false, false, false, null));
    }
}
