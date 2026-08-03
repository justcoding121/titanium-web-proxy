using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WinAuthMessageTests
{
    [TestMethod]
    public void Message_Null_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new Message(null!));
    }

    [TestMethod]
    public void Message_TooShort_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Message(new byte[8]));
    }

    [TestMethod]
    public void Message_InvalidHeader_Throws()
    {
        var bytes = new byte[64];
        Assert.ThrowsExactly<ArgumentException>(() => new Message(bytes));
    }

    [TestMethod]
    public void Message_ValidMinimalType3_ParsesDomainAndUser()
    {
        // Minimal NTLM Type3 with NTLMSSP\0 header, length fields, and empty strings.
        var message = new byte[64];
        // header
        message[0] = (byte)'N';
        message[1] = (byte)'T';
        message[2] = (byte)'L';
        message[3] = (byte)'M';
        message[4] = (byte)'S';
        message[5] = (byte)'S';
        message[6] = (byte)'P';
        message[7] = 0;
        // type = 3 at offset 8 (little-endian UInt32)
        message[8] = 3;
        // message length at offset 56 (UInt16)
        message[56] = 64;
        message[57] = 0;
        // domain/user/host lengths/offsets - all empty at offset 64
        // offsets at 32/40/48 as UInt16
        message[32] = 64; // domain offset
        message[40] = 64; // user offset
        message[48] = 64; // host offset

        var parsed = new Message(message);
        Assert.AreEqual(string.Empty, parsed.Domain);
        Assert.AreEqual(string.Empty, parsed.Username);
    }

    [TestMethod]
    public void Message_WrongDeclaredLength_Throws()
    {
        var message = new byte[64];
        WriteNtlmHeader(message, 3);
        message[56] = 32; // declared length != actual
        message[57] = 0;
        Assert.ThrowsExactly<ArgumentException>(() => new Message(message));
    }

    [TestMethod]
    public void Message_UnicodeDomainAndUser_ParsesStrings()
    {
        const string domain = "CORP";
        const string user = "alice";
        var domainBytes = Encoding.Unicode.GetBytes(domain);
        var userBytes = Encoding.Unicode.GetBytes(user);
        var payloadOffset = 64;
        var total = payloadOffset + domainBytes.Length + userBytes.Length;
        var message = new byte[total];
        WriteNtlmHeader(message, 3);
        WriteUInt16(message, 28, (ushort)domainBytes.Length);
        WriteUInt16(message, 32, (ushort)payloadOffset);
        WriteUInt16(message, 36, (ushort)userBytes.Length);
        WriteUInt16(message, 40, (ushort)(payloadOffset + domainBytes.Length));
        WriteUInt16(message, 48, (ushort)total); // host offset (empty)
        WriteUInt16(message, 56, (ushort)total);
        WriteUInt32(message, 60, (uint)Common.NtlmFlags.NegotiateUnicode);
        Buffer.BlockCopy(domainBytes, 0, message, payloadOffset, domainBytes.Length);
        Buffer.BlockCopy(userBytes, 0, message, payloadOffset + domainBytes.Length, userBytes.Length);

        var parsed = new Message(message);
        Assert.AreEqual(domain, parsed.Domain);
        Assert.AreEqual(user, parsed.Username);
        Assert.AreEqual(Common.NtlmFlags.NegotiateUnicode, parsed.Flags);
    }

    [TestMethod]
    public void Message_ShortPayload_UsesDefaultFlags()
    {
        var message = new byte[63];
        WriteNtlmHeader(message, 3);
        WriteUInt16(message, 56, 63);
        WriteUInt16(message, 32, 63);
        WriteUInt16(message, 40, 63);
        WriteUInt16(message, 48, 63);

        var parsed = new Message(message);
        Assert.AreEqual((Common.NtlmFlags)0x8201, parsed.Flags);
    }

    private static void WriteNtlmHeader(byte[] message, uint type)
    {
        message[0] = (byte)'N';
        message[1] = (byte)'T';
        message[2] = (byte)'L';
        message[3] = (byte)'M';
        message[4] = (byte)'S';
        message[5] = (byte)'S';
        message[6] = (byte)'P';
        message[7] = 0;
        WriteUInt32(message, 8, type);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
