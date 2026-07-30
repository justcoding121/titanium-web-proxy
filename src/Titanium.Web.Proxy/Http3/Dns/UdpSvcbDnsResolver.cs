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
///     Production <see cref="IHttpsSvcbResolver" /> that sends a UDP DNS query for HTTPS RR (type 65)
///     per host:port and parses the ALPN SvcParam (key 1) looking for "h3".
/// </summary>
/// <remarks>
///     Design points:
///     <list type="bullet">
///       <item>Per-query UDP <see cref="Socket" /> — avoids shared-socket threading issues.</item>
///       <item>Random query-ID validated in the response.</item>
///       <item>RCODE check — NXDOMAIN/SERVFAIL is a definitive negative and is negative-cached.</item>
///       <item>TC (Truncated) flag — truncated UDP responses are treated as transient and not cached.</item>
///       <item>Query coalescing — concurrent requests for the same host:port share one UDP round-trip.
///         The shared task uses its own internal timeout CTS so one caller's cancellation cannot
///         abort or poison the lookup for other callers.</item>
///       <item>Per-waiter cancellation — callers cancel their own wait via <see cref="Task.WaitAsync" />
///         without affecting the shared task or other waiters.</item>
///       <item>Negative caching — only for definitive results (NXDOMAIN, valid no-H3 response).
///         Transient failures (timeout, socket error, TC bit) are not negative-cached.</item>
///       <item>SvcPriority selection — returns the ServiceMode record with the lowest SvcPriority.</item>
///       <item>AliasMode chain — bounded recursive resolution (max depth <see cref="MaxAliasDepth" />)
///         with loop protection via the same-host guard.</item>
///       <item>TargetName — preserved from ServiceMode records so the caller can connect to the SVCB
///         target host for QUIC while retaining the origin host for TLS SNI and <c>:authority</c>.</item>
///       <item>Address family — derived from the DNS server <see cref="IPEndPoint" /> so both IPv4 and
///         IPv6 DNS servers work correctly.</item>
///       <item>Port validation — port SvcParam value 0 is rejected.</item>
///     </list>
///     <para>
///         ECH (Encrypted Client Hello) and address-hint SvcParams are intentionally not consumed.
///         Normal host resolution remains the fallback for IP routing; ECH support can be added when
///         the runtime exposes the relevant TLS primitives.
///     </para>
/// </remarks>
internal sealed class UdpSvcbDnsResolver : IHttpsSvcbResolver
{
    private const int DnsQueryTimeoutMs = 500;
    private const ushort DnsTypeHttps = 65;
    private const ushort DnsClassIn = 1;

    /// <summary>Maximum depth for recursive AliasMode chain following.</summary>
    private const int MaxAliasDepth = 3;

    private readonly IPEndPoint _dnsServerEndPoint;

    // Coalescing: concurrent requests for the same host:port share one Task<SvcbQueryState>.
    // The task uses its own timeout CTS — independent of any individual caller — so one waiter's
    // cancellation cannot cancel or poison the shared lookup.
    private readonly ConcurrentDictionary<string, Task<SvcbQueryState>> _inflight = new();

    // Negative cache: stores the expiry for definitive "no H3" results only.
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

        // Fast path: definitive negative already cached.
        if (_negativeCache.TryGetValue(key, out var expires) && DateTime.UtcNow < expires)
            return Task.FromResult<SvcbResult?>(null);

        // Coalesce: reuse in-flight shared task or start a new one.
        var sharedTask = _inflight.GetOrAdd(key, _ => RunSharedQueryAsync(key, host, port));

