using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class BoundaryLimitTests
{
    [TestMethod]
    public void CertificateListsOfZeroOneAndOneHundredParse()
    {
        AssertEntries(0);
        AssertEntries(1);
        AssertEntries(100);
    }

    [TestMethod]
    public void CertificateListPastOneHundredIsDecodeError()
    {
        Assert.IsFalse(HandshakeMessages.TryReadCertificate(CertificateMessage(101), out _, out HandshakeAlert alert));
        Assert.AreEqual(HandshakeAlert.DecodeError, alert);
    }

    [TestMethod]
    public void CertificateEntryPastTheBufferIsDecodeError()
    {
        byte[] message = CertificateMessage(1);
        message[8] = 0x01;
        Assert.IsFalse(HandshakeMessages.TryReadCertificate(message, out _, out HandshakeAlert alert));
        Assert.AreEqual(HandshakeAlert.DecodeError, alert);
    }

    private static void AssertEntries(int count)
    {
        Assert.IsTrue(HandshakeMessages.TryReadCertificate(CertificateMessage(count), out CertificateView view, out HandshakeAlert alert), alert.ToString());
        int seen = 0;
        TlsReader reader = new(view.CertificateList);
        while (reader.Remaining > 0)
        {
            Assert.IsTrue(reader.TryReadVector24(out ReadOnlySpan<byte> certificate));
            Assert.AreEqual(1, certificate.Length);
            Assert.IsTrue(reader.TryReadVector16(out ReadOnlySpan<byte> extensions));
            Assert.AreEqual(0, extensions.Length);
            seen++;
        }

        Assert.AreEqual(count, seen);
    }

    private static byte[] CertificateMessage(int entries)
    {
        int listLength = entries * 6;
        int bodyLength = 4 + listLength;
        var message = new byte[4 + bodyLength];
        message[0] = (byte)HandshakeType.Certificate;
        message[1] = (byte)(bodyLength >> 16);
        message[2] = (byte)(bodyLength >> 8);
        message[3] = (byte)bodyLength;
        message[5] = (byte)(listLength >> 16);
        message[6] = (byte)(listLength >> 8);
        message[7] = (byte)listLength;
        for (int i = 0; i < entries; i++)
        {
            int at = 8 + (i * 6);
            message[at + 2] = 1;
            message[at + 3] = 0x30;
        }

        return message;
    }
}
