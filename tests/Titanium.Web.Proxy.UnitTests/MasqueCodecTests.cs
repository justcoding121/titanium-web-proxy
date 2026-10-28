#pragma warning disable TWP001
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class MasqueCodecTests
{
    [TestMethod]
    public void Datagram_RoundTrips_OneByteAtATime()
    {
        var payload = "hello-udp"u8.ToArray();
        Span<byte> encoded = stackalloc byte[64];
        var written = CapsuleReader.WriteDatagram(encoded, payload);
        Assert.IsTrue(written > payload.Length);
        var reader = new CapsuleReader();
        var got = new List<byte[]>();
        for (var i = 0; i < written; i++)
        {
            reader.Feed(encoded.Slice(i, 1), span => got.Add(span.ToArray()));
        }

        Assert.AreEqual(1, got.Count);
        CollectionAssert.AreEqual(payload, got[0]);
    }

    [TestMethod]
    public void UnknownType_AndNonZeroContext_AreSkipped()
    {
        Span<byte> buffer = stackalloc byte[32];
        var written = CapsuleReader.WriteVarint(buffer, 7);
        written += CapsuleReader.WriteVarint(buffer[written..], 3);
        buffer[written++] = 1;
        buffer[written++] = 2;
        buffer[written++] = 3;
        written += CapsuleReader.WriteDatagram(buffer[written..], "ab"u8);
        var reader = new CapsuleReader();
        var got = new List<byte[]>();
        reader.Feed(buffer[..written], span => got.Add(span.ToArray()));
        Assert.AreEqual(1, got.Count);
        CollectionAssert.AreEqual("ab"u8.ToArray(), got[0]);
    }

    [TestMethod]
    public void NonZeroContextId_IsCountedAsDropped()
    {
        Span<byte> buffer = stackalloc byte[16];
        var written = CapsuleReader.WriteVarint(buffer, 0);
        written += CapsuleReader.WriteVarint(buffer[written..], 2);
        written += CapsuleReader.WriteVarint(buffer[written..], 1);
        buffer[written++] = 9;
        var reader = new CapsuleReader();
        var calls = 0;
        reader.Feed(buffer[..written], _ => calls++);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, reader.Dropped);
    }

    [TestMethod]
    public void OversizeDeclaredLength_IsSkippedWithoutBufferingIt()
    {
        Span<byte> header = stackalloc byte[16];
        var written = CapsuleReader.WriteVarint(header, 0);
        written += CapsuleReader.WriteVarint(header[written..], 10_000);
        var blob = new byte[written + 10_000];
        header[..written].CopyTo(blob);
        var reader = new CapsuleReader(maxCapsuleBytes: 64);
        var calls = 0;
        reader.Feed(blob, _ => calls++);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, reader.Dropped);
        Span<byte> next = stackalloc byte[32];
        var nextLen = CapsuleReader.WriteDatagram(next, "z"u8);
        reader.Feed(next[..nextLen], span =>
        {
            calls++;
            Assert.AreEqual("z", Encoding.ASCII.GetString(span));
        });
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void Varints_CoverOneTwoFourAndEightByteForms()
    {
        long[] values = [0, 63, 64, 16383, 16384, 1L << 30, (1L << 62) - 1];
        var encoded = new byte[8];
        var capsule = new byte[24];
        foreach (var value in values)
        {
            var size = CapsuleReader.WriteVarint(encoded, value);
            Assert.IsTrue(size is 1 or 2 or 4 or 8);
            var n = 0;
            n += CapsuleReader.WriteVarint(capsule, 9);
            n += CapsuleReader.WriteVarint(capsule.AsSpan(n), size);
            encoded.AsSpan(0, size).CopyTo(capsule.AsSpan(n));
            n += size;
            var reader = new CapsuleReader();
            var seen = 0;
            reader.Feed(capsule.AsSpan(0, n), _ => seen++);
            Assert.AreEqual(0, seen, "a non-datagram capsule must not emit a payload");
        }
    }

    [DataTestMethod]
    [DataRow("/.well-known/masque/udp/example.com/443/", "example.com", 443)]
    [DataRow("/.well-known/masque/udp/127.0.0.1/9/", "127.0.0.1", 9)]
    [DataRow("/.well-known/masque/udp/2001%3Adb8%3A%3A1/443/", "2001:db8::1", 443)]
    public void Path_ParsesHostAndPort(string path, string host, int port)
    {
        Assert.IsTrue(ConnectUdpPath.TryParse(path, out var parsedHost, out var parsedPort));
        Assert.AreEqual(host, parsedHost);
        Assert.AreEqual(port, parsedPort);
    }

    [DataTestMethod]
    [DataRow("/.well-known/masque/udp/example.com/443")]
    [DataRow("/.well-known/masque/udp//443/")]
    [DataRow("/.well-known/masque/udp/example.com/0/")]
    [DataRow("/.well-known/masque/udp/a/443/extra/")]
    [DataRow("/.well-known/masque/udp/%2F/443/")]
    [DataRow("/.well-known/masque/udp/%00/443/")]
    [DataRow("/.well-known/masque/udp/%253A/443/")]
    public void Path_RejectsMalformedTemplates(string path)
    {
        Assert.IsFalse(ConnectUdpPath.TryParse(path, out _, out _));
    }

    [TestMethod]
    public void Policy_PinsTheFirstAllowedAddress_AndSkipsPrivateOnes()
    {
        var allowed = MasqueTargetPolicy.Select("example.com", 443, [443], blockPrivate: true,
            _ => [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("1.2.3.4")]);
        Assert.AreEqual(IPAddress.Parse("1.2.3.4"), allowed.Address);

        var blocked = MasqueTargetPolicy.Select("127.0.0.1", 443, [443], blockPrivate: true, _ => []);
        Assert.AreEqual("blocked", blocked.Rejection);

        var port = MasqueTargetPolicy.Select("1.2.3.4", 53, [443], blockPrivate: false, _ => []);
        Assert.AreEqual("port", port.Rejection);
    }

    [TestMethod]
    public void Path_DecodesUtf8_KeepsATrailingDot_AndUnwrapsBracketedIpv6()
    {
        Assert.IsTrue(ConnectUdpPath.TryParse("/.well-known/masque/udp/caf%C3%A9/443/", out var host, out var port));
        Assert.AreEqual("café", host);
        Assert.AreEqual(443, port);

        Assert.IsTrue(ConnectUdpPath.TryParse("/.well-known/masque/udp/localhost./443/", out host, out port));
        Assert.AreEqual("localhost.", host);

        Assert.IsTrue(ConnectUdpPath.TryParse(
            "/.well-known/masque/udp/%5B2001%3Adb8%3A%3A1%5D/443/", out host, out port));
        Assert.AreEqual("2001:db8::1", host);
    }

    [TestMethod]
    public void Path_RejectsATruncatedEscape_ABadEscape_AndAHostOver255Bytes()
    {
        Assert.IsFalse(ConnectUdpPath.TryParse("/.well-known/masque/udp/%4/443/", out _, out _));
        Assert.IsFalse(ConnectUdpPath.TryParse("/.well-known/masque/udp/%zz/443/", out _, out _));
        var huge = new string('a', 256);
        Assert.IsFalse(ConnectUdpPath.TryParse($"/.well-known/masque/udp/{huge}/443/", out _, out _));
    }

    [TestMethod]
    public void CapsuleProtocol_AllowsOmittedAndParameters_AndRejectsFalse()
    {
        Assert.IsTrue(MasqueRequestChecks.CapsuleProtocolAccepted(null));
        Assert.IsTrue(MasqueRequestChecks.CapsuleProtocolAccepted(" ?1 "));
        Assert.IsTrue(MasqueRequestChecks.CapsuleProtocolAccepted("?1;a=1"));
        Assert.IsFalse(MasqueRequestChecks.CapsuleProtocolAccepted("?0"));
        Assert.IsFalse(MasqueRequestChecks.CapsuleProtocolAccepted("1"));
    }

    [TestMethod]
    public void ClientKey_SharesASlash64_AndReleaseRemovesTheSlot()
    {
        var first = IPAddress.Parse("2001:db8:1:2:3:4:5:6");
        var second = IPAddress.Parse("2001:db8:1:2:9:9:9:9");
        Assert.AreEqual(MasqueProxyEndPoint.ClientKey(first), MasqueProxyEndPoint.ClientKey(second));
        Assert.AreEqual(IPAddress.Parse("203.0.113.5"),
            MasqueProxyEndPoint.ClientKey(IPAddress.Parse("::ffff:203.0.113.5")));

        var endpoint = new MasqueProxyEndPoint(IPAddress.Loopback, 0)
        {
            Options = new MasqueOptions { MaxTunnels = 4, MaxTunnelsPerClient = 1 },
        };
        var key = MasqueProxyEndPoint.ClientKey(first);
        Assert.IsTrue(endpoint.TryAdmit(key));
        Assert.IsFalse(endpoint.TryAdmit(MasqueProxyEndPoint.ClientKey(second)));
        endpoint.ReleaseTunnel(key);
        Assert.AreEqual(0, endpoint.ClientSlotsForTests);
        Assert.AreEqual(0, endpoint.Active);
    }

    [TestMethod]
    public void Policy_SelectAll_KeepsEveryPublicAddress()
    {
        var all = MasqueTargetPolicy.SelectAll(443, [443], blockPrivate: true,
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.5")],
            out var rejection);
        Assert.IsNull(rejection);
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual(IPAddress.Parse("1.2.3.4"), all[0]);
    }

    [TestMethod]
    public void DatagramBudget_DoesNotWrapTheByteCounter()
    {
        var budget = new DatagramBudget(int.MaxValue, int.MaxValue, () => 10_000L);
        Assert.IsTrue(budget.TryTake(int.MaxValue - 5));
        Assert.IsFalse(budget.TryTake(10));
    }

    [TestMethod]
    public void Admission_ReturnsToZero_OnEveryRelease()
    {
        var endpoint = new MasqueProxyEndPoint(IPAddress.Loopback, 0)
        {
            Options = new MasqueOptions { MaxTunnels = 2, MaxTunnelsPerClient = 1 },
        };
        var client = IPAddress.Parse("203.0.113.5");
        Assert.IsTrue(endpoint.TryAdmit(client));
        Assert.IsFalse(endpoint.TryAdmit(client));
        endpoint.ReleaseTunnel(client);
        Assert.AreEqual(0, endpoint.Active);
        Assert.IsTrue(endpoint.TryAdmit(client));
        endpoint.ReleaseTunnel(client);
        Assert.AreEqual(0, endpoint.Active);
    }

    [TestMethod]
    public void DatagramBudget_ResetsWhenTheClockAdvances()
    {
        long now = 10_000;
        var budget = new DatagramBudget(1, 100, () => now);
        Assert.IsTrue(budget.TryTake(10));
        Assert.IsFalse(budget.TryTake(10));
        now += 1000;
        Assert.IsTrue(budget.TryTake(10));
        Assert.IsFalse(budget.TryTake(100));
    }

    [TestMethod]
    public void ClientQueue_DropsWhenTheBoundIsFull()
    {
        var inbound = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        var channel = new CapsuleDatagramChannel(inbound.Reader, Stream.Null);
        var accepted = 0;
        var dropped = 0;
        var payload = new byte[] { 1 };
        for (var i = 0; i < 80; i++)
        {
            if (channel.TryQueueToClient(payload))
            {
                accepted++;
            }
            else
            {
                dropped++;
            }
        }

        Assert.AreEqual(64, accepted);
        Assert.AreEqual(16, dropped);
    }
}
