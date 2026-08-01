using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TunnelConnectTimingTests
{
    [TestMethod]
    public void Marks_ComputeDurationsWhenComplete()
    {
        var timing = new TunnelConnectTiming(DateTime.UtcNow.AddMilliseconds(-50));
        timing.MarkOriginCapabilityStarted("resolve");
        timing.MarkOriginCapabilityCompleted("background");
        timing.MarkHttp2ProbeStarted(cacheHit: true);
        timing.MarkHttp2ProbeCompleted();
        timing.MarkCertificateReady();
        timing.MarkBrowserTlsStarted();
        timing.MarkBrowserTlsCompleted();

        Assert.AreEqual("background", timing.OriginCapabilitySource);
        Assert.IsTrue(timing.Http2CapabilityCacheHit);
        Assert.IsNotNull(timing.OriginCapabilityDuration);
        Assert.IsNotNull(timing.Http2ProbeDuration);
        Assert.IsNotNull(timing.BrowserTlsDuration);
        Assert.IsNotNull(timing.TotalDuration);
        Assert.IsTrue(timing.TotalDuration >= TimeSpan.Zero);
    }
}
