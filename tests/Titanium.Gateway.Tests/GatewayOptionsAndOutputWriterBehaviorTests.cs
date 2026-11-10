using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway;
using Titanium.Gateway.Control;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Tests;

/// <summary>
///     GatewayOptions validation leftovers and OutputWriter.SignatureOf stability.
/// </summary>
[TestClass]
public sealed class GatewayOptionsAndOutputWriterBehaviorTests
{
    private static readonly Dictionary<string, string?> Env = new() { ["POD_NAMESPACE"] = "gw" };

    [TestMethod]
    public void Options_RejectNegativeMinReconcile_AndNonNumericPorts()
    {
        var min = Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.Parse(
            ["--data-plane-service", "titanium", "--min-reconcile-ms", "-1"], Env));
        StringAssert.Contains(min.Message, "min-reconcile-ms");

        var admin = Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.Parse(
            ["--data-plane-service", "titanium", "--admin-port", "abc"], Env));
        StringAssert.Contains(admin.Message, "admin-port");

        var health = Assert.ThrowsExactly<ArgumentException>(() => GatewayOptions.Parse(
            ["--data-plane-service=titanium", "--health-port=nope"], Env));
        StringAssert.Contains(health.Message, "health-port");
    }

    [TestMethod]
    public void SignatureOf_IsStable_Empty_AndOrderIndependent()
    {
        Assert.AreEqual(OutputWriter.SignatureOf([]), OutputWriter.SignatureOf([]));

        var a = new ListenerConfig { Host = "0.0.0.0", Port = 80, Type = "explicit", DecryptSsl = false };
        var b = new ListenerConfig { Host = "0.0.0.0", Port = 443, Type = "explicit", DecryptSsl = true };
        var first = OutputWriter.SignatureOf([b, a]);
        var second = OutputWriter.SignatureOf([a, b]);
        Assert.AreEqual(first, second);
        Assert.AreEqual(16, first.Length);

        var aTls = new ListenerConfig { Host = "0.0.0.0", Port = 80, Type = "explicit", DecryptSsl = true };
        var changed = OutputWriter.SignatureOf([aTls, b]);
        Assert.AreNotEqual(first, changed);
    }
}
