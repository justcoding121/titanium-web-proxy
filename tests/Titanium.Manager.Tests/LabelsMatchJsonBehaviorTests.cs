using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Store;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Tests;

/// <summary>
///     ManagerStore.LabelsMatchJson selector edges and dashboardViewerSecret rejection.
/// </summary>
[TestClass]
public class LabelsMatchJsonBehaviorTests
{
    [TestMethod]
    public void LabelsMatchJson_EmptySelectorMatches_MalformedTreatsAsEmpty_MismatchRejects()
    {
        Assert.IsTrue(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", "{}"));
        Assert.IsTrue(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", ""));
        Assert.IsTrue(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", "{not-json"));
        Assert.IsTrue(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", """{"env":"prod"}"""));
        Assert.IsFalse(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", """{"env":"dev"}"""));
        Assert.IsFalse(ManagerStore.LabelsMatchJson("""{"env":"prod"}""", """{"tier":"a"}"""));
    }

    [TestMethod]
    public void ValidateYaml_RejectsInlineDashboardViewerSecret()
    {
        var errors = ConfigRevisionValidator.ValidateYaml("""
            plus:
              enabled: true
              controlPlane:
                host: 127.0.0.1
                dashboardViewerSecret: inline-secret
                allowInsecureNonLoopback: true
            """);
        Assert.IsTrue(errors.Any(e => e.Contains("dashboardViewerSecret", StringComparison.Ordinal)));
    }
}
