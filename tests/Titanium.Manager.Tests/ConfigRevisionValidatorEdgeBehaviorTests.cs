using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Tests;

/// <summary>
///     ConfigRevisionValidator parse/validate catch paths and YamlDiff identical inputs.
/// </summary>
[TestClass]
public class ConfigRevisionValidatorEdgeBehaviorTests
{
    [TestMethod]
    public void ValidateYaml_MalformedDocument_ReturnsParseErrorWithoutThrowing()
    {
        var errors = ConfigRevisionValidator.ValidateYaml("listeners: [");
        Assert.AreEqual(1, errors.Count);
        StringAssert.StartsWith(errors[0], "YAML parse error:");
    }

    [TestMethod]
    public void YamlDiff_IdenticalInputs_HaveNoChangeHunks()
    {
        var diff = YamlDiff.Unified("a\nb\n", "a\nb\n", "from", "to");
        StringAssert.StartsWith(diff, "--- from\n+++ to\n");
        Assert.IsFalse(diff.Split('\n').Any(static l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal)));
        Assert.IsFalse(diff.Split('\n').Any(static l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WaitHub_CancellationToken_RemovesWaiter()
    {
        var hub = new ConfigWaitHub();
        using var cts = new CancellationTokenSource();
        var wait = hub.WaitAsync(3, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(20);
        Assert.AreEqual(1, hub.CountWaiters(3));
        await cts.CancelAsync();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, hub.CountWaiters(3));
    }
}
