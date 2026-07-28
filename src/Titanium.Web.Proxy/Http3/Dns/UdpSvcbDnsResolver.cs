#pragma warning disable CA1416
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Dns;

/// <summary>
///     Production <see cref="IHttpsSvcbResolver" /> that sends a single UDP DNS query for HTTPS RR
///     (type 65) per host:port and parses the ALPN SvcParam (key 1) looking for "h3".
/// </summary>
/// <remarks>
///     Design points:
///     <list type="bullet">
///       <item>Per-query <see cref="Socket" /> (UDP) — lightweight, avoids shared-socket threading issues.</item>
///       <item>DNS query-ID validation — random 2-byte ID embedded in query, verified in response.</item>
///       <item>RCODE check — NXDOMAIN/SERVFAIL returns <see langword="null" /> immediately.</item>
///       <item>No retry — a dropped packet falls through to TCP; the next request probes again.</item>
///       <item>Query coalescing — concurrent requests for the same host:port share one UDP round-trip.</item>
///       <item>Negative caching — 1-minute TTL for misses prevents a DNS query per request to H2-only origins.</item>
///     </list>
/// </remarks>
internal sealed class UdpSvcbDnsResolver : IHttpsSvcbResolver
{
    private const int DnsQueryTimeoutMs = 500;
    private const ushort DnsTypeHttps = 65;
    private const ushort DnsClassIn = 1;

    private readonly IPEndPoint _dnsServerEndPoint;

    // Coalescing: concurrent requests for the same host:port share one Task.
    private readonly ConcurrentDictionary<string, Task<SvcbResult?>> _inflight = new();

    // Negative cache: DateTime.UtcNow expiry per host:port key.
    private readonly ConcurrentDictionary<string, DateTime> _negativeCache = new();

    private readonly TimeSpan _negativeCacheTtl;

    internal UdpSvcbDnsResolver(IPEndPoint dnsServerEndPoint, TimeSpan? negativeCacheTtl = null)
    {
        _dnsServerEndPoint = dnsServerEndPoint;
        _negativeCacheTtl = negativeCacheTtl ?? TimeSpan.FromMinutes(1);
    }

    /// <inheritdoc />
    public Task<SvcbResult?> TryGetH3CapabilityAsync(string host, int port, CancellationToken ct)
    {
        var key = $"{host}:{port}";

        // Fast path: negative cache hit.
        if (_negativeCache.TryGetValue(key, out var expires) && DateTime.UtcNow < expires)
            return Task.FromResult<SvcbResult?>(null);

        // Coalesce concurrent queries: if one is in flight, reuse its Task.
        return _inflight.GetOrAdd(key, _ => RunQueryAndCacheAsync(key, host, port, ct));
    }

