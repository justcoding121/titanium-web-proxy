using System;
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
}
