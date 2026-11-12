using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.TestKit;

/// <summary>
/// Minimal loopback DNS server for unit tests. Handles SRV queries over UDP and TCP (same port).
/// Bind via <see cref="Start"/>; the server returns an ephemeral loopback port. The TCP listener
/// shares that port so DnsClient's <c>UseTcpFallback</c> TC-bit retry works end-to-end.
/// </summary>
public sealed class FakeDnsServer : IAsyncDisposable
{
    /// <summary>Controls how the server responds to queries.</summary>
    public enum FaultMode
    {
        /// <summary>Serve the configured SRV records normally.</summary>
        None,
        /// <summary>Return NXDOMAIN (RCODE 3) for every query.</summary>
        NxDomain,
        /// <summary>Return SERVFAIL (RCODE 2) for every query.</summary>
        ServFail,
        /// <summary>Set TC=1 in UDP responses with no answers; serve full records over TCP.</summary>
        TruncateUdpUseTcp,
        /// <summary>Return a structurally invalid response (valid ID, corrupt remaining bytes).</summary>
        Malformed,
    }

    /// <summary>One SRV record the server will include in answers.</summary>
    public sealed record SrvEntry(string Target, int Port, int Priority, int Weight, int Ttl = 30);

    private readonly Socket _udp;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _cts = new();
    private volatile State _state = new([], FaultMode.None);

    private sealed record State(IReadOnlyList<SrvEntry> Records, FaultMode Fault);

    private FakeDnsServer(Socket udp, TcpListener tcp, int port)
    {
        _udp = udp;
        _tcp = tcp;
        Port = port;
        _ = Task.Run(UdpLoopAsync);
        _ = Task.Run(TcpLoopAsync);
    }

