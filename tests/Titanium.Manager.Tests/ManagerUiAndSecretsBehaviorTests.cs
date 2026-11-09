using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Ui;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Tests;

/// <summary>
///     ManagerUiHost.IsHtmlPage routing and inline sharedSecret rejection.
/// </summary>
[TestClass]
public class ManagerUiAndSecretsBehaviorTests
{
    [TestMethod]
    [DataRow("/", true)]
    [DataRow("", true)]
    [DataRow("/audit", true)]
    [DataRow("/AUDIT", true)]
    [DataRow("/fleets", true)]
    [DataRow("/fleets/1/revisions", true)]
    [DataRow("/v1/fleets", false)]
    [DataRow("/login", false)]
    public void IsHtmlPage_ClassifiesUiPaths(string path, bool expected) =>
        Assert.AreEqual(expected, ManagerUiHost.IsHtmlPage(path));

    [TestMethod]
    public void ValidateYaml_RejectsInlineSharedSecret()
    {
        var errors = ConfigRevisionValidator.ValidateYaml("""
            plus:
              enabled: true
              controlPlane:
                host: 127.0.0.1
                sharedSecret: literal-not-allowed
                allowInsecureNonLoopback: true
            """);
        Assert.IsTrue(errors.Any(e => e.Contains("sharedSecret", StringComparison.Ordinal)));
    }
}
