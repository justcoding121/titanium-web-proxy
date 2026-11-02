using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Resilience;

namespace Titanium.Web.Proxy.UnitTests.Resilience;

[TestClass]
public class OverloadMonitorTests
{
    [TestMethod]
    public void Next_ElevatesAndHysteresisPreventsFlap()
    {
        var monitor = new OverloadMonitor(new OverloadMonitorConfig
        {
            HeapPercent = new OverloadSignalThresholds { ElevatedAt = 80, CriticalAt = 90, MinDwell = TimeSpan.FromSeconds(5) },
        });
        var t0 = DateTimeOffset.UtcNow;
        Assert.AreEqual(OverloadLevel.Elevated, monitor.Next(new Dictionary<string, double?> { ["heapPercent"] = 82 }, t0));
        Assert.AreEqual(OverloadLevel.Elevated, monitor.Next(new Dictionary<string, double?> { ["heapPercent"] = 70 }, t0.AddSeconds(1)));
        Assert.AreEqual(OverloadLevel.Normal, monitor.Next(new Dictionary<string, double?> { ["heapPercent"] = 70 }, t0.AddSeconds(6)));
    }

    [TestMethod]
    public void Next_CriticalWinsOverElevated()
    {
        var monitor = new OverloadMonitor(new OverloadMonitorConfig
        {
            HeapPercent = new OverloadSignalThresholds { ElevatedAt = 80, CriticalAt = 90 },
            ThreadPoolQueue = new OverloadSignalThresholds { ElevatedAt = 10, CriticalAt = 100 },
        });
        var level = monitor.Next(new Dictionary<string, double?>
        {
            ["heapPercent"] = 85,
            ["threadPoolQueue"] = 150,
        }, DateTimeOffset.UtcNow);
        Assert.AreEqual(OverloadLevel.Critical, level);
    }
}
