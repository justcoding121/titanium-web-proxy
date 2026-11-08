using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class InterceptionServicePortConflictTests
{
    [TestMethod]
    public async Task StartAsync_CanRetry_AfterATakenPort()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var port = ((IPEndPoint)occupier.LocalEndpoint).Port;
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };

        var failed = await Assert.ThrowsExactlyAsync<Titanium.Web.Proxy.Exceptions.ProxyListenException>(
            () => interception.StartAsync(IPAddress.Loopback, port));
        Assert.IsTrue(failed.IsAddressInUse);
        Assert.IsFalse(interception.IsRunning);

        occupier.Stop();
        await interception.StartAsync(IPAddress.Loopback, port);
        Assert.IsTrue(interception.IsRunning);
    }

    [TestMethod]
    public void PortInUseMessage_NamesThePortAndTheFreePortMarker()
    {
        var text = MainWindowViewModel.PortInUseMessage(8866);
        StringAssert.Contains(text, "8866");
        StringAssert.Contains(text, "*");
    }
}
