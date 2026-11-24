using System;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UpdateHttpBehaviorTests
{
    [TestMethod]
    public void IsLoopbackProxy_AcceptsLocalhostVariants()
    {
        Assert.IsTrue(UpdateHttp.IsLoopbackProxy(new Uri("http://127.0.0.1:8866")));
        Assert.IsTrue(UpdateHttp.IsLoopbackProxy(new Uri("http://localhost:8866")));
        Assert.IsFalse(UpdateHttp.IsLoopbackProxy(new Uri("http://proxy.corp.example:8080")));
        Assert.IsFalse(UpdateHttp.IsLoopbackProxy(null));
    }

    [TestMethod]
    public void IsOwnLoopbackProxy_RequiresActivePortMatch()
    {
        var own = new Uri("http://127.0.0.1:8866/");
        Assert.IsTrue(UpdateHttp.IsOwnLoopbackProxy(own, 8866, active: true));
        Assert.IsFalse(UpdateHttp.IsOwnLoopbackProxy(own, 8866, active: false));
        Assert.IsFalse(UpdateHttp.IsOwnLoopbackProxy(own, 9999, active: true));
        Assert.IsFalse(UpdateHttp.IsOwnLoopbackProxy(new Uri("http://proxy.corp:8080"), 8866, active: true));
    }

    [TestMethod]
    public void FormatFailure_IncludesInnerAndCaptureHint()
    {
        var inner = new InvalidOperationException("ssl boom");
        var outer = new HttpRequestException("download failed", inner);
        var text = UpdateHttp.FormatFailure(outer, updateTrafficMayBeCaptured: true);
        StringAssert.Contains(text, "download failed");
        StringAssert.Contains(text, "ssl boom");
        StringAssert.Contains(text, "decrypt can break");
    }

    [TestMethod]
    public void CreateClient_ReturnsConfiguredHttpClient()
    {
        using var client = UpdateHttp.CreateClient(
            TimeSpan.FromSeconds(5),
            UpdateHttp.IsLoopbackProxy);
        Assert.IsNotNull(client);
        Assert.AreEqual(TimeSpan.FromSeconds(5), client.Timeout);
    }
}
