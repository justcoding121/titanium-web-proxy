using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class DecryptHttpsProbeTests
{
    [TestMethod]
    public async Task VerifyAsync_WhenProxyNotRunning_FailsFast()
    {
        using var interception = new InterceptionService();
        var result = await DecryptHttpsProbe.VerifyAsync(
            interception,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Message, "not listening");
    }

    [TestMethod]
    public async Task VerifyAsync_WhenDecryptOff_FailsFast()
    {
        using var interception = new InterceptionService();
        // BoundPort stays 0 until Start; still reports not listening first.
        interception.DecryptHttps = false;
        var result = await DecryptHttpsProbe.VerifyAsync(
            interception,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.Ok);
    }
}
