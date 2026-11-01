using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class ListenerReconcilerTests
{
    [TestMethod]
    public void Plan_AddRemoveReplace_IdentifiesActions()
    {
        var running = new Dictionary<ListenerKey, ListenerConfig>
        {
            [new ListenerKey("explicit", "127.0.0.1", 8080)] = new ListenerConfig { Host = "127.0.0.1", Port = 8080 },
            [new ListenerKey("explicit", "127.0.0.1", 8081)] = new ListenerConfig { Host = "127.0.0.1", Port = 8081, DecryptSsl = true },
        };

        var desired = new List<ListenerConfig>
        {
            new() { Host = "127.0.0.1", Port = 8080 },
            new() { Host = "127.0.0.1", Port = 9090 },
        };

        var plan = ListenerReconciler.Plan(running, desired);
        Assert.IsTrue(plan.Any(p => p.Key.Port == 9090 && p.Action == ListenerPlanAction.Add));
        Assert.IsTrue(plan.Any(p => p.Key.Port == 8081 && p.Action == ListenerPlanAction.Remove));
        Assert.IsTrue(plan.Any(p => p.Key.Port == 8080 && p.Action == ListenerPlanAction.Keep));
    }

    [TestMethod]
    public void DetectListenerChangeWithoutReload_WarnsWhenDisabled()
    {
        var running = new Dictionary<ListenerKey, ListenerConfig>
        {
            [ListenerKey.From(new ListenerConfig { Host = "127.0.0.1", Port = 80 })] =
                new ListenerConfig { Host = "127.0.0.1", Port = 80 },
        };
        var desired = new List<ListenerConfig> { new() { Host = "127.0.0.1", Port = 81 } };
        Assert.IsTrue(ListenerReconciler.DetectListenerChangeWithoutReload(running, desired));
    }
}
