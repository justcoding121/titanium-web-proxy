using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
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

    // ─────────────────────────────────────────────────────────────────────────
    // TC bit
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDnsResponse_TcBitSet_ReturnsNull()
    {
        // TC bit (bit 1 of byte 2 in flags) set — response is truncated and must be treated as transient.
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null);

        // Patch TC bit into the response (byte 2, bit 1 = 0x02).
        var patched = (byte[])response.Clone();
        patched[2] |= 0x02;

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            patched.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "Truncated (TC=1) response must return null without negative-caching.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RCODE / SERVFAIL
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDnsResponse_ServFail_ReturnsNullAndIsTransient()
    {
        // RCODE=2 (SERVFAIL) — resolver failure, not "origin has no H3".
        var response = BuildNxDomainResponseWithRcode(ValidId, rcode: 2);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNull(result, "SERVFAIL (RCODE=2) should return null.");
        Assert.IsTrue(
            UdpSvcbDnsResolver.ParseDnsResponseIsTransientInternal(
                response.AsSpan(), ValidId.AsSpan(), "example.com", 443),
            "SERVFAIL must be classified as transient, not a definitive negative.");
    }

    [TestMethod]
    public void ParseDnsResponse_NxDomain_IsDefinitiveNegative()
    {
        var response = BuildNxDomainResponseWithRcode(ValidId, rcode: 3);

        Assert.IsNull(UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443));
        Assert.IsFalse(
            UdpSvcbDnsResolver.ParseDnsResponseIsTransientInternal(
                response.AsSpan(), ValidId.AsSpan(), "example.com", 443),
            "NXDOMAIN must remain a definitive negative.");
    }

    [TestMethod]
    public void ParseDnsResponse_Refused_IsTransient()
    {
        var response = BuildNxDomainResponseWithRcode(ValidId, rcode: 5);
        Assert.IsTrue(
            UdpSvcbDnsResolver.ParseDnsResponseIsTransientInternal(
                response.AsSpan(), ValidId.AsSpan(), "example.com", 443));
    }

    [TestMethod]
    [DataRow((byte)1)]
    [DataRow((byte)4)]
    [DataRow((byte)5)]
    [DataRow((byte)15)]
    public void ParseDnsResponse_ErrorRcodes_AreTransient(byte rcode)
    {
        var response = BuildNxDomainResponseWithRcode(ValidId, rcode);

        Assert.IsNull(UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response, ValidId, "example.com", 443));
        Assert.IsTrue(UdpSvcbDnsResolver.ParseDnsResponseIsTransientInternal(
            response, ValidId, "example.com", 443));
    }

    [TestMethod]
    public void ParseDnsResponse_NoAnswers_IsDefinitiveNegative()
    {
        var response = BuildNxDomainResponseWithRcode(ValidId, rcode: 0);

        Assert.IsNull(UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response, ValidId, "example.com", 443));
        Assert.IsFalse(UdpSvcbDnsResolver.ParseDnsResponseIsTransientInternal(
            response, ValidId, "example.com", 443));
    }

    [TestMethod]
    public void ParseDnsResponse_MalformedQuestionName_IsTransient()
    {
        var response = BuildNxDomainResponseWithRcode(ValidId, rcode: 0);
        response[12] = 0x80; // reserved DNS label prefix
        response[7] = 1; // force answer parsing past the question

        Assert.IsTrue(UdpSvcbDnsResolver.ParseDnsResponseIsTransientInternal(
            response, ValidId, "example.com", 443));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SvcPriority selection
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDnsResponse_MultipleRecords_LowestPriorityWins()
    {
        // Two ServiceMode records: priority=10 with port 9000, priority=1 with port 8443.
        // The record with priority=1 (lowest) should win.
        var buf = new System.IO.MemoryStream();

        buf.Write(ValidId);
        buf.Write(new byte[] { 0x81, 0x80 }); // QR=1, RA=1, RCODE=0
        buf.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT = 1
        buf.Write(new byte[] { 0x00, 0x02 }); // ANCOUNT = 2
        buf.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // NS + AR = 0

        var qname = EncodeDnsName("example.com");
        buf.Write(qname);
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 }); // QTYPE=65, QCLASS=IN

        // Record 1: priority=10, altPort=9000
        WriteHttpsRrAnswer(buf, svcPriority: 10, alpn: "h3", altPort: 9000, ttlSecs: 300);
        // Record 2: priority=1, altPort=8443
        WriteHttpsRrAnswer(buf, svcPriority: 1, alpn: "h3", altPort: 8443, ttlSecs: 300);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            buf.ToArray().AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result, "Should select the record with the lowest SvcPriority.");
        Assert.AreEqual(8443, result.AltPort, "Lower SvcPriority (1) wins over higher (10).");
    }

    [TestMethod]
    public void ParseDnsResponse_AliasModeFirst_ServiceModeSecond_ReturnsServiceMode()
    {
        // AliasMode (priority=0) followed by a ServiceMode (priority=1).
        // ServiceMode should win even if AliasMode appears first.
        var buf = new System.IO.MemoryStream();

        buf.Write(ValidId);
        buf.Write(new byte[] { 0x81, 0x80 });
        buf.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT
        buf.Write(new byte[] { 0x00, 0x02 }); // ANCOUNT = 2
        buf.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        var qname = EncodeDnsName("example.com");
        buf.Write(qname);
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 });

        // AliasMode (priority=0)
        WriteHttpsRrAnswer(buf, svcPriority: 0, alpn: null, altPort: null, ttlSecs: 60);
        // ServiceMode (priority=1)
        WriteHttpsRrAnswer(buf, svcPriority: 1, alpn: "h3", altPort: 443, ttlSecs: 300);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            buf.ToArray().AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result, "ServiceMode record should be returned even when AliasMode appears first.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TargetName
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDnsResponse_TargetName_RootLabel_Null()
    {
        // TargetName = "." (single 0x00 byte in RDATA) means "use owner name" → null in SvcbResult.
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result);
        Assert.IsNull(result.TargetName, "TargetName '.' should be normalised to null.");
    }

    [TestMethod]
    public void ParseDnsResponse_TargetName_ExplicitHost_Extracted()
    {
        // Build a ServiceMode record with TargetName = "target.example.com" (no compression).
        var response = BuildHttpsRrResponseWithTargetName(
            ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: null, targetName: "target.example.com");

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result);
        Assert.AreEqual("target.example.com", result.TargetName, "Explicit TargetName must be preserved.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Port SvcParam edge cases
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseDnsResponse_PortZeroInSvcParam_FallsBackToQueriedPort()
    {
        // Port SvcParam value 0 is invalid; the resolver should fall back to the queried port.
        var response = BuildHttpsRrResponse(ValidId, rcode: 0, svcPriority: 1, alpn: "h3", altPort: 0);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            response.AsSpan(), ValidId.AsSpan(), "example.com", 443);

        Assert.IsNotNull(result, "Record with port=0 should still be returned (ALPN=h3 is valid).");
        Assert.AreEqual(443, result.AltPort, "Port 0 is invalid; resolver must use the queried port (443).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TrimExpired (negative-result cache bounding)
    // ─────────────────────────────────────────────────────────────────────────

    private static ConcurrentDictionary<string, DateTime> GetNegativeCache(UdpSvcbDnsResolver resolver)
    {
        var field = typeof(UdpSvcbDnsResolver).GetField("_negativeCache",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field, "_negativeCache field not found via reflection");
        return (ConcurrentDictionary<string, DateTime>)field.GetValue(resolver)!;
    }

    /// <summary>
    ///     The SVCB negative cache set TTLs but never removed expired keys, so it grew unbounded for
    ///     the lifetime of the process purely from distinct hosts that failed to resolve H3 capability
    ///     once. <see cref="UdpSvcbDnsResolver.TrimExpired" /> must actually remove entries whose TTL
    ///     has elapsed.
    /// </summary>
    [TestMethod]
    public void TrimExpired_RemovesExpiredNegativeCacheEntries()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 53));
        var cache = GetNegativeCache(resolver);

        cache["expired.example:443"] = DateTime.UtcNow.AddMinutes(-1);
        cache["still-valid.example:443"] = DateTime.UtcNow.AddMinutes(5);

        resolver.TrimExpired();

        Assert.IsFalse(cache.ContainsKey("expired.example:443"),
            "Expired negative-cache entries must be removed by TrimExpired.");
        Assert.IsTrue(cache.ContainsKey("still-valid.example:443"),
            "Non-expired negative-cache entries must be kept by TrimExpired.");
    }

    /// <summary>
    ///     Backstop hard cap: even if nothing has expired yet (e.g. a burst of distinct hosts within
    ///     one TTL window), the negative cache must never grow past the configured hard cap.
    /// </summary>
    [TestMethod]
    public void TrimExpired_EnforcesHardCap_WhenNothingHasExpiredYet()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 53));
        var cache = GetNegativeCache(resolver);

        var capField = typeof(UdpSvcbDnsResolver).GetField("MaxNegativeCacheEntries",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(capField, "MaxNegativeCacheEntries field not found via reflection");
        var cap = (int)capField.GetValue(null)!;

        // None of these are expired, but the count exceeds the hard cap.
        for (var i = 0; i < cap + 10; i++)
            cache[$"host{i}.example:443"] = DateTime.UtcNow.AddMinutes(5);

        resolver.TrimExpired();

        Assert.IsTrue(cache.Count <= cap,
            $"negative cache must never exceed the hard cap of {cap}, had {cache.Count}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Coalesce / neg-cache / transient / backoff (no real DNS)
    // ─────────────────────────────────────────────────────────────────────────

    private static ConcurrentDictionary<string, DateTime> GetTransientCache(UdpSvcbDnsResolver resolver)
    {
        var field = typeof(UdpSvcbDnsResolver).GetField("_transientCache",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field);
        return (ConcurrentDictionary<string, DateTime>)field.GetValue(resolver)!;
    }

    private static object GetInflight(UdpSvcbDnsResolver resolver)
    {
        var field = typeof(UdpSvcbDnsResolver).GetField("_inflight",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field);
        return field.GetValue(resolver)!;
    }

    private static int GetInflightCount(UdpSvcbDnsResolver resolver) =>
        (int)GetInflight(resolver).GetType().GetProperty("Count")!.GetValue(GetInflight(resolver))!;

    private static void InflightTryAdd(UdpSvcbDnsResolver resolver, string key, object completedTask)
    {
        var dict = GetInflight(resolver);
        var tryAdd = dict.GetType().GetMethod("TryAdd")!;
        var added = (bool)tryAdd.Invoke(dict, new[] { key, completedTask })!;
        Assert.IsTrue(added);
    }

    private static void SetBackoffState(UdpSvcbDnsResolver resolver, int consecutiveFailures,
        DateTime backoffUntilUtc, int halfOpenInFlight = 0)
    {
        typeof(UdpSvcbDnsResolver).GetField("_consecutiveTransientFailures",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(resolver, consecutiveFailures);
        typeof(UdpSvcbDnsResolver).GetField("_resolverBackoffUntilUtc",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(resolver, backoffUntilUtc);
        typeof(UdpSvcbDnsResolver).GetField("_halfOpenProbeInFlight",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(resolver, halfOpenInFlight);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TryGetH3CapabilityAsync_NegativeCacheHit_SkipsDnsAndReturnsNull()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 1));
        GetNegativeCache(resolver)["neg.example:443"] = DateTime.UtcNow.AddMinutes(5);

        var result = await resolver.TryGetH3CapabilityAsync("neg.example", 443, default);

        Assert.IsNull(result);
        Assert.AreEqual(0, GetInflightCount(resolver), "Negative-cache hit must not start a DNS probe.");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TryGetH3CapabilityAsync_TransientCacheHit_ReturnsNullWithoutProbe()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 1));
        GetTransientCache(resolver)["tmp.example:443"] = DateTime.UtcNow.AddSeconds(30);

        var result = await resolver.TryGetH3CapabilityAsync("tmp.example", 443, default);

        Assert.IsNull(result);
        Assert.AreEqual(0, GetInflightCount(resolver));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TryGetH3CapabilityAsync_ConcurrentCallers_ShareSingleInflightTask()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 1));
        var key = "coalesce.example:443";

        // SvcbQueryState is a private nested type; build a completed Task<SvcbQueryState> via reflection.
        var stateType = typeof(UdpSvcbDnsResolver).GetNestedType("SvcbQueryState", BindingFlags.NonPublic);
        Assert.IsNotNull(stateType);
        var stateCtor = stateType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        var state = stateCtor.Invoke(new object?[] { null });
        var completed = typeof(System.Threading.Tasks.Task).GetMethod(nameof(System.Threading.Tasks.Task.FromResult))!
            .MakeGenericMethod(stateType)
            .Invoke(null, new[] { state })!;

        InflightTryAdd(resolver, key, completed);

        var a = resolver.TryGetH3CapabilityAsync("coalesce.example", 443, default);
        var b = resolver.TryGetH3CapabilityAsync("coalesce.example", 443, default);
        var ra = await a;
        var rb = await b;

        Assert.IsNull(ra);
        Assert.IsNull(rb);
        Assert.AreEqual(1, GetInflightCount(resolver), "Both waiters must share the pre-seeded inflight task.");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TryGetH3CapabilityAsync_DuringBackoff_RejectsAllProbes()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 1));
        SetBackoffState(resolver, consecutiveFailures: 2, backoffUntilUtc: DateTime.UtcNow.AddMinutes(1));

        var result = await resolver.TryGetH3CapabilityAsync("backoff.example", 443, default);

        Assert.IsNull(result);
        Assert.AreEqual(0, GetInflightCount(resolver));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task TryGetH3CapabilityAsync_HalfOpenAlreadyInFlight_RejectsSecondCaller()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 1));
        // Backoff window elapsed, but a half-open probe is already reserved.
        SetBackoffState(resolver, consecutiveFailures: 1, backoffUntilUtc: DateTime.UtcNow.AddMinutes(-1),
            halfOpenInFlight: 1);

        var result = await resolver.TryGetH3CapabilityAsync("halfopen.example", 443, default);

        Assert.IsNull(result);
        Assert.AreEqual(0, GetInflightCount(resolver));
    }

    [TestMethod]
    public void NoteQueryTransientFailure_SetsBackoffWindow_WithExponentialGrowth()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 1));
        var note = typeof(UdpSvcbDnsResolver).GetMethod("NoteQueryTransientFailure",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(note);

        note.Invoke(resolver, null);
        var firstUntil = (DateTime)typeof(UdpSvcbDnsResolver)
            .GetField("_resolverBackoffUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(resolver)!;
        var firstFailures = (int)typeof(UdpSvcbDnsResolver)
            .GetField("_consecutiveTransientFailures", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(resolver)!;

        Assert.AreEqual(1, firstFailures);
        Assert.IsTrue(firstUntil > DateTime.UtcNow,
            "First transient failure must open a future backoff window.");

        // Force the first window into the past so the second Note clearly advances absolute time.
        typeof(UdpSvcbDnsResolver).GetField("_resolverBackoffUntilUtc",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(resolver, DateTime.UtcNow.AddSeconds(-1));

        note.Invoke(resolver, null);
        var secondUntil = (DateTime)typeof(UdpSvcbDnsResolver)
            .GetField("_resolverBackoffUntilUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(resolver)!;
        var secondFailures = (int)typeof(UdpSvcbDnsResolver)
            .GetField("_consecutiveTransientFailures", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(resolver)!;

        Assert.AreEqual(2, secondFailures);
        Assert.IsTrue(secondUntil > DateTime.UtcNow);
        Assert.IsTrue(secondUntil <= DateTime.UtcNow.AddMinutes(5).AddSeconds(1),
            "Backoff must stay within the 5-minute cap.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Additional packet builder helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static byte[] BuildNxDomainResponseWithRcode(byte[] queryId, byte rcode)
    {
        var buf = new System.IO.MemoryStream();
        buf.Write(queryId);
        buf.Write(new byte[] { 0x81, (byte)(0x80 | (rcode & 0x0F)) });
        buf.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT
        buf.Write(new byte[] { 0x00, 0x00 }); // ANCOUNT = 0
        buf.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // NS + AR
        buf.Write(EncodeDnsName("example.com"));
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 }); // QTYPE+QCLASS
        return buf.ToArray();
    }

    /// <summary>
    ///     Writes a single HTTPS RR answer record (with compression pointer name) into <paramref name="buf"/>.
    /// </summary>
    private static void WriteHttpsRrAnswer(System.IO.MemoryStream buf, ushort svcPriority,
        string? alpn, ushort? altPort, uint ttlSecs)
    {
        buf.Write(new byte[] { 0xC0, 0x0C }); // NAME: pointer to offset 12 (question QNAME)
        buf.Write(new byte[] { 0x00, 0x41 }); // TYPE = 65
        buf.Write(new byte[] { 0x00, 0x01 }); // CLASS = IN
        buf.WriteByte((byte)(ttlSecs >> 24));
        buf.WriteByte((byte)(ttlSecs >> 16));
        buf.WriteByte((byte)(ttlSecs >> 8));
        buf.WriteByte((byte)ttlSecs);

        var rdata = BuildHttpsRdata(svcPriority, alpn, altPort);
        buf.Write(new byte[] { (byte)(rdata.Length >> 8), (byte)(rdata.Length & 0xFF) });
        buf.Write(rdata);
    }

    private static byte[] BuildHttpsRrResponseWithTargetName(
        byte[] queryId, byte rcode, ushort svcPriority, string? alpn, ushort? altPort,
        string targetName, uint ttlSecs = 300)
    {
        var buf = new System.IO.MemoryStream();

        buf.Write(queryId);
        buf.Write(new byte[] { 0x81, (byte)(0x80 | (rcode & 0x0F)) });
        buf.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT = 1
        buf.Write(new byte[] { 0x00, 0x01 }); // ANCOUNT = 1
        buf.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        var qname = EncodeDnsName("example.com");
        buf.Write(qname);
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 });

        buf.Write(new byte[] { 0xC0, 0x0C }); // NAME pointer
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 }); // TYPE, CLASS
        buf.WriteByte((byte)(ttlSecs >> 24));
        buf.WriteByte((byte)(ttlSecs >> 16));
        buf.WriteByte((byte)(ttlSecs >> 8));
        buf.WriteByte((byte)ttlSecs);

        var rdata = BuildHttpsRdataWithTargetName(svcPriority, targetName, alpn, altPort);
        buf.Write(new byte[] { (byte)(rdata.Length >> 8), (byte)(rdata.Length & 0xFF) });
        buf.Write(rdata);

        return buf.ToArray();
    }

    private static byte[] BuildHttpsRdataWithTargetName(
        ushort svcPriority, string targetName, string? alpn, ushort? altPort)
    {
        var rdata = new System.IO.MemoryStream();
        rdata.WriteByte((byte)(svcPriority >> 8));
        rdata.WriteByte((byte)(svcPriority & 0xFF));

        // Write TargetName as label-encoded (no compression)
        rdata.Write(EncodeDnsName(targetName));

        if (alpn != null)
        {
            var alpnBytes = System.Text.Encoding.ASCII.GetBytes(alpn);
            rdata.Write(new byte[] { 0x00, 0x01 }); // key=1
            var alpnValLen = (ushort)(1 + alpnBytes.Length);
            rdata.Write(new byte[] { (byte)(alpnValLen >> 8), (byte)(alpnValLen & 0xFF) });
            rdata.WriteByte((byte)alpnBytes.Length);
            rdata.Write(alpnBytes);
        }

        if (altPort.HasValue)
        {
            rdata.Write(new byte[] { 0x00, 0x03, 0x00, 0x02 });
            rdata.WriteByte((byte)(altPort.Value >> 8));
            rdata.WriteByte((byte)(altPort.Value & 0xFF));
        }

        return rdata.ToArray();
    }
}
