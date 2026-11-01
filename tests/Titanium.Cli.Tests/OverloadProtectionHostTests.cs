using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class OverloadProtectionHostTests
{
    [TestMethod]
    public void Settings_MissingSignalSections_UsePlanDefaults()
    {
        var settings = OverloadProtectionHost.Settings.From(new OverloadConfig { Enabled = true });

        Assert.AreEqual(85, settings.Monitor.HeapPercent.ElevatedAt);
        Assert.AreEqual(95, settings.Monitor.HeapPercent.CriticalAt);
        Assert.AreEqual(1000, settings.Monitor.ThreadPoolQueue.ElevatedAt);
        Assert.AreEqual(10_000, settings.Monitor.ThreadPoolQueue.CriticalAt);
        Assert.AreEqual(0, settings.Monitor.ActiveConnections.ElevatedAt, "connections stay off unless configured");
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), settings.Interval);
        Assert.IsTrue(settings.StopAccepting && settings.DisableKeepAlive && settings.RejectNewRequests);
        Assert.IsTrue(settings.ReadyzFailsOnCritical);
        Assert.AreEqual(1, settings.RetryAfterSeconds);
    }

    [TestMethod]
    public void Settings_ExplicitValuesWin_AndZeroTurnsASignalOff()
    {
        var settings = OverloadProtectionHost.Settings.From(new OverloadConfig
        {
            Enabled = true,
            IntervalMs = 250,
            RetryAfterSeconds = 7,
            HeapPercent = new OverloadSignalConfig { Elevated = 70, Critical = 80 },
            ThreadPoolQueue = new OverloadSignalConfig { Elevated = 0, Critical = 0 },
            ActiveConnections = new OverloadSignalConfig { Elevated = 50, Critical = 100 },
            Actions = new OverloadActionsConfig { ReadyzFailsOnCritical = false },
        });

        Assert.AreEqual(70, settings.Monitor.HeapPercent.ElevatedAt);
        Assert.AreEqual(0, settings.Monitor.ThreadPoolQueue.ElevatedAt);
        Assert.AreEqual(100, settings.Monitor.ActiveConnections.CriticalAt);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), settings.Interval);
        Assert.AreEqual(7, settings.RetryAfterSeconds);
        Assert.IsFalse(settings.ReadyzFailsOnCritical);
    }

    [TestMethod]
    public async Task Apply_StartsReconfiguresAndStopsWithoutRestart()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        await using var host = new OverloadProtectionHost(proxy);
        Assert.IsFalse(host.IsRunning, "nothing runs until the section is enabled");

        host.Apply(new OverloadConfig { Enabled = false });
        Assert.IsFalse(host.IsRunning);

        host.Apply(new OverloadConfig { Enabled = true, IntervalMs = 10_000 });
        Assert.IsTrue(host.IsRunning);

        host.Apply(new OverloadConfig { Enabled = true, IntervalMs = 5_000 });
        Assert.IsTrue(host.IsRunning, "a reload swaps settings on the running loop");

        host.Apply(null);
        Assert.IsFalse(host.IsRunning);
        Assert.IsFalse(proxy.OverloadProtectionEnabled);
        Assert.AreEqual(0, proxy.OverloadLevel);
    }

    [TestMethod]
    public async Task ReadyzShouldFail_FollowsCriticalLevelAndTheAction()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        await using var host = new OverloadProtectionHost(proxy);
        host.Apply(new OverloadConfig { Enabled = true, IntervalMs = 10_000 });

        proxy.SetOverloadRuntime(true, 1, true, true, true, 1);
        Assert.IsFalse(host.ReadyzShouldFail, "elevated does not fail readiness");

        proxy.SetOverloadRuntime(true, 2, true, true, true, 1);
        Assert.IsTrue(host.ReadyzShouldFail);

        host.Apply(new OverloadConfig
        {
            Enabled = true,
            IntervalMs = 10_000,
            Actions = new OverloadActionsConfig { ReadyzFailsOnCritical = false },
        });
        proxy.SetOverloadRuntime(true, 2, true, true, true, 1);
        Assert.IsFalse(host.ReadyzShouldFail, "the action can be switched off by a reload");
    }

    [TestMethod]
    public void Readiness_OverloadProbe_AddsReasonAndRecovers()
    {
        var state = new ReadinessState();
        state.MarkConfigLoaded();
        state.MarkListenersBound();
        var critical = false;
        state.OverloadCriticalProbe = () => critical;
        Assert.IsTrue(state.Check().Ready);

        critical = true;
        var report = state.Check();
        Assert.IsFalse(report.Ready);
        CollectionAssert.AreEqual(new[] { ReadinessState.ReasonOverloadCritical }, report.Reasons.ToArray());

        critical = false;
        Assert.IsTrue(state.Check().Ready, "unlike draining, overload readiness is not one-way");
    }
}