    /// <summary>
    /// Binds a UDP socket to loopback:0 for an ephemeral port, then starts a TCP listener on
    /// the same port. Retries up to 20 times if the TCP port is taken between the two binds.
    /// </summary>
    public static FakeDnsServer Start()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
            var tcp = new TcpListener(IPAddress.Loopback, port);
            try
            {
                tcp.Start();
                return new FakeDnsServer(udp, tcp, port);
            }
            catch (SocketException)
            {
                udp.Dispose();
            }
        }

        throw new InvalidOperationException("FakeDnsServer: could not bind UDP and TCP to the same loopback port after 20 attempts.");
    }

    /// <summary>Port the server is listening on (both UDP and TCP).</summary>
    public int Port { get; }

    /// <summary><c>"127.0.0.1:{Port}"</c> — suitable for <c>discovery.dnsServer</c>.</summary>
    public string Endpoint => $"127.0.0.1:{Port}";

    /// <summary>Replaces the SRV record list and optional fault mode atomically.</summary>
    public void SetRecords(IEnumerable<SrvEntry> records, FaultMode fault = FaultMode.None) =>
        _state = new State([.. records], fault);

    /// <summary>Injects a fault without changing the record list.</summary>
    public void SetFault(FaultMode fault) => _state = new State(_state.Records, fault);

    /// <summary>Clears all records (NOERROR with zero answers) without changing the fault mode.</summary>
    public void ClearRecords() => _state = new State([], _state.Fault);

    // ── UDP worker ─────────────────────────────────────────────────────────────

    private async Task UdpLoopAsync()
    {
        var buf = new byte[4096];
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _udp.ReceiveFromAsync(
                    buf.AsMemory(), SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), _cts.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            var query = buf[..result.ReceivedBytes];
            var response = BuildResponse(query, isTcp: false);
            if (response is null)
                continue;

            try
            {
                await _udp.SendToAsync(response.AsMemory(), SocketFlags.None,
                    result.RemoteEndPoint, _cts.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Client went away.
            }
        }
    }

    // ── TCP worker ─────────────────────────────────────────────────────────────

    private async Task TcpLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            _ = HandleTcpClientAsync(client);
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var lenBuf = new byte[2];
                if (!await ReadExactAsync(stream, lenBuf, _cts.Token))
                    return;

                var msgLen = (lenBuf[0] << 8) | lenBuf[1];
                if (msgLen is <= 0 or > 65535)
                    return;

                var query = new byte[msgLen];
                if (!await ReadExactAsync(stream, query, _cts.Token))
                    return;

                // isTcp=true so TruncateUdpUseTcp serves the full answer over TCP.
                var response = BuildResponse(query, isTcp: true);
                if (response is null)
                    return;

                var prefix = new byte[] { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) };
                await stream.WriteAsync(prefix.AsMemory(), _cts.Token);
                await stream.WriteAsync(response.AsMemory(), _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch
            {
                // Client disconnected or server shutting down.
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0)
                return false;
            total += n;
        }

        return true;
    }

    // ── DNS response building ──────────────────────────────────────────────────

    private byte[]? BuildResponse(byte[] query, bool isTcp)
    {
        if (query.Length < 12)
            return null;

        var id0 = query[0];
        var id1 = query[1];
        var state = _state;

        // Malformed: keep the correct ID but corrupt everything after it.
        if (state.Fault == FaultMode.Malformed)
        {
            return [id0, id1, 0xFF, 0xFF, 0x00, 0x01, 0xAA, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00];
        }

        // Locate end of question section (starts at byte 12).
        var questionEnd = FindQuestionEnd(query, offset: 12);
        if (questionEnd < 0)
            return null;

        // Question bytes to echo verbatim (QNAME + QTYPE + QCLASS).
        var question = query[12..questionEnd];

        using var ms = new MemoryStream(256);

        // Header ID
        ms.WriteByte(id0);
        ms.WriteByte(id1);

        if (state.Fault is FaultMode.NxDomain or FaultMode.ServFail)
        {
            var rcode = (byte)(state.Fault == FaultMode.ServFail ? 2 : 3);
            // Flags: QR=1, AA=0, TC=0, RD=1, RA=1, RCODE
            ms.Write(new byte[] { 0x81, rcode });
            WriteUint16(ms, 1); // QDCOUNT
            WriteUint16(ms, 0); // ANCOUNT
            WriteUint16(ms, 0); // NSCOUNT
            WriteUint16(ms, 0); // ARCOUNT
            ms.Write(question);
            return ms.ToArray();
        }

        var truncate = !isTcp && state.Fault == FaultMode.TruncateUdpUseTcp;
        var records = state.Records;
        var ancount = truncate ? 0 : records.Count;

        // Flags high byte: QR=1, OPCODE=0, AA=0, TC=(truncate?1:0), RD=1
        //   0x83 = 1000_0011  (TC=1, RD=1)
        //   0x81 = 1000_0001  (TC=0, RD=1)
        var flagsHigh = truncate ? (byte)0x83 : (byte)0x81;
        ms.Write(new byte[] { flagsHigh, 0x80 }); // RA=1, RCODE=0
        WriteUint16(ms, 1);                        // QDCOUNT
        WriteUint16(ms, (ushort)ancount);           // ANCOUNT
        WriteUint16(ms, 0);                        // NSCOUNT
        WriteUint16(ms, 0);                        // ARCOUNT
        ms.Write(question);

        if (!truncate)
        {
            foreach (var rec in records)
                WriteSrvRr(ms, rec);
        }

        return ms.ToArray();
    }

    private static void WriteSrvRr(MemoryStream ms, SrvEntry rec)
    {
        // NAME: compression pointer to offset 12 (start of QNAME in message).
        ms.WriteByte(0xC0);
        ms.WriteByte(0x0C);

        // TYPE = SRV (33), CLASS = IN (1)
        ms.Write(new byte[] { 0x00, 0x21, 0x00, 0x01 });

        // TTL (4 bytes big-endian)
        ms.Write(new byte[]
        {
            (byte)(rec.Ttl >> 24), (byte)(rec.Ttl >> 16), (byte)(rec.Ttl >> 8), (byte)(rec.Ttl & 0xFF)
        });

        var targetEncoded = EncodeDnsName(rec.Target);
        var rdlen = (ushort)(6 + targetEncoded.Length); // Priority + Weight + Port + Target

        WriteUint16(ms, rdlen);
        WriteUint16(ms, (ushort)rec.Priority);
        WriteUint16(ms, (ushort)rec.Weight);
        WriteUint16(ms, (ushort)rec.Port);
        ms.Write(targetEncoded);
    }

    private static byte[] EncodeDnsName(string name)
    {
        using var ms = new MemoryStream();
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }

        ms.WriteByte(0); // root label
        return ms.ToArray();
    }

    /// <summary>Returns the byte offset just past the question section (QNAME + QTYPE + QCLASS), or -1.</summary>
    private static int FindQuestionEnd(byte[] data, int offset)
    {
        var pos = offset;
        while (pos < data.Length)
        {
            var len = data[pos];
            if (len == 0)
            {
                pos++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer in the query QNAME (unusual but valid).
                pos += 2;
                break;
            }

            pos += 1 + len;
        }

        // QTYPE (2) + QCLASS (2)
        var end = pos + 4;
        return end <= data.Length ? end : -1;
    }

    private static void WriteUint16(MemoryStream ms, ushort value) =>
        ms.Write(new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _udp.Dispose(); }
        catch { /* ignore */ }

        try { _tcp.Stop(); }
        catch { /* ignore */ }

        _cts.Dispose();
    }
}
