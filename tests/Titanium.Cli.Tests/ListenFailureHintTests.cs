using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Exceptions;

namespace Titanium.Cli.Tests;

[TestClass]
public class ListenFailureHintTests
{
    [TestMethod]
    public void Format_AddsTheListenerConfigKey()
    {
        var ex = new ProxyListenException("explicit", System.Net.IPAddress.Loopback, 8000, new SocketException(10048));
        var config = new TwpConfig
        {
            Listeners =
            [
                new ListenerConfig { Port = 80 },
                new ListenerConfig { Port = 8000 },
            ],
        };

        var text = ListenFailureHint.Format(ex, config, "twp.yaml", isAlive: _ => false, readPid: _ => null);

        StringAssert.Contains(text, "address already in use");
        StringAssert.Contains(text, "(config: listeners[1].port)");
    }

    [TestMethod]
    public void Format_OmitsTheConfigKey_WhenNoListenerMatches()
    {
        var ex = new ProxyListenException("explicit", System.Net.IPAddress.Loopback, 9, new SocketException(10048));
        var text = ListenFailureHint.Format(
            ex,
            new TwpConfig { Listeners = [new ListenerConfig { Port = 8000 }] },
            "twp.yaml",
            isAlive: _ => false,
            readPid: _ => null);

        Assert.IsFalse(text.Contains("listeners[", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Format_AddsAPidHint_WhenAnotherLiveProcessHasThisConfig()
    {
        var ex = new ProxyListenException("explicit", System.Net.IPAddress.Loopback, 8000, new SocketException(10048));
        var text = ListenFailureHint.Format(
            ex,
            new TwpConfig { Listeners = [new ListenerConfig { Port = 8000 }] },
            "twp.yaml",
            isAlive: pid => pid == 20716,
            currentPid: 1,
            readPid: _ => 20716);

        StringAssert.Contains(text, "PID 20716");
        StringAssert.Contains(text, "titanium reload -c twp.yaml");
    }

    [TestMethod]
    public void Format_OmitsThePidHint_ForTheCurrentProcess_ADeadPid_OrAnotherError()
    {
        var inUse = new ProxyListenException("explicit", System.Net.IPAddress.Loopback, 8000, new SocketException(10048));
        var config = new TwpConfig { Listeners = [new ListenerConfig { Port = 8000 }] };

        var self = ListenFailureHint.Format(inUse, config, "twp.yaml", isAlive: _ => true, currentPid: 20716, readPid: _ => 20716);
        var dead = ListenFailureHint.Format(inUse, config, "twp.yaml", isAlive: _ => false, currentPid: 1, readPid: _ => 20716);
        var denied = new ProxyListenException("explicit", System.Net.IPAddress.Loopback, 8000, new SocketException(10013));
        var other = ListenFailureHint.Format(denied, config, "twp.yaml", isAlive: _ => true, currentPid: 1, readPid: _ => 20716);

        Assert.IsFalse(self.Contains("PID", StringComparison.Ordinal));
        Assert.IsFalse(dead.Contains("PID", StringComparison.Ordinal));
        Assert.IsFalse(other.Contains("PID", StringComparison.Ordinal));
    }
}
