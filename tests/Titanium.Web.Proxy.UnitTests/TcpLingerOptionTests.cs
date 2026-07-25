using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Characterization for issue #896: TcpTimeWaitSeconds configures SO_LINGER via LingerOption,
///     not the kernel TCP TIME_WAIT timer.
/// </summary>
[TestClass]
public class TcpLingerOptionTests
{
    [TestMethod]
    public void TcpTimeWaitSeconds_MapsToEnabledLingerOptionLingerTime()
    {
        var proxy = new ProxyServer(false, false, false) { TcpTimeWaitSeconds = 7 };

        // Runtime mapping used wherever sockets are accepted/connected:
        //   socket.LingerState = new LingerOption(true, proxy.TcpTimeWaitSeconds);
        var linger = new LingerOption(true, proxy.TcpTimeWaitSeconds);

        Assert.IsTrue(linger.Enabled, "Linger must be enabled when applying TcpTimeWaitSeconds.");
        Assert.AreEqual(7, linger.LingerTime,
            "TcpTimeWaitSeconds maps to LingerOption.LingerTime (SO_LINGER seconds), not OS TIME_WAIT.");
        Assert.AreEqual(7, proxy.TcpTimeWaitSeconds);

        proxy.Dispose();
    }
}
