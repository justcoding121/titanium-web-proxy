using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Soft leftovers toward 90%: GatewayOptions eq-form flags, empty prefixed env values,
///     and ParsePortMap via full Parse.
/// </summary>
[TestClass]
public class GatewayOptionsSoftCoverageWave24BehaviorTests
{
    [TestMethod]
    public void Parse_EqFormFlags_AndPortMapFromEnv()
    {
        var env = new Dictionary<string, string?>
        {
            ["POD_NAMESPACE"] = "gw",
            ["POD_NAME"] = "pod-1",
            ["TITANIUM_GATEWAY_PORT_MAP"] = "80=8080,53/udp=5353",
        };
        var options = GatewayOptions.Parse(
            [
                "--data-plane-service=titanium",
                "--controller-name=example.com/mine",
                "--verbose",
            ],
            env);
        Assert.AreEqual("titanium", options.DataPlaneService);
        Assert.AreEqual("example.com/mine", options.ControllerName);
        Assert.IsTrue(options.Verbose);
        Assert.AreEqual(8080, options.PortMap[80]);
        Assert.AreEqual(5353, options.UdpPortMap[53]);
        Assert.AreEqual("pod-1", options.Identity);
    }

    [TestMethod]
    public void Parse_EmptyPrefixedEnvValue_IsIgnored()
    {
        var env = new Dictionary<string, string?>
        {
            ["POD_NAMESPACE"] = "gw",
            ["TITANIUM_GATEWAY_DATA_PLANE_SERVICE"] = "titanium",
            ["TITANIUM_GATEWAY_VERBOSE"] = "",
            ["TITANIUM_GATEWAY_UNKNOWN_TOGGLE"] = "1",
        };
        var options = GatewayOptions.Parse([], env);
        Assert.IsFalse(options.Verbose);
        CollectionAssert.Contains(options.IgnoredEnvironment.ToList(), "TITANIUM_GATEWAY_UNKNOWN_TOGGLE");
    }

    [TestMethod]
    public void Parse_UnexpectedToken_AndUnknownOption_Throw()
    {
        var env = new Dictionary<string, string?> { ["POD_NAMESPACE"] = "gw" };
        var positional = Assert.ThrowsExactly<ArgumentException>(() =>
            GatewayOptions.Parse(["positional"], env));
        StringAssert.Contains(positional.Message, "Unexpected");

        var unknown = Assert.ThrowsExactly<ArgumentException>(() =>
            GatewayOptions.Parse(["--data-plane-service=titanium", "--not-a-flag", "x"], env));
        StringAssert.Contains(unknown.Message, "not-a-flag");
    }
}
