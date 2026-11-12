using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests;

/// <summary>
///     EventWriter.EventName stability and Conditions.Make status formatting.
/// </summary>
[TestClass]
public sealed class EventNameAndConditionsBehaviorTests
{
    [TestMethod]
    public void EventName_IsStablePerKey_AndTruncatesLongIngressNames()
    {
        var first = new IngressEvent("ns", "ing", "uid", "Warning", "BadBackend", "boom");
        var again = new IngressEvent("ns", "ing", "uid-2", "Warning", "BadBackend", "boom");
        Assert.AreEqual(EventWriter.EventName(first), EventWriter.EventName(again));

        var other = new IngressEvent("ns", "ing", "uid", "Warning", "Other", "boom");
        Assert.AreNotEqual(EventWriter.EventName(first), EventWriter.EventName(other));

        var longName = new string('a', 220);
        var truncated = EventWriter.EventName(new IngressEvent("ns", longName, null, "Normal", "Synced", "ok"));
        Assert.IsTrue(truncated.StartsWith(new string('a', 200) + ".", StringComparison.Ordinal));
        Assert.AreEqual(200 + 1 + 16, truncated.Length);
    }

    [TestMethod]
    public void Conditions_Make_MapsBooleanStatusAndCopiesFields()
    {
        var ok = Conditions.Make(Conditions.Accepted, true, "Accepted", "ready", 7);
        Assert.AreEqual(Conditions.Accepted, ok.Type);
        Assert.AreEqual("True", ok.Status);
        Assert.AreEqual("Accepted", ok.Reason);
        Assert.AreEqual("ready", ok.Message);
        Assert.AreEqual(7, ok.ObservedGeneration);

        var bad = Conditions.Make(Conditions.Programmed, false, "Pending", "wait", 0);
        Assert.AreEqual("False", bad.Status);
        Assert.AreEqual(Conditions.Programmed, bad.Type);
    }
}
