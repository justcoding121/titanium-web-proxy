using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Dns;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="UdpSvcbDnsResolver" /> DNS response parsing.
///     All tests inject pre-built DNS binary packets to avoid real network calls.
/// </summary>
[TestClass]
public class SvcbDnsResolverTests
{
    // Fixed query ID used in all "valid ID" tests.
    private static readonly byte[] ValidId = [0x12, 0x34];

    // ─────────────────────────────────────────────────────────────────────────
    // DNS packet builder helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds a minimal DNS response with exactly one HTTPS RR answer.
    /// </summary>
    private static byte[] BuildHttpsRrResponse(
        byte[] queryId,
        byte rcode,
        ushort svcPriority,
        string? alpn,      // e.g. "h3" or null
        ushort? altPort,   // e.g. 443 or null
        uint ttlSecs = 300)
    {
        var buf = new System.IO.MemoryStream();

        // ── Header ──────────────────────────────────────────────────────────
        buf.Write(queryId);                     // Transaction ID
        buf.Write(new byte[] { 0x81, (byte)(0x80 | (rcode & 0x0F)) }); // Flags (QR=1, RA=1, RCODE)
        buf.Write(new byte[] { 0x00, 0x01 });   // QDCOUNT = 1
        buf.Write(new byte[] { 0x00, 0x01 });   // ANCOUNT = 1
        buf.Write(new byte[] { 0x00, 0x00 });   // NSCOUNT = 0
        buf.Write(new byte[] { 0x00, 0x00 });   // ARCOUNT = 0

        // ── Question section — "example.com" TYPE=65 CLASS=1 ────────────────
        var qname = EncodeDnsName("example.com");
        buf.Write(qname);
        buf.Write(new byte[] { 0x00, 0x41 });   // QTYPE = 65 (HTTPS)
        buf.Write(new byte[] { 0x00, 0x01 });   // QCLASS = IN

        // ── Answer section ───────────────────────────────────────────────────
        // NAME: compression pointer back to offset 12 (start of question QNAME)
        buf.Write(new byte[] { 0xC0, 0x0C });
        buf.Write(new byte[] { 0x00, 0x41 });   // TYPE = 65
        buf.Write(new byte[] { 0x00, 0x01 });   // CLASS = IN
        // TTL (4 bytes big-endian)
        buf.WriteByte((byte)(ttlSecs >> 24));
        buf.WriteByte((byte)(ttlSecs >> 16));
        buf.WriteByte((byte)(ttlSecs >> 8));
        buf.WriteByte((byte)ttlSecs);

        // RDATA — build first to know length
        var rdata = BuildHttpsRdata(svcPriority, alpn, altPort);
        buf.Write(new byte[] { (byte)(rdata.Length >> 8), (byte)(rdata.Length & 0xFF) }); // RDLENGTH
        buf.Write(rdata);

        return buf.ToArray();
    }

    private static byte[] BuildHttpsRdata(ushort svcPriority, string? alpn, ushort? altPort)
    {
        var rdata = new System.IO.MemoryStream();

        rdata.WriteByte((byte)(svcPriority >> 8));
        rdata.WriteByte((byte)(svcPriority & 0xFF));
        rdata.WriteByte(0x00); // TargetName = "." (root = use owner name)

        // SvcParam key 1 (alpn)
        if (alpn != null)
        {
            var alpnBytes = System.Text.Encoding.ASCII.GetBytes(alpn);
            rdata.Write(new byte[] { 0x00, 0x01 }); // key=1
            var alpnValLen = (ushort)(1 + alpnBytes.Length);
            rdata.Write(new byte[] { (byte)(alpnValLen >> 8), (byte)(alpnValLen & 0xFF) }); // valLen
            rdata.WriteByte((byte)alpnBytes.Length); // ALPN protocol length
            rdata.Write(alpnBytes);
        }

        // SvcParam key 3 (port)
        if (altPort.HasValue)
        {
            rdata.Write(new byte[] { 0x00, 0x03 }); // key=3
            rdata.Write(new byte[] { 0x00, 0x02 }); // valLen=2
            rdata.WriteByte((byte)(altPort.Value >> 8));
            rdata.WriteByte((byte)(altPort.Value & 0xFF));
        }

        return rdata.ToArray();
    }

