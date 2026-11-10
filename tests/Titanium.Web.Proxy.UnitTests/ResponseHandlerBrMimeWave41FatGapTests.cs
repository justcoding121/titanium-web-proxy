#pragma warning disable TWP001
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat response compression policy: brotli selection for JSON and refusal for tiny bodies.
/// </summary>
[TestClass]
public class ResponseHandlerBrMimeWave41FatGapTests
{
    [TestMethod]
    public void ResponseCompressionPolicy_BrEligible_ForApplicationJson()
    {
        var policy = ResponseCompressionPolicy.Create(new ResponseCompressionConfig
        {
            Enabled = true,
            MinBytes = 32,
            MaxBytes = 512 * 1024,
            MimeTypes = ["application/json"],
            Algorithms = ["br", "gzip"],
        })!;
        var req = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        req.Headers.AddHeader("Accept-Encoding", "br, gzip");
        var res = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        res.Headers.AddHeader("Content-Type", "application/json; charset=utf-8");
        res.ContentLength = 256;
        res.Body = Encoding.UTF8.GetBytes(new string('j', 256));
        res.IsBodyRead = true;
        Assert.IsTrue(policy.TrySelect(req, res, out var algo));
        Assert.AreEqual(HttpCompression.Brotli, algo);
    }

    [TestMethod]
    public void ResponseCompressionPolicy_BelowMinBytes_Refuses()
    {
        var policy = ResponseCompressionPolicy.Create(new ResponseCompressionConfig
        {
            Enabled = true,
            MinBytes = 512,
        })!;
        var req = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        var res = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        res.Headers.AddHeader("Content-Type", "text/plain");
        res.ContentLength = 8;
        res.Body = "tiny"u8.ToArray();
        res.IsBodyRead = true;
        Assert.IsFalse(policy.TrySelect(req, res, out _));
    }
}
#pragma warning restore TWP001
