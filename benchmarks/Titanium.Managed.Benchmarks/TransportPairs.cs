using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Titanium.Managed.Benchmarks;

public enum TransportKind
{
    Loopback,
    Memory,
}

internal sealed class TransportPair : IDisposable
{
    private readonly IDisposable _owner;
    private int _disposed;

    private TransportPair(Stream client, Stream server, IDisposable owner)
    {
        Client = client;
        Server = server;
        _owner = owner;
    }

    public Stream Client { get; }

    public Stream Server { get; }

    public static TransportPair Create(TransportKind kind)
    {
        return kind == TransportKind.Memory ? CreateMemory() : CreateLoopback();
    }

    public void Unblock() => _owner.Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unblock();
        try
        {
            Client.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            Server.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static TransportPair CreateMemory()
    {
        var clientToServer = new BlockingPipe();
        var serverToClient = new BlockingPipe();
        var owner = new CompositeDisposable(clientToServer, serverToClient);
        return new TransportPair(new PipeStream(serverToClient, clientToServer), new PipeStream(clientToServer, serverToClient), owner);
    }

    private static TransportPair CreateLoopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient { NoDelay = true };
        Exception? connectError = null;
        var connect = new Thread(() =>
        {
            try
            {
                client.Connect(IPAddress.Loopback, port);
            }
            catch (Exception ex)
            {
                connectError = ex;
            }
        });
        connect.IsBackground = true;
        connect.Start();
        TcpClient server = listener.AcceptTcpClient();
        connect.Join();
        if (connectError is not null)
        {
            server.Dispose();
            client.Dispose();
            throw connectError;
        }

        server.NoDelay = true;
        var owner = new CompositeDisposable(client, server);
        return new TransportPair(client.GetStream(), server.GetStream(), owner);
    }
}

internal sealed class BlockingPipe : IDisposable
{
    private readonly byte[] _buffer = new byte[32 * 1024];
    private readonly object _gate = new();
    private int _head;
    private int _count;
    private bool _closed;

    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            while (_count == 0 && !_closed)
            {
                if (!Monitor.Wait(_gate, 30_000))
                {
                    throw new TimeoutException("The in-memory transport stalled.");
                }
            }

            int length = Math.Min(destination.Length, _count);
            int first = Math.Min(length, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(destination);
            if (first < length)
            {
                _buffer.AsSpan(0, length - first).CopyTo(destination[first..]);
            }

            _head = (_head + length) % _buffer.Length;
            _count -= length;
            Monitor.PulseAll(_gate);
            return length;
        }
    }

    public void Write(ReadOnlySpan<byte> source)
    {
        int offset = 0;
        while (offset < source.Length)
        {
            lock (_gate)
            {
                while (_count == _buffer.Length && !_closed)
                {
                    if (!Monitor.Wait(_gate, 30_000))
                    {
                        throw new TimeoutException("The in-memory transport stalled.");
                    }
                }

                if (_closed)
                {
                    throw new IOException("The in-memory transport is closed.");
                }

                int length = Math.Min(source.Length - offset, _buffer.Length - _count);
                int tail = (_head + _count) % _buffer.Length;
                int first = Math.Min(length, _buffer.Length - tail);
                source.Slice(offset, first).CopyTo(_buffer.AsSpan(tail, first));
                if (first < length)
                {
                    source.Slice(offset + first, length - first).CopyTo(_buffer.AsSpan(0, length - first));
                }

                _count += length;
                offset += length;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }
}

internal sealed class PipeStream : Stream
{
    private readonly BlockingPipe _read;
    private readonly BlockingPipe _write;

    public PipeStream(BlockingPipe read, BlockingPipe write)
    {
        _read = read;
        _write = write;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => _read.Read(buffer);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => _write.Write(buffer);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class CompositeDisposable : IDisposable
{
    private readonly IDisposable _first;
    private readonly IDisposable _second;

    public CompositeDisposable(IDisposable first, IDisposable second)
    {
        _first = first;
        _second = second;
    }

    public void Dispose()
    {
        _first.Dispose();
        _second.Dispose();
    }
}