        // Per-waiter cancellation: WaitAsync throws OperationCanceledException to THIS caller but
        // leaves the shared task running so other waiters are unaffected.
        return WrapResultAsync(sharedTask, ct);
    }

    private static async Task<SvcbResult?> WrapResultAsync(Task<SvcbQueryState> sharedTask, CancellationToken ct)
    {
        var state = ct.CanBeCanceled ? await sharedTask.WaitAsync(ct) : await sharedTask;
        return state.Result;
    }

    private async Task<SvcbQueryState> RunSharedQueryAsync(string key, string host, int port)
    {
        try
        {
            // The shared query has its own dedicated timeout — not linked to any caller's CT.
            using var timeoutCts = new CancellationTokenSource(DnsQueryTimeoutMs);
            var (result, isTransient) = await QueryCoreAsync(host, port, timeoutCts.Token);

            // Only negative-cache definitive results; transient failures allow a fresh retry.
            if (!isTransient && result == null)
                _negativeCache[key] = DateTime.UtcNow + _negativeCacheTtl;

            return new SvcbQueryState(result);
        }
        catch (OperationCanceledException)
        {
            // Internal timeout: transient, not negative-cached.
            return new SvcbQueryState(null);
        }
        catch
        {
            // Socket / IO / unexpected error: treat as transient.
            return new SvcbQueryState(null);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>
    ///     Sends a DNS UDP query and parses the response.
    ///     Returns <c>(result, false)</c> for a successful H3 record,
    ///     <c>(null, false)</c> for a definitive negative,
    ///     <c>(null, true)</c> for a transient failure (socket error, TC bit, parse error).
    /// </summary>
    private async Task<(SvcbResult? result, bool isTransient)> QueryCoreAsync(
        string host, int port, CancellationToken ct, int aliasDepth = 0)
    {
        var queryId = new byte[2];
        RandomNumberGenerator.Fill(queryId.AsSpan());
        var queryPacket = BuildDnsQuery(queryId, host, DnsTypeHttps);

        // Derive the socket address family from the configured DNS server endpoint.
        var addrFamily = _dnsServerEndPoint.AddressFamily;
        using var socket = new Socket(addrFamily, SocketType.Dgram, ProtocolType.Udp);
        var bindAddr = addrFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        socket.Bind(new IPEndPoint(bindAddr, 0));

        // Connect() on a UDP socket does not perform a handshake — it just filters the socket at the
        // kernel level so only datagrams from _dnsServerEndPoint are ever delivered to ReceiveAsync.
        // Without this, any host on the network path could race a spoofed UDP response to this
        // ephemeral port (classic off-path DNS cache-poisoning) and Send/ReceiveTo would happily accept
        // it, since plain SendToAsync/ReceiveAsync do not validate the peer address at all.
        socket.Connect(_dnsServerEndPoint);

        await socket.SendAsync(queryPacket, SocketFlags.None, ct);

        var responseBuffer = new byte[4096];
        var received = await socket.ReceiveAsync(responseBuffer.AsMemory(), SocketFlags.None, ct);

        var parsed = ParseDnsResponseCore(responseBuffer.AsSpan(0, received), queryId, host, port);

        if (parsed.IsTransient) return (null, isTransient: true);
        if (parsed.BestRecord != null) return (parsed.BestRecord, isTransient: false);

        // AliasMode chain following: bounded depth, loop guard via same-host check.
        if (parsed.AliasTarget != null
            && aliasDepth < MaxAliasDepth
            && !string.Equals(parsed.AliasTarget, host, StringComparison.OrdinalIgnoreCase))
        {
            return await QueryCoreAsync(parsed.AliasTarget, port, ct, aliasDepth + 1);
        }

        return (null, isTransient: false); // definitive no-H3
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DNS wire-format builder
    // ─────────────────────────────────────────────────────────────────────────

    private static byte[] BuildDnsQuery(ReadOnlySpan<byte> queryId, string host, ushort rrType)
    {
        var buf = new System.IO.MemoryStream(64);

        buf.Write(queryId);
        buf.Write(stackalloc byte[] { 0x01, 0x00 }); // Flags: standard query, RD=1
        buf.Write(stackalloc byte[] { 0x00, 0x01 }); // QDCOUNT = 1
        buf.Write(stackalloc byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // AN/NS/AR = 0

        WriteDnsName(buf, host);

        buf.WriteByte((byte)(rrType >> 8));
        buf.WriteByte((byte)(rrType & 0xFF));
        buf.WriteByte((byte)(DnsClassIn >> 8));
        buf.WriteByte((byte)(DnsClassIn & 0xFF));

        return buf.ToArray();
    }

    private static void WriteDnsName(System.IO.Stream buf, string name)
    {
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
    // DNS response parser (core + public test hook)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Public test hook — returns the best <see cref="SvcbResult" /> (or null) from a raw
    ///     DNS response buffer without any coalescing or caching.
    /// </summary>
    internal static SvcbResult? ParseDnsResponseInternal(
        ReadOnlySpan<byte> response, ReadOnlySpan<byte> expectedId, string host, int queriedPort)
        => ParseDnsResponseCore(response, expectedId, host, queriedPort).BestRecord;

    private static DnsParseResult ParseDnsResponseCore(
        ReadOnlySpan<byte> response, ReadOnlySpan<byte> expectedId, string host, int queriedPort)
    {
        if (response.Length < 12) return DnsParseResult.Transient;

        // Transaction ID must match.
        if (response[0] != expectedId[0] || response[1] != expectedId[1])
            return DnsParseResult.Transient;

        // TC (Truncated) flag — bit 1 of byte 2. Truncated UDP responses are transient.
        if ((response[2] & 0x02) != 0) return DnsParseResult.Transient;

        // RCODE (lower 4 bits of byte 3): non-zero means NXDOMAIN, SERVFAIL, etc.
        var rcode = response[3] & 0x0F;
        if (rcode != 0) return DnsParseResult.DefinitiveNegative;

        int anCount = (response[6] << 8) | response[7];
        if (anCount == 0) return DnsParseResult.DefinitiveNegative;

        // Skip question section.
        int offset = 12;
        if (!SkipDnsName(response, ref offset)) return DnsParseResult.Transient;
        offset += 4; // QTYPE + QCLASS

        // Collect all HTTPS RR answers:
        //   - ServiceMode (SvcPriority > 0): choose the record with the lowest priority.
        //   - AliasMode   (SvcPriority = 0): capture the first target for chain following.
        SvcbResult? bestRecord = null;
        ushort bestPriority = ushort.MaxValue;
        string? firstAlias = null;

        for (int i = 0; i < anCount && offset < response.Length; i++)
        {
            if (!SkipDnsName(response, ref offset)) return DnsParseResult.Transient;
            if (offset + 10 > response.Length) return DnsParseResult.Transient;

            ushort rrType = (ushort)((response[offset] << 8) | response[offset + 1]);
            uint ttlSecs = (uint)((response[offset + 4] << 24) | (response[offset + 5] << 16)
                                 | (response[offset + 6] << 8) | response[offset + 7]);
            int rdLen = (response[offset + 8] << 8) | response[offset + 9];
            offset += 10;

            if (offset + rdLen > response.Length) return DnsParseResult.Transient;

            if (rrType == DnsTypeHttps && rdLen >= 2)
            {
                var rdata = response.Slice(offset, rdLen);
                ushort svcPriority = (ushort)((rdata[0] << 8) | rdata[1]);

                if (svcPriority == 0)
                {
                    // AliasMode: extract the alias target for bounded chain following.
                    if (firstAlias == null)
                    {
                        int p = 2;
                        firstAlias = ReadDnsName(rdata, ref p);
                    }
                }
                else if (svcPriority < bestPriority)
                {
                    // ServiceMode: candidate with lower priority wins.
                    var record = ParseServiceModeRr(rdata, queriedPort, ttlSecs);
                    if (record != null)
                    {
                        bestRecord = record;
                        bestPriority = svcPriority;
                    }
                }
            }

            offset += rdLen;
        }

        if (bestRecord != null) return new DnsParseResult(bestRecord, null, false);
        if (firstAlias != null) return new DnsParseResult(null, firstAlias, false);
        return DnsParseResult.DefinitiveNegative;
    }

    /// <summary>
    ///     Parses a ServiceMode HTTPS RR RDATA section (SvcPriority &gt; 0) and returns a
    ///     <see cref="SvcbResult" /> when the <c>alpn</c> SvcParam (key 1) contains "h3".
    ///     Returns <see langword="null" /> when ALPN is absent or does not include "h3".
    /// </summary>
    private static SvcbResult? ParseServiceModeRr(ReadOnlySpan<byte> rdata, int queriedPort, uint ttlSecs)
    {
        int pos = 2; // skip SvcPriority (already extracted by caller)

        var targetName = ReadDnsName(rdata, ref pos);
        if (pos < 0) return null; // malformed

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
            {
                int p = (rdata[pos] << 8) | rdata[pos + 1];
                if (p > 0) altPort = p; // skip invalid port 0
            }

            pos += valLen;
        }

        if (!hasH3Alpn) return null;

        var ttl = TimeSpan.FromSeconds(Math.Min(ttlSecs, 3600)); // clamp to 1 hour

        // TargetName "." (empty string after parsing) means owner name — normalize to null.
        var effectiveTarget = string.IsNullOrEmpty(targetName) ? null : targetName;

        return new SvcbResult(altPort, ttl, effectiveTarget);
    }

    /// <summary>Parses the ALPN SvcParam wire format, returning true when "h3" is present.</summary>
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
    ///     Reads a DNS wire-format name from <paramref name="rdata" /> starting at
    ///     <paramref name="offset" />, advancing <paramref name="offset" /> past it.
    ///     Compression pointers are skipped without following (no full message context available).
    ///     Returns an empty string for <c>.</c> (root label = owner name).
    ///     Returns <see langword="null" /> on malformed or truncated input.
    /// </summary>
    private static string? ReadDnsName(ReadOnlySpan<byte> rdata, ref int offset)
    {
        var sb = new StringBuilder();
        int iterations = 0;

        while (offset < rdata.Length)
        {
            if (++iterations > 128) return null;

            byte len = rdata[offset];

            if (len == 0)
            {
                offset++;
                return sb.ToString().TrimEnd('.');
            }

            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer: skip without following; return whatever we have.
                offset += 2;
                return sb.Length > 0 ? sb.ToString().TrimEnd('.') : null;
            }

            if ((len & 0xC0) != 0) return null; // reserved bits

            offset++;
            if (offset + len > rdata.Length) return null;

            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(rdata.Slice(offset, len)));
            offset += len;
        }

        return null;
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
            if (++iterations > 128) return false;

            byte len = data[offset];
            if (len == 0) { offset++; return true; }
            if ((len & 0xC0) == 0xC0) { offset += 2; return true; }
            if ((len & 0xC0) != 0) return false;

            offset += 1 + len;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal types
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Carries the result of a completed shared DNS query task.</summary>
    private sealed class SvcbQueryState
    {
        internal SvcbQueryState(SvcbResult? result) => Result = result;
        internal SvcbResult? Result { get; }
    }

    /// <summary>Structured result of <see cref="ParseDnsResponseCore" />.</summary>
    private readonly struct DnsParseResult
    {
        internal static readonly DnsParseResult Transient = new(null, null, isTransient: true);
        internal static readonly DnsParseResult DefinitiveNegative = new(null, null, isTransient: false);

        internal DnsParseResult(SvcbResult? best, string? alias, bool isTransient)
        {
            BestRecord = best;
            AliasTarget = alias;
            IsTransient = isTransient;
        }

        internal SvcbResult? BestRecord { get; }
        internal string? AliasTarget { get; }
        internal bool IsTransient { get; }
    }
}
#pragma warning restore CA1416
