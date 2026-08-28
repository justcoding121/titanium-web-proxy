using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

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
}
