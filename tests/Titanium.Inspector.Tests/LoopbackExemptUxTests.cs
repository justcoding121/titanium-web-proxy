using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class LoopbackExemptUxTests
{
    [TestMethod]
    public void Copy_AppliedStatus_NamesRestart_AndRefreshOnlyWhenNudged()
    {
        var refreshed = LoopbackExemptCopy.AppliedStatus(2, LoopbackCaptureReadiness.Ready, refreshed: true);
        StringAssert.Contains(refreshed, "Allowed: 2.");
        StringAssert.Contains(refreshed, "System proxy was refreshed");
        StringAssert.Contains(refreshed, "fully quit");

        var ready = LoopbackExemptCopy.AppliedStatus(1, LoopbackCaptureReadiness.Ready, refreshed: false);
        StringAssert.Contains(ready, "Allowed: 1.");
        StringAssert.Contains(ready, "Fully quit and reopen");
        Assert.IsFalse(ready.Contains("refreshed", StringComparison.OrdinalIgnoreCase));

        StringAssert.Contains(
            LoopbackExemptCopy.AppliedStatus(3, LoopbackCaptureReadiness.ProxyStopped, refreshed: false),
            "Start the proxy");
        StringAssert.Contains(
            LoopbackExemptCopy.AppliedStatus(3, LoopbackCaptureReadiness.SystemProxyOff, refreshed: false),
            "Turn on System proxy");
    }

    [TestMethod]
    public void Copy_WarningAndUnchanged_MatchReadiness()
    {
        Assert.IsNull(LoopbackExemptCopy.ProxyWarning(LoopbackCaptureReadiness.Ready));
        StringAssert.Contains(
            LoopbackExemptCopy.ProxyWarning(LoopbackCaptureReadiness.ProxyStopped),
            "Start the proxy");
        StringAssert.Contains(
            LoopbackExemptCopy.ProxyWarning(LoopbackCaptureReadiness.SystemProxyOff),
            "Turn on System proxy");
        StringAssert.Contains(LoopbackExemptCopy.Intro, "fully quit");
        StringAssert.Contains(LoopbackExemptCopy.UnchangedStatus(4), "already matches");
        StringAssert.Contains(
            LoopbackExemptCopy.ClearedStatus(LoopbackCaptureReadiness.Ready, refreshed: true),
            "refreshed");
        var closed = new LoopbackExemptResult();
        Assert.IsFalse(closed.Changed);
        Assert.AreEqual(LoopbackExemptCopy.ClosedUnchanged, closed.StatusText);
        StringAssert.Contains(closed.StatusText, "unchanged");
    }

    [TestMethod]
    public void Copy_ShouldRefreshOnlyWhenTheAllowListChangedAndProxyIsOn()
    {
        Assert.IsTrue(LoopbackExemptCopy.ShouldRefreshRunningApps(true, LoopbackCaptureReadiness.Ready));
        Assert.IsFalse(LoopbackExemptCopy.ShouldRefreshRunningApps(false, LoopbackCaptureReadiness.Ready));
        Assert.IsFalse(LoopbackExemptCopy.ShouldRefreshRunningApps(true, LoopbackCaptureReadiness.ProxyStopped));
        Assert.IsFalse(LoopbackExemptCopy.ShouldRefreshRunningApps(true, LoopbackCaptureReadiness.SystemProxyOff));
        Assert.IsTrue(LoopbackExemptCopy.SameSet(["S-1-1", "s-1-2"], ["S-1-2", "S-1-1"]));
        Assert.IsFalse(LoopbackExemptCopy.SameSet(["S-1-1"], ["S-1-1", "S-1-2"]));
    }

    [TestMethod]
    public async Task NudgeSystemProxy_TurnsOffThenOn()
    {
        var recorder = new RecordingSystemProxyController();
        using var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            Assert.IsFalse(interception.NudgeSystemProxy());
            Assert.AreEqual(0, recorder.SetCount);
            Assert.AreEqual(0, recorder.RestoreCount);

            Assert.IsTrue(interception.SetSystemProxy(true, new InspectorSettings()));
            var sets = recorder.SetCount;
            var restores = recorder.RestoreCount;

            Assert.IsTrue(interception.NudgeSystemProxy());
            Assert.AreEqual(restores + 1, recorder.RestoreCount);
            Assert.AreEqual(sets + 1, recorder.SetCount);
            Assert.IsTrue(interception.SystemProxyEnabled);
        }
        finally
        {
            interception.Stop();
        }
    }

    [TestMethod]
    public async Task NudgeSystemProxy_LeavesProxyOff_WhenReenableFailsOrUserTurnsItOff()
    {
        var recorder = new RecordingSystemProxyController();
        using var interception = new InterceptionService(recorder) { UseInMemoryTrustState = true };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            Assert.IsTrue(interception.SetSystemProxy(true, new InspectorSettings()));
            var setsAfterEnable = recorder.SetCount;

            var checks = 0;
            Assert.IsFalse(interception.NudgeSystemProxy(() => ++checks == 1));
            Assert.AreEqual(setsAfterEnable, recorder.SetCount);
            Assert.IsFalse(interception.SystemProxyEnabled);

            Assert.IsTrue(interception.SetSystemProxy(true, new InspectorSettings()));
            recorder.FailSet = true;
            Assert.IsFalse(interception.NudgeSystemProxy());
            Assert.IsFalse(interception.SystemProxyEnabled);
            Assert.IsFalse(string.IsNullOrWhiteSpace(interception.LastSystemProxyError));
        }
        finally
        {
            interception.Stop();
        }
    }
}
