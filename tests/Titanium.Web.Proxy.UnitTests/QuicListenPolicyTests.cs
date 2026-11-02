using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class QuicListenPolicyTests
{
    [TestMethod]
    public void OptIn_AllowsTheListenWithoutCallingTheGuard()
    {
        var previous = QuicListenPolicy.BeforeListen;
        var allow = Environment.GetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS");
        var called = false;
        try
        {
            QuicListenPolicy.BeforeListen = () => called = true;
            Environment.SetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS", "1");
            QuicListenPolicy.EnsureAllowed();
            Assert.IsFalse(called);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS", allow);
            QuicListenPolicy.BeforeListen = previous;
        }
    }

    [TestMethod]
    public void DeveloperTestHost_RunsTheGuardThenRefuses()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The firewall prompt this guards is a Windows behavior.");
            return;
        }

        var previous = QuicListenPolicy.BeforeListen;
        var allow = Environment.GetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS");
        var called = false;
        try
        {
            QuicListenPolicy.BeforeListen = () => called = true;
            Environment.SetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS", null);
            var ex = Assert.ThrowsException<InvalidOperationException>(QuicListenPolicy.EnsureAllowed);
            Assert.IsTrue(called);
            StringAssert.Contains(ex.Message, "TITANIUM_ALLOW_WILDCARD_BIND_TESTS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS", allow);
            QuicListenPolicy.BeforeListen = previous;
        }
    }

    [TestMethod]
    public void ProductProcess_WithNoGuard_StillAllowsTheListen()
    {
        var previous = QuicListenPolicy.BeforeListen;
        var allow = Environment.GetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS");
        try
        {
            QuicListenPolicy.BeforeListen = null;
            Environment.SetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS", null);
            QuicListenPolicy.EnsureAllowed();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_ALLOW_WILDCARD_BIND_TESTS", allow);
            QuicListenPolicy.BeforeListen = previous;
        }
    }
}
