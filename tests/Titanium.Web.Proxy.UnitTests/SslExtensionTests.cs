using System;
using System.Linq;
using System.Net.Security;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="SslExtension" /> name/data parsers (SNI, groups, ALPN, versions, etc.).
/// </summary>
[TestClass]
public class SslExtensionTests
{
    private static byte[] Be16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var buf = new byte[len];
        var o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, o, p.Length);
            o += p.Length;
        }

        return buf;
    }

    [TestMethod]
    [DataRow(0, "server_name")]
    [DataRow(5, "status_request")]
    [DataRow(10, "supported_groups")]
    [DataRow(11, "ec_point_formats")]
    [DataRow(13, "signature_algorithms")]
    [DataRow(16, "ALPN")]
    [DataRow(21, "padding")]
    [DataRow(43, "supported_versions")]
    [DataRow(51, "key_share")]
    [DataRow(57, "quic_transport_parameters")]
    [DataRow(0x0a0a, "Reserved (GREASE)")]
    [DataRow(65281, "renegotiation_info")]
    public void Name_KnownExtensionTypes(int type, string expected)
    {
        Assert.AreEqual(expected, new SslExtension(type, ReadOnlyMemory<byte>.Empty, 0).Name);
    }

    [TestMethod]
    public void Name_DraftKeyShare_MapsSeparately()
    {
        Assert.AreEqual("key_share_draft", new SslExtension(40, ReadOnlyMemory<byte>.Empty, 0).Name);
    }

    [TestMethod]
    public void Name_Unknown_UsesHexSuffix()
    {
        Assert.AreEqual("unknown_270f", new SslExtension(9999, ReadOnlyMemory<byte>.Empty, 0).Name);
    }

    [TestMethod]
    public void Data_ServerName_ParsesHostAndJoinsMultiple()
    {
        // list_len | name_type=0 | host_len | host | name_type=0 | host_len | host
        var host1 = Encoding.ASCII.GetBytes("example.com");
        var host2 = Encoding.ASCII.GetBytes("cdn.example.com");
        var entry1 = Concat(new byte[] { 0 }, Be16(host1.Length), host1);
        var entry2 = Concat(new byte[] { 0 }, Be16(host2.Length), host2);
        var list = Concat(entry1, entry2);
        var payload = Concat(Be16(list.Length), list);

        var ext = new SslExtension(0, payload, 0);
        Assert.AreEqual("example.com; cdn.example.com", ext.Data);
    }

    [TestMethod]
    public void Data_StatusRequest_OcspImplicitResponder()
    {
        var ext = new SslExtension(5, new byte[] { 1, 0, 0, 0, 0 }, 0);
        Assert.AreEqual("OCSP - Implicit Responder", ext.Data);
    }

    [TestMethod]
    public void Data_SupportedGroups_NamedAndUnknown()
    {
        // list_len + secp256r1 (0x0017) + x25519 (0x001D) + unknown
        var groups = Concat(Be16(0x0017), Be16(0x001D), Be16(0xDEAD));
        var payload = Concat(Be16(groups.Length), groups);

        var data = new SslExtension(10, payload, 0).Data;
        StringAssert.Contains(data, "secp256r1 [0x17]");
        StringAssert.Contains(data, "x25519 [0x1D]");
        StringAssert.Contains(data, "unknown [0xDEAD]");
    }

    [TestMethod]
    public void Data_SupportedGroups_TooShort_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, new SslExtension(10, new byte[] { 0 }, 0).Data);
    }

    [TestMethod]
    public void Data_EcPointFormats_ReportsKnownFormats()
    {
        // length byte + formats 0 and 1. Parser advances i+=2 (skips every other byte after first).
        var payload = new byte[] { 2, 0, 1 };
        var data = new SslExtension(11, payload, 0).Data;
        StringAssert.Contains(data, "uncompressed [0x0]");
    }

    [TestMethod]
    public void Data_EcPointFormats_UnknownFormat_ReportsHex()
    {
        var payload = new byte[] { 1, 0xFF };
        StringAssert.Contains(new SslExtension(11, payload, 0).Data, "unknown [0xFF]");
    }

    [TestMethod]
    public void Data_SignatureAlgorithms_NamedPairs()
    {
        var algs = Concat(Be16(0x0401), Be16(0x0804), Be16(0x0201));
        var payload = Concat(Be16(algs.Length), algs);

        var data = new SslExtension(13, payload, 0).Data;
        Assert.IsFalse(string.IsNullOrWhiteSpace(data));
        Assert.IsFalse(data.EndsWith(','));
    }

    [TestMethod]
    public void Data_SignatureAlgorithms_ViaType50()
    {
        var algs = Concat(Be16(0x0403));
        var payload = Concat(Be16(algs.Length), algs);
        Assert.IsFalse(string.IsNullOrWhiteSpace(new SslExtension(50, payload, 0).Data));
    }

    [TestMethod]
    public void Data_Alpn_JoinsProtocols_AndMapsKnownAlpns()
    {
        var h11 = Encoding.ASCII.GetBytes("http/1.1");
        var h2 = Encoding.ASCII.GetBytes("h2");
        var h3 = Encoding.ASCII.GetBytes("h3");
        var custom = Encoding.ASCII.GetBytes("custom");
        var list = Concat(
            new[] { (byte)h11.Length }, h11,
            new[] { (byte)h2.Length }, h2,
            new[] { (byte)h3.Length }, h3,
            new[] { (byte)custom.Length }, custom);
        var payload = Concat(Be16(list.Length), list);

        var ext = new SslExtension(16, payload, 0);
        Assert.AreEqual("http/1.1, h2, h3, custom", ext.Data);
        Assert.AreEqual(4, ext.Alpns.Count);
        Assert.AreEqual(SslApplicationProtocol.Http11, ext.Alpns[0]);
        Assert.AreEqual(SslApplicationProtocol.Http2, ext.Alpns[1]);
        Assert.AreEqual(SslApplicationProtocol.Http3, ext.Alpns[2]);
    }

    [TestMethod]
    public void Data_Padding_AllNull_ReportsCount()
    {
        var payload = new byte[16];
        Assert.AreEqual("16 null bytes", new SslExtension(21, payload, 0).Data);
    }

    [TestMethod]
    public void Data_Padding_NonNull_ReturnsHex()
    {
        var data = new SslExtension(21, new byte[] { 0, 1, 0 }, 0).Data;
        Assert.IsFalse(data.Contains("null bytes", StringComparison.Ordinal));
        Assert.IsTrue(data.Length > 0);
    }

    private static readonly string[] expected = new[] { "Tls1.3" };

    [TestMethod]
    public void Data_SupportedVersions_ClientList_AndServerSingle()
    {
        // client list: length byte + Tls1.3 + Tls1.2
        var client = new byte[] { 4, 0x03, 0x04, 0x03, 0x03 };
        var clientData = new SslExtension(43, client, 0).Data;
        StringAssert.Contains(clientData, "Tls1.3");
        StringAssert.Contains(clientData, "Tls1.2");

        // server hello: exactly 2 bytes
        var server = new byte[] { 0x03, 0x04 };
        Assert.AreEqual("Tls1.3", new SslExtension(43, server, 0).Data);
        CollectionAssert.AreEqual(expected, new SslExtension(43, server, 0).Protocols.ToArray());
    }

    [TestMethod]
    public void Data_SupportedVersions_TooShort_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, new SslExtension(43, new byte[] { 0 }, 0).Data);
    }

    [TestMethod]
    public void Data_DraftPadding_ReportsByteCount()
    {
        Assert.AreEqual("3 bytes", new SslExtension(35655, new byte[] { 1, 2, 3 }, 0).Data);
    }

    [TestMethod]
    public void Data_UnknownType_ReturnsHex()
    {
        var data = new SslExtension(9999, new byte[] { 0xAB, 0xCD }, 0).Data;
        Assert.IsTrue(data.Contains("AB", StringComparison.OrdinalIgnoreCase) ||
                      data.Contains("ab", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Data_TruncatedServerName_DoesNotThrow()
    {
        // Claims a host longer than remaining bytes.
        var payload = new byte[] { 0, 8, 0, 0, 5, (byte)'a', (byte)'b' };
        string data = null!;
        try
        {
            data = new SslExtension(0, payload, 0).Data;
        }
        catch (Exception ex)
        {
            Assert.Fail($"SNI Data must not throw on truncated payload: {ex}");
        }

        Assert.IsNotNull(data);
    }

    [TestMethod]
    public void Alpns_TruncatedPayload_DoesNotThrow()
    {
        // ALPN list length claims more than available; protocol length overruns.
        var payload = new byte[] { 0, 10, 5, (byte)'h', (byte)'2' };
        System.Collections.Generic.List<SslApplicationProtocol> alpns = null!;
        try
        {
            alpns = new SslExtension(16, payload, 0).Alpns;
        }
        catch (Exception ex)
        {
            Assert.Fail($"Alpns must not throw on truncated payload: {ex}");
        }

        Assert.IsNotNull(alpns);
        Assert.AreEqual(0, alpns.Count);
    }

    [TestMethod]
    public void Data_TruncatedSignatureAlgorithms_DoesNotThrow()
    {
        var payload = new byte[] { 0, 8, 0x04 }; // length 8, only 1 data byte
        string data = null!;
        try
        {
            data = new SslExtension(13, payload, 0).Data;
        }
        catch (Exception ex)
        {
            Assert.Fail($"Signature algorithms Data must not throw: {ex}");
        }

        Assert.IsNotNull(data);
    }
}