    private static byte[] EncodeDnsName(string host)
    {
        var buf = new System.IO.MemoryStream();
        foreach (var label in host.Split('.'))
        {
            var lbl = System.Text.Encoding.ASCII.GetBytes(label);
            buf.WriteByte((byte)lbl.Length);
            buf.Write(lbl);
        }
        buf.WriteByte(0); // root label
        return buf.ToArray();
    }

    private static byte[] BuildNxDomainResponse(byte[] queryId)
    {
        var buf = new System.IO.MemoryStream();
        buf.Write(queryId);
        buf.Write(new byte[] { 0x81, 0x83 }); // Flags: QR=1, RA=1, RCODE=3 (NXDOMAIN)
        buf.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT
        buf.Write(new byte[] { 0x00, 0x00 }); // ANCOUNT = 0
        buf.Write(new byte[] { 0x00, 0x00 }); // NSCOUNT
        buf.Write(new byte[] { 0x00, 0x00 }); // ARCOUNT
        buf.Write(EncodeDnsName("example.com"));
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 }); // QTYPE+QCLASS
        return buf.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDnsResponse_ValidH3Alpn_ReturnsSvcbResult()
    {
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result, "Should detect h3 ALPN.");
        Assert.AreEqual(443, result.AltPort, "AltPort should default to queried port when not specified.");
        Assert.IsTrue(result.Ttl.TotalSeconds > 0, "TTL should be positive.");
    }

    [TestMethod]
    public void ParseDnsResponse_WithPortSvcParam_ReturnsAltPort()
    {
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: 8443);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result);
        Assert.AreEqual(8443, result.AltPort);
    }

    [TestMethod]
    public void ParseDnsResponse_TtlReflectedInResult()
    {
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null, ttlSecs: 120);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result);
        Assert.AreEqual(120.0, result.Ttl.TotalSeconds, delta: 0.1);
    }

    [TestMethod]
    public void ParseDnsResponse_QueryIdMismatch_ReturnsNull()
    {
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null);

        var wrongId = new byte[] { 0xFF, 0xFF };
        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), wrongId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "Mismatched query ID should return null.");
    }

    [TestMethod]
    public void ParseDnsResponse_NxDomain_ReturnsNull()
    {
        var response = BuildNxDomainResponse(ValidId);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "NXDOMAIN (RCODE=3) should return null.");
    }

    [TestMethod]
    public void ParseDnsResponse_AliasModeEntry_SvcPriorityZero_ReturnsNull()
    {
        // SvcPriority = 0 means AliasMode — must be skipped (no ALPN params).
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 0, alpn: "h3", altPort: null);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "AliasMode (SvcPriority=0) must be skipped.");
    }

    [TestMethod]
    public void ParseDnsResponse_NoH3InAlpn_ReturnsNull()
    {
        // ALPN contains "h2" but not "h3".
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h2", altPort: null);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "ALPN without h3 should return null.");
    }

    [TestMethod]
    public void ParseDnsResponse_NoAlpnSvcParam_ReturnsNull()
    {
        // No ALPN SvcParam at all.
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: null, altPort: null);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "Missing ALPN SvcParam should return null.");
    }

    [TestMethod]
    public void ParseDnsResponse_TooShort_ReturnsNull()
    {
        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            new byte[] { 0x12, 0x34, 0x81 }.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "Truncated response should return null.");
    }

    [TestMethod]
    public void ParseDnsResponse_TtlClampsToOneHour()
    {
        // TTL = 7200s > 3600s cap → clamped to 3600s.
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null, ttlSecs: 7200);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result);
        Assert.AreEqual(3600.0, result.Ttl.TotalSeconds, delta: 0.1, "TTL should be clamped to 3600s.");
    }
}
