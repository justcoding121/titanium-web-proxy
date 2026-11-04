using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class ListenerReconcilerTests
{
    private static readonly string[] Add9090Remove8080 = ["add:9090", "remove:8080"];
    private static readonly int[] Removed9090 = [9090];

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

    private static (Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> Running, ProxyEndPoint Old)
        RunningWith(ListenerConfig config)
    {
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, config.Port, false);
        return (new() { [ListenerKey.From(config)] = (config, endpoint) }, endpoint);
    }

    [TestMethod]
    public void Apply_AddsBeforeRemoving_AndRemovesLast()
    {
        var keep = new ListenerConfig { Host = "127.0.0.1", Port = 8080 };
        var (running, oldEndpoint) = RunningWith(keep);
        var calls = new List<string>();
        var desired = new List<ListenerConfig> { new() { Host = "127.0.0.1", Port = 9090 } };

        ListenerReconciler.Apply(
            desired,
            running,
            l =>
            {
                calls.Add("add:" + l.Port);
                return new ExplicitProxyEndPoint(IPAddress.Loopback, l.Port, false);
            },
            e => calls.Add("remove:" + e.Port));

        CollectionAssert.AreEqual(Add9090Remove8080, calls);
        Assert.AreEqual(1, running.Count);
        Assert.IsTrue(running.ContainsKey(ListenerKey.From(desired[0])));
        Assert.IsNotNull(oldEndpoint);
    }

    [TestMethod]
    public void Apply_FailedAdd_KeepsEveryRunningListener()
    {
        var keep = new ListenerConfig { Host = "127.0.0.1", Port = 8080 };
        var (running, oldEndpoint) = RunningWith(keep);
        var removed = new List<int>();
        var desired = new List<ListenerConfig>
        {
            new() { Host = "127.0.0.1", Port = 9090 },
            new() { Host = "127.0.0.1", Port = 9091 },
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => ListenerReconciler.Apply(
            desired,
            running,
            l => l.Port == 9091
                ? throw new InvalidOperationException("address in use")
                : new ExplicitProxyEndPoint(IPAddress.Loopback, l.Port, false),
            e => removed.Add(e.Port)));

        // The old listener was never touched, the endpoint that did bind is undone.
        Assert.AreEqual(1, running.Count);
        Assert.AreSame(oldEndpoint, running[ListenerKey.From(keep)].Endpoint);
        CollectionAssert.AreEqual(Removed9090, removed);
    }

    [TestMethod]
    public void Apply_FailedReplace_RestoresThePreviousListener()
    {
        var original = new ListenerConfig { Host = "127.0.0.1", Port = 8080 };
        var (running, _) = RunningWith(original);
        var changed = new ListenerConfig { Host = "127.0.0.1", Port = 8080, DecryptSsl = true };
        var binds = 0;

        Assert.ThrowsExactly<InvalidOperationException>(() => ListenerReconciler.Apply(
            [changed],
            running,
            l =>
            {
                // First bind (the replacement) fails, the second (restoring the old config) succeeds.
                if (binds++ == 0)
                {
                    throw new InvalidOperationException("bind failed");
                }

                Assert.IsFalse(l.DecryptSsl);
                return new ExplicitProxyEndPoint(IPAddress.Loopback, l.Port, false);
            },
            _ => { }));

        Assert.AreEqual(1, running.Count);
        Assert.IsFalse(running[ListenerKey.From(original)].Config.DecryptSsl);
        Assert.AreEqual(2, binds);
    }
}
