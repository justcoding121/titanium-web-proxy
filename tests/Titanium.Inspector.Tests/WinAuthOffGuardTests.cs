using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// The server-wide Windows authentication flag leaves the engine fast path.
/// Inspector must keep it off; per-host auth, if added, uses the session flag only.
/// </summary>
[TestClass]
public class WinAuthOffGuardTests
{
    [TestMethod]
    public async Task StartedProxy_LeavesServerWinAuthOff()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            var proxy = typeof(InterceptionService)
                .GetField("_proxy", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(interception);
            Assert.IsNotNull(proxy);
            var enabled = (bool)proxy!.GetType().GetProperty("EnableWinAuth")!.GetValue(proxy)!;
            Assert.IsFalse(enabled);
        }
        finally
        {
            interception.Stop();
            interception.EnsureShutdown();
        }
    }
}
