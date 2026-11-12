using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Tests;

/// <summary>
///     Asserted branches for <see cref="AgentAckPayload.TryParse"/> beyond the fuzz corpus.
///     Bug classes: string fleet rejected, health objects dropped, ok:false treated as success.
/// </summary>
[TestClass]
public class AgentAckPayloadBehaviorTests
{
    [TestMethod]
    public void TryParse_EmptyOrNonObject_Fails()
    {
        Assert.IsFalse(AgentAckPayload.TryParse([], out _, out var empty));
        Assert.AreEqual("empty body", empty);
        Assert.IsFalse(AgentAckPayload.TryParse(Encoding.UTF8.GetBytes("[]"), out _, out var root));
        Assert.AreEqual("root must be object", root);
        Assert.IsFalse(AgentAckPayload.TryParse(Encoding.UTF8.GetBytes("{"), out _, out var json));
        StringAssert.StartsWith(json!, "json:");
    }

    [TestMethod]
    public void TryParse_FleetStringNumber_AndBadFleetObject()
    {
        Assert.IsTrue(AgentAckPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"fleet":"42","nodeId":"n1","configHash":"h","ok":false,"error":"bad"}"""),
            out var parsed,
            out _));
        Assert.AreEqual(42L, parsed!.Fleet);
        Assert.AreEqual("n1", parsed.NodeId);
        Assert.AreEqual("h", parsed.ConfigHash);
        Assert.IsFalse(parsed.Ok);
        Assert.AreEqual("bad", parsed.Error);

        Assert.IsFalse(AgentAckPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"fleet":{"id":1}}"""),
            out _,
            out var fleetError));
        Assert.AreEqual("fleet must be number or string", fleetError);

        Assert.IsTrue(AgentAckPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"fleet":null,"node_id":"snake"}"""),
            out var nullFleet,
            out _));
        Assert.IsNull(nullFleet!.Fleet);
        Assert.AreEqual("snake", nullFleet.NodeId);
    }

    [TestMethod]
    public void TryParse_PreservesHealthAndLabelsJsonShapes()
    {
        Assert.IsTrue(AgentAckPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"health":{"paused":true},"labels":{"zone":"a"},"ok":true}"""),
            out var obj,
            out _));
        Assert.AreEqual("""{"paused":true}""", obj!.HealthJson);
        Assert.AreEqual("""{"zone":"a"}""", obj.LabelsJson);

        Assert.IsTrue(AgentAckPayload.TryParse(
            Encoding.UTF8.GetBytes("""{"health":"raw","labelsJson":"{\"k\":\"v\"}"}"""),
            out var raw,
            out _));
        Assert.AreEqual("raw", raw!.HealthJson);
        Assert.AreEqual("""{"k":"v"}""", raw.LabelsJson);
    }
}