    private async Task<SvcbResult?> RunQueryAndCacheAsync(string key, string host, int port, CancellationToken ct)
    {
        try
        {
            var result = await QueryAsync(host, port, ct);
            if (result == null)
                _negativeCache[key] = DateTime.UtcNow + _negativeCacheTtl;
            return result;
        }
        catch
        {
            // Any error (timeout, socket, parse) is treated as a negative result.
            _negativeCache[key] = DateTime.UtcNow + _negativeCacheTtl;
            return null;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<SvcbResult?> QueryAsync(string host, int port, CancellationToken ct)
    {
        // Build the query.
        Span<byte> queryId = stackalloc byte[2];
        RandomNumberGenerator.Fill(queryId);

        var queryPacket = BuildDnsQuery(queryId, host, DnsTypeHttps);

        // Send via a fresh per-query UDP socket.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        using var timeoutCts = new CancellationTokenSource(DnsQueryTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        await socket.SendToAsync(queryPacket, SocketFlags.None, _dnsServerEndPoint, linked.Token);

        var responseBuffer = new byte[4096];
        var received = await socket.ReceiveAsync(responseBuffer.AsMemory(), SocketFlags.None, linked.Token);

        return ParseDnsResponse(responseBuffer.AsSpan(0, received), queryId, host, port);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DNS wire-format builder
    // ─────────────────────────────────────────────────────────────────────────

    private static byte[] BuildDnsQuery(ReadOnlySpan<byte> queryId, string host, ushort rrType)
    {
        // Estimate: header (12) + encoded name + 4 (QTYPE + QCLASS)
        var buf = new System.IO.MemoryStream(64);

        // Transaction ID
        buf.Write(queryId);
        // Flags: standard query, RD=1
        buf.Write(stackalloc byte[] { 0x01, 0x00 });
        // QDCOUNT = 1
        buf.Write(stackalloc byte[] { 0x00, 0x01 });
        // ANCOUNT, NSCOUNT, ARCOUNT = 0
        buf.Write(stackalloc byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // QNAME: label-encoded host
        WriteDnsName(buf, host);

        // QTYPE
        buf.WriteByte((byte)(rrType >> 8));
        buf.WriteByte((byte)(rrType & 0xFF));
        // QCLASS IN
        buf.WriteByte((byte)(DnsClassIn >> 8));
        buf.WriteByte((byte)(DnsClassIn & 0xFF));

        return buf.ToArray();
    }

    private static void WriteDnsName(System.IO.Stream buf, string name)
    {
        // Remove trailing dot if present.
        var labels = name.TrimEnd('.').Split('.');
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buf.WriteByte((byte)bytes.Length);
            buf.Write(bytes);
        }
        buf.WriteByte(0); // root label terminator
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DNS response parser
    // ─────────────────────────────────────────────────────────────────────────

    private static SvcbResult? ParseDnsResponse(
        ReadOnlySpan<byte> response, ReadOnlySpan<byte> expectedId, string host, int queriedPort)
    {
        if (response.Length < 12) return null;

        // Validate transaction ID.
        if (response[0] != expectedId[0] || response[1] != expectedId[1]) return null;

        // Flags byte 2: QR=1, check RCODE (lower 4 bits of byte 3).
        var rcode = response[3] & 0x0F;
        if (rcode != 0) return null; // NXDOMAIN (3) / SERVFAIL (2) / etc.

        int anCount = (response[6] << 8) | response[7];
        if (anCount == 0) return null;

        // Skip past the question section.
        int offset = 12;
        if (!SkipDnsName(response, ref offset)) return null;
        offset += 4; // QTYPE + QCLASS

        // Scan answer section.
        for (int i = 0; i < anCount && offset < response.Length; i++)
        {
            if (!SkipDnsName(response, ref offset)) return null;
            if (offset + 10 > response.Length) return null;

            ushort rrType = (ushort)((response[offset] << 8) | response[offset + 1]);
            // ushort rrClass = ...
            uint ttlSecs = (uint)((response[offset + 4] << 24) | (response[offset + 5] << 16)
                                 | (response[offset + 6] << 8) | response[offset + 7]);
            int rdLen = (response[offset + 8] << 8) | response[offset + 9];
            offset += 10;

            if (offset + rdLen > response.Length) return null;

            if (rrType == DnsTypeHttps)
            {
                var result = ParseHttpsRr(response.Slice(offset, rdLen), queriedPort, ttlSecs);
                if (result != null) return result;
            }

            offset += rdLen;
        }

        return null;
    }

    /// <summary>
    ///     Parses an HTTPS RR RDATA section per RFC 9460. Returns non-null only when
    ///     SvcPriority &gt; 0 (skip AliasMode) and the <c>alpn</c> SvcParam (key 1) contains "h3".
    /// </summary>
    private static SvcbResult? ParseHttpsRr(ReadOnlySpan<byte> rdata, int queriedPort, uint ttlSecs)
    {
        if (rdata.Length < 2) return null;

        ushort svcPriority = (ushort)((rdata[0] << 8) | rdata[1]);
        if (svcPriority == 0) return null; // AliasMode — skip

        // Skip TargetName (label-encoded, terminated by 0).
        int pos = 2;
        if (!SkipDnsName(rdata, ref pos)) return null;

        // Parse SvcParams.
        bool hasH3Alpn = false;
        int altPort = queriedPort;

        while (pos + 4 <= rdata.Length)
        {
            ushort key = (ushort)((rdata[pos] << 8) | rdata[pos + 1]);
            int valLen = (rdata[pos + 2] << 8) | rdata[pos + 3];
            pos += 4;

            if (pos + valLen > rdata.Length) break;

            if (key == 1) // alpn
                hasH3Alpn = ParseAlpnParam(rdata.Slice(pos, valLen));
            else if (key == 3 && valLen == 2) // port
                altPort = (rdata[pos] << 8) | rdata[pos + 1];

            pos += valLen;
        }

        if (!hasH3Alpn) return null;

        var ttl = TimeSpan.FromSeconds(Math.Min(ttlSecs, 3600)); // clamp to 1 hour
        return new SvcbResult(altPort, ttl);
    }

    /// <summary>Parses the ALPN SvcParam value, returning true when "h3" is present.</summary>
    private static bool ParseAlpnParam(ReadOnlySpan<byte> value)
    {
        int pos = 0;
        while (pos < value.Length)
        {
            if (pos + 1 > value.Length) break;
            int len = value[pos++];
            if (pos + len > value.Length) break;
            if (len == 2 && value[pos] == (byte)'h' && value[pos + 1] == (byte)'3')
                return true;
            pos += len;
        }
        return false;
    }

    /// <summary>
    ///     Advances <paramref name="offset" /> past a DNS label-encoded name, following compression
    ///     pointers (RFC 1035 §4.1.4). Returns false if the name is malformed.
    /// </summary>
    private static bool SkipDnsName(ReadOnlySpan<byte> data, ref int offset)
    {
        int iterations = 0;
        while (offset < data.Length)
        {
            if (++iterations > 128) return false; // infinite-loop guard

            byte len = data[offset];
            if (len == 0) { offset++; return true; }                   // root label
            if ((len & 0xC0) == 0xC0) { offset += 2; return true; }   // compression pointer
            if ((len & 0xC0) != 0) return false;                       // reserved

            offset += 1 + len;
        }
        return false;
    }
}
#pragma warning restore CA1416
