using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.TestKit;

/// <summary>
/// Minimal in-process RESP server for StackExchange.Redis handshake + SCRIPT EVAL/EVALSHA.
/// Enough for <c>RedisFleetRateStore</c> Sonar coverage without a live Redis (which does not feed the Windows merge).
/// </summary>
public sealed class FakeRespServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly object _gate = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public int EvalCount => Volatile.Read(ref _evalCount);
    private int _evalCount;

    /// <summary>Integer returned from EVAL/EVALSHA (token grant). Default 1.</summary>
    public long EvalGrant { get; set; } = 1;

    /// <summary>When true, close the socket after the next command is read.</summary>
    public bool CloseAfterNextCommand { get; set; }

    /// <summary>When true, answer EVAL with -NOSCRIPT once, then succeed (forces reload path if client retries).</summary>
    public bool NoscriptOnce { get; set; }

    /// <summary>Delay before answering EVAL (for timeout tests).</summary>
    public TimeSpan EvalDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Recorded command names (upper-case) in arrival order.</summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands.ToArray();
            }
        }
    }

    private readonly List<string> _commands = [];
    public FakeRespServer(IPAddress? address = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public string Endpoint => $"127.0.0.1:{Port}";

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var task = ServeClientAsync(client, ct);
            lock (_gate)
            {
                _clients.Add(task);
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var pending = new List<byte>();
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        pending.Add(buffer[i]);
                    }

                    while (TryParseCommand(pending, out var argv))
                    {
                        if (argv.Count == 0)
                        {
                            continue;
                        }

                        var name = argv[0].ToUpperInvariant();
                        lock (_gate)
                        {
                            _commands.Add(name);
                        }

                        if (CloseAfterNextCommand)
                        {
                            CloseAfterNextCommand = false;
                            return;
                        }

                        var reply = await BuildReplyAsync(name, argv, ct).ConfigureAwait(false);
                        await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (IOException)
            {
                // peer closed
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task<byte[]> BuildReplyAsync(string name, IReadOnlyList<string> argv, CancellationToken ct)
    {
        switch (name)
        {
            case "PING":
                return argv.Count > 1 ? Bulk(argv[1]) : Simple("PONG");
            case "ECHO":
                return Bulk(argv.Count > 1 ? argv[1] : string.Empty);
            case "HELLO":
                // RESP2 map-ish array SE.Redis accepts for HELLO
                return Encoding.UTF8.GetBytes("*2\r\n$6\r\nserver\r\n$5\r\nredis\r\n");
            case "CLIENT":
            case "SELECT":
            case "AUTH":
            case "READONLY":
            case "COMMAND":
                return Simple("OK");
            case "CONFIG":
                // CONFIG GET * → empty array; CONFIG SET → OK
                if (argv.Count > 1 && argv[1].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.UTF8.GetBytes("*0\r\n");
                }

                return Simple("OK");
            case "INFO":
                // SE.Redis probes redis_version / redis_mode / role during handshake.
                return Bulk(
                    "redis_version:7.2.0\r\n" +
                    "redis_mode:standalone\r\n" +
                    "role:master\r\n" +
                    "connected_clients:1\r\n" +
                    "run_id:0123456789abcdef0123456789abcdef01234567\r\n");
            case "CLUSTER":
                return Error("ERR This instance has cluster support disabled");
            case "SENTINEL":
                return Error("ERR unknown command 'SENTINEL'");
            case "SUBSCRIBE":
            case "PSUBSCRIBE":
            case "UNSUBSCRIBE":
            case "PUNSUBSCRIBE":
            {
                // Pub/sub confirmation: [kind, channel, count]
                var channel = argv.Count > 1 ? argv[1] : string.Empty;
                return Encoding.UTF8.GetBytes(
                    $"*3\r\n${name.Length}\r\n{name}\r\n${channel.Length}\r\n{channel}\r\n:1\r\n");
            }
            case "GET":
            case "HGET":
                return Encoding.UTF8.GetBytes("$-1\r\n");
            case "SCRIPT":
                if (argv.Count > 1 && argv[1].Equals("LOAD", StringComparison.OrdinalIgnoreCase))
                {
                    return Bulk("deadbeefcafebabe");
                }

                return Simple("OK");
            case "EVAL":
            case "EVALSHA":
                Interlocked.Increment(ref _evalCount);
                if (EvalDelay > TimeSpan.Zero)
                {
                    await Task.Delay(EvalDelay, ct).ConfigureAwait(false);
                }

                if (NoscriptOnce)
                {
                    NoscriptOnce = false;
                    return Encoding.UTF8.GetBytes("-NOSCRIPT No matching script. Please use EVAL.\r\n");
                }

                return Integer(EvalGrant);
            case "TIME":
                var now = DateTimeOffset.UtcNow;
                return Encoding.UTF8.GetBytes($"*2\r\n${now.ToUnixTimeSeconds().ToString().Length}\r\n{now.ToUnixTimeSeconds()}\r\n$1\r\n0\r\n");
            default:
                return Simple("OK");
        }
    }

    private static byte[] Error(string message) => Encoding.UTF8.GetBytes($"-{message}\r\n");

    private static byte[] Simple(string s) => Encoding.UTF8.GetBytes($"+{s}\r\n");
    private static byte[] Integer(long n) => Encoding.UTF8.GetBytes($":{n}\r\n");
    private static byte[] Bulk(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return Encoding.UTF8.GetBytes($"${bytes.Length}\r\n{s}\r\n");
    }

    /// <summary>Parse one RESP array command from the front of <paramref name="pending"/>.</summary>
    private static bool TryParseCommand(List<byte> pending, out List<string> argv)
    {
        argv = [];
        if (pending.Count < 4 || pending[0] != (byte)'*')
        {
            // Inline/simple commands are not expected from SE.Redis; discard noise until '*'.
            var star = pending.IndexOf((byte)'*');
            if (star < 0)
            {
                pending.Clear();
                return false;
            }

            pending.RemoveRange(0, star);
            if (pending.Count < 4)
            {
                return false;
            }
        }

        var offset = 0;
        if (!TryReadLine(pending, ref offset, out var starLine) || starLine.Length < 2 || starLine[0] != '*')
        {
            return false;
        }

        if (!int.TryParse(starLine.AsSpan(1), out var arity) || arity < 0)
        {
            pending.Clear();
            return false;
        }

        var args = new List<string>(arity);
        for (var i = 0; i < arity; i++)
        {
            if (!TryReadLine(pending, ref offset, out var bulkHeader) || bulkHeader.Length < 2 || bulkHeader[0] != '$')
            {
                return false;
            }

            if (!int.TryParse(bulkHeader.AsSpan(1), out var len) || len < 0)
            {
                pending.Clear();
                return false;
            }

            if (pending.Count < offset + len + 2)
            {
                return false;
            }

            var slice = pending.GetRange(offset, len);
            offset += len;
            if (pending[offset] != (byte)'\r' || pending[offset + 1] != (byte)'\n')
            {
                pending.Clear();
                return false;
            }

            offset += 2;
            args.Add(Encoding.UTF8.GetString(slice.ToArray()));
        }

        pending.RemoveRange(0, offset);
        argv = args;
        return true;
    }

    private static bool TryReadLine(List<byte> pending, ref int offset, out string line)
    {
        line = string.Empty;
        for (var i = offset; i + 1 < pending.Count; i++)
        {
            if (pending[i] == (byte)'\r' && pending[i + 1] == (byte)'\n')
            {
                var len = i - offset;
                var buf = new byte[len];
                for (var j = 0; j < len; j++)
                {
                    buf[j] = pending[offset + j];
                }

                line = Encoding.ASCII.GetString(buf);
                offset = i + 2;
                return true;
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop.ConfigureAwait(false);
            }
        }
        catch
        {
            // ignore
        }

        Task[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
        }

        try
        {
            await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
    }
}
