using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class StaticFileHostTests
{
    [TestMethod]
    public void RegisterIfNeeded_NullOrEmptyRoot_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        StaticFileHost.RegisterIfNeeded(proxy, null, sessionPathEnabled: true);
        StaticFileHost.RegisterIfNeeded(proxy, new StaticFilesConfig { Root = "" }, sessionPathEnabled: true);
        StaticFileHost.RegisterIfNeeded(proxy, new StaticFilesConfig { Root = null }, sessionPathEnabled: false);
        Assert.IsFalse(proxy.EnableHttpInterception);
        Assert.AreEqual(0, proxy.ProxyEndPoints.Count);
    }

    [TestMethod]
    public void RegisterIfNeeded_WithoutSessionPath_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var root = Path.Combine(Path.GetTempPath(), "twp-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                StaticFileHost.RegisterIfNeeded(proxy, new StaticFilesConfig { Root = root }, sessionPathEnabled: false));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void RegisterIfNeeded_MissingRoot_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var missing = Path.Combine(Path.GetTempPath(), "twp-static-missing-" + Guid.NewGuid().ToString("N"));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            StaticFileHost.RegisterIfNeeded(proxy, new StaticFilesConfig { Root = missing }, sessionPathEnabled: true));
    }

    [TestMethod]
    public void GuessContentType_MapsCommonExtensions()
    {
        Assert.AreEqual("text/html; charset=utf-8", StaticFileHost.GuessContentType("x.html"));
        Assert.AreEqual("text/css; charset=utf-8", StaticFileHost.GuessContentType("a.css"));
        Assert.AreEqual("application/javascript; charset=utf-8", StaticFileHost.GuessContentType("app.js"));
        Assert.AreEqual("application/json; charset=utf-8", StaticFileHost.GuessContentType("data.json"));
        Assert.AreEqual("image/png", StaticFileHost.GuessContentType("i.png"));
        Assert.AreEqual("image/jpeg", StaticFileHost.GuessContentType("i.jpg"));
        Assert.AreEqual("image/svg+xml", StaticFileHost.GuessContentType("i.svg"));
        Assert.AreEqual("text/plain; charset=utf-8", StaticFileHost.GuessContentType("n.txt"));
        Assert.AreEqual("application/octet-stream", StaticFileHost.GuessContentType("bin.dat"));
    }

    [TestMethod]
    public void RangeEtagAndCompressionHelpers_CoverPrivateArms()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var parse = typeof(StaticFileHost).GetMethod("TryParseBytesRange", flags)!;
        object?[] args = [null, 10, 0, 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);
        args = ["items=0-1", 10, 0, 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);
        args = ["bytes=0-1,2-3", 10, 0, 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);
        args = ["bytes=nope", 10, 0, 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);
        args = ["bytes=-3", 10, 0, 0];
        Assert.IsTrue((bool)parse.Invoke(null, args)!);
        args = ["bytes=2-", 10, 0, 0];
        Assert.IsTrue((bool)parse.Invoke(null, args)!);
        args = ["bytes=2-8", 10, 0, 0];
        Assert.IsTrue((bool)parse.Invoke(null, args)!);
        args = ["bytes=9-1", 10, 0, 0];
        Assert.IsFalse((bool)parse.Invoke(null, args)!);

        var etag = typeof(StaticFileHost).GetMethod("ETagMatches", flags)!;
        Assert.IsTrue((bool)etag.Invoke(null, ["*", "W/\"1\""])!);
        Assert.IsTrue((bool)etag.Invoke(null, ["W/\"1\"", "W/\"1\""])!);
        Assert.IsFalse((bool)etag.Invoke(null, ["W/\"2\"", "W/\"1\""])!);

        var gzip = (byte[])typeof(StaticFileHost).GetMethod("GzipCompress", flags)!.Invoke(null, ["hello"u8.ToArray()])!;
        Assert.IsTrue(gzip.Length > 0);
        var br = (byte[])typeof(StaticFileHost).GetMethod("BrotliCompress", flags)!.Invoke(null, ["hello"u8.ToArray()])!;
        Assert.IsTrue(br.Length > 0);
    }

    [TestMethod]
    public async Task RegisterIfNeeded_ServesIndexRangeAndNotModified()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-static-serve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html>ok</html>");
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(ep);
        StaticFileHost.RegisterIfNeeded(proxy, new StaticFilesConfig
        {
            Root = root,
            EnableGzip = true,
            EnableBrotli = true,
        }, sessionPathEnabled: true);
        proxy.Start();
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{ep.Port}"),
                UseProxy = true,
            })
            { Timeout = TimeSpan.FromSeconds(5) };

            var ok = await http.GetAsync("http://static.test/");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
            var etag = ok.Headers.TryGetValues("ETag", out var etags)
                ? etags.First()
                : ok.Headers.ETag!.ToString();
            using var inm = new HttpRequestMessage(HttpMethod.Get, "http://static.test/");
            inm.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var notMod = await http.SendAsync(inm);
            Assert.AreEqual(HttpStatusCode.NotModified, notMod.StatusCode);

            using var range = new HttpRequestMessage(HttpMethod.Get, "http://static.test/index.html");
            range.Headers.TryAddWithoutValidation("Range", "bytes=0-3");
            var partial = await http.SendAsync(range);
            Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);

            using var gz = new HttpRequestMessage(HttpMethod.Get, "http://static.test/index.html");
            gz.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            var gzipped = await http.SendAsync(gz);
            Assert.AreEqual(HttpStatusCode.OK, gzipped.StatusCode);

            using var br = new HttpRequestMessage(HttpMethod.Get, "http://static.test/index.html");
            br.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
            var brotli = await http.SendAsync(br);
            Assert.AreEqual(HttpStatusCode.OK, brotli.StatusCode);
        }
        finally
        {
            proxy.Stop();
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
