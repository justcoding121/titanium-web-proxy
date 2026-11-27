using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests.Support;

internal static class ChromeHelloInvariants
{
    internal static void AssertMatch(byte[] pinned, byte[] other)
    {
        AssertFrame(pinned);
        AssertFrame(other);
        foreach (ushort type in StableTypes)
        {
            CollectionAssert.AreEqual(Body(pinned, type), Body(other, type), type.ToString("x4"));
        }

        CollectionAssert.AreEqual(Tail(CipherBytes(pinned), 2), Tail(CipherBytes(other), 2));
        CollectionAssert.AreEqual(Tail(Body(pinned, ExtensionType.SupportedGroups), 4), Tail(Body(other, ExtensionType.SupportedGroups), 4));
        CollectionAssert.AreEqual(Tail(Body(pinned, ExtensionType.SignatureAlgorithms), 4), Tail(Body(other, ExtensionType.SignatureAlgorithms), 4));
        CollectionAssert.AreEqual(Tail(Body(pinned, ExtensionType.SupportedVersions), 3), Tail(Body(other, ExtensionType.SupportedVersions), 3));
        AssertGreaseEnds(pinned);
        AssertGreaseEnds(other);
        AssertEchOuter(Body(pinned, ExtensionType.EncryptedClientHello));
        AssertEchOuter(Body(other, ExtensionType.EncryptedClientHello));
        AssertKeyShareLengths(Body(pinned, ExtensionType.KeyShare));
        AssertKeyShareLengths(Body(other, ExtensionType.KeyShare));
        CollectionAssert.AreEquivalent(NonGrease(ExtensionTypes(pinned)), NonGrease(ExtensionTypes(other)));
    }

    private static readonly ushort[] StableTypes =
    [
        ExtensionType.ServerName,
        ExtensionType.ExtendedMasterSecret,
        ExtensionType.ApplicationSettings,
        ExtensionType.ApplicationLayerProtocolNegotiation,
        ExtensionType.SessionTicket,
        ExtensionType.SignedCertificateTimestamp,
        ExtensionType.RenegotiationInfo,
        ExtensionType.PskKeyExchangeModes,
        ExtensionType.CompressCertificate,
        ExtensionType.StatusRequest,
        0xCA34,
        ExtensionType.EcPointFormats,
    ];

    private static void AssertFrame(byte[] record)
    {
        Assert.AreEqual(22, record[0]);
        Assert.AreEqual(0x0301, (record[1] << 8) | record[2]);
        ClientHelloView hello = JaHello.Read(record);
        Assert.AreEqual(0x0303, hello.LegacyVersion);
        Assert.AreEqual(32, hello.SessionId.Length);
        CollectionAssert.AreEqual(new byte[] { 0 }, hello.CompressionMethods.ToArray());
    }

    private static void AssertGreaseEnds(byte[] record)
    {
        ushort[] types = ExtensionTypes(record);
        Assert.IsTrue(Grease.IsGrease(types[0]));
        Assert.IsTrue(Grease.IsGrease(types[^1]));
        Assert.AreNotEqual(types[0], types[^1]);
        Assert.AreEqual(0, Body(record, types[0]).Length);
        Assert.AreEqual(1, Body(record, types[^1]).Length);
        byte[] groups = Body(record, ExtensionType.SupportedGroups);
        byte[] shares = Body(record, ExtensionType.KeyShare);
        Assert.AreEqual(JaHello.ReadU16(groups, 2), JaHello.ReadU16(shares, 2));
    }

    private static void AssertEchOuter(byte[] body)
    {
        Assert.IsTrue(body.Length > 42);
        Assert.AreEqual(0, body[0]);
        Assert.AreEqual(0x0001, JaHello.ReadU16(body, 1));
        Assert.AreEqual(0x0001, JaHello.ReadU16(body, 3));
        Assert.AreEqual(32, JaHello.ReadU16(body, 6));
        int payload = JaHello.ReadU16(body, 40);
        Assert.AreEqual(body.Length, 42 + payload);
        Assert.IsTrue(payload is 144 or 176 or 208 or 240);
    }

    private static void AssertKeyShareLengths(byte[] body)
    {
        int offset = 2;
        Assert.IsTrue(Grease.IsGrease(JaHello.ReadU16(body, offset)));
        Assert.AreEqual(1, JaHello.ReadU16(body, offset + 2));
        offset += 5;
        Assert.AreEqual(0x11EC, JaHello.ReadU16(body, offset));
        Assert.AreEqual(1216, JaHello.ReadU16(body, offset + 2));
        offset += 4 + 1216;
        Assert.AreEqual(0x001D, JaHello.ReadU16(body, offset));
        Assert.AreEqual(32, JaHello.ReadU16(body, offset + 2));
        Assert.AreEqual(body.Length, offset + 4 + 32);
    }

    private static byte[] CipherBytes(byte[] record) => JaHello.Read(record).CipherSuites.ToArray();

    private static byte[] Body(byte[] record, ushort wanted)
    {
        byte[] found = [];
        JaHello.Walk(JaHello.Read(record).Extensions, (type, body) =>
        {
            if (type == wanted)
            {
                found = body.ToArray();
            }
        });
        return found;
    }

    private static ushort[] ExtensionTypes(byte[] record)
    {
        var types = new List<ushort>();
        JaHello.Walk(JaHello.Read(record).Extensions, (type, _) => types.Add(type));
        return types.ToArray();
    }

    private static byte[] Tail(byte[] body, int skip) => body.AsSpan(skip).ToArray();

    private static ushort[] NonGrease(ushort[] types)
    {
        var kept = new List<ushort>();
        foreach (ushort type in types)
        {
            if (!Grease.IsGrease(type))
            {
                kept.Add(type);
            }
        }

        return kept.ToArray();
    }
}
