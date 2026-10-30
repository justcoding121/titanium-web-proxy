using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.UnitTests.Udp;

[TestClass]
public class UdpSessionWheelTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);

    private readonly List<Socket> sockets = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var socket in sockets)
        {
            socket.Dispose();
        }
    }

    private UdpSession Session(long nowMs)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockets.Add(socket);
        var address = new IPEndPoint(IPAddress.Loopback, 5000).Serialize();
        var client = new UdpClientState(IPAddress.Loopback, 100, nowMs);
        return new UdpSession(address, new IPEndPoint(IPAddress.Loopback, 5000), client, socket, null, null, nowMs);
    }

    private static List<UdpSession> Advance(UdpSessionWheel wheel, long nowMs)
    {
        var expired = new List<UdpSession>();
        wheel.Advance(nowMs, expired);
        return expired;
    }

    [TestMethod]
    public void IdleSession_ExpiresOnlyAfterTheTimeout()
    {
        var wheel = new UdpSessionWheel(Idle, 0);
        var session = Session(0);
        wheel.Add(session);

        Assert.AreEqual(0, Advance(wheel, 9_999).Count);
        var expired = Advance(wheel, 10_000);
        Assert.AreEqual(1, expired.Count);
        Assert.AreSame(session, expired[0]);
        Assert.AreEqual(0, wheel.Count);
    }

    [TestMethod]
    public void ActiveSession_IsRescheduledInsteadOfExpired_WithoutAnyPerDatagramWheelWork()
    {
        var wheel = new UdpSessionWheel(Idle, 0);
        var session = Session(0);
        wheel.Add(session);

        session.Touch(9_000);
        Assert.AreEqual(0, Advance(wheel, 12_000).Count, "Reached its first slot, but traffic at 9s pushed expiry to 19s.");
        Assert.AreEqual(1, wheel.Count);
        Assert.AreEqual(0, Advance(wheel, 18_999).Count);
        Assert.AreEqual(1, Advance(wheel, 19_000).Count);
    }

    [TestMethod]
    public void ClosedSession_IsForgottenWithoutBeingReportedExpired()
    {
        var wheel = new UdpSessionWheel(Idle, 0);
        var session = Session(0);
        wheel.Add(session);
        Assert.IsTrue(session.TryClose());
        Assert.IsFalse(session.TryClose(), "Only one caller closes a session.");

        Assert.AreEqual(0, Advance(wheel, 60_000).Count);
        Assert.AreEqual(0, wheel.Count);
    }

    [TestMethod]
    public void ClockJumpPastAFullRevolution_StillExpiresEverySession()
    {
        var wheel = new UdpSessionWheel(Idle, 0);
        var sessions = new List<UdpSession>();
        for (var i = 0; i < 50; i++)
        {
            var session = Session(i * 100);
            sessions.Add(session);
            wheel.Add(session);
        }

        var expired = Advance(wheel, 3_600_000);

        Assert.AreEqual(50, expired.Count);
        Assert.AreEqual(0, wheel.Count);
    }

    [TestMethod]
    public void ManySessions_ExpireInTheirOwnTick()
    {
        var wheel = new UdpSessionWheel(Idle, 0);
        for (var i = 0; i < 100; i++)
        {
            wheel.Add(Session(i * 100));
        }

        var total = 0;
        for (var now = 0L; now <= 25_000; now += 500)
        {
            var expired = Advance(wheel, now);
            foreach (var session in expired)
            {
                Assert.IsTrue(now >= session.LastActivityMs + 10_000, "Never early.");
                Assert.IsTrue(now <= session.LastActivityMs + 10_000 + wheel.TickMilliseconds + 500, "Never late by more than a tick.");
            }

            total += expired.Count;
        }

        Assert.AreEqual(100, total);
    }

    [TestMethod]
    public void ShortTimeouts_GetAFinerTick()
    {
        Assert.AreEqual(1000, new UdpSessionWheel(TimeSpan.FromSeconds(30), 0).TickMilliseconds);
        Assert.AreEqual(125, new UdpSessionWheel(TimeSpan.FromSeconds(1), 0).TickMilliseconds);
    }
}
