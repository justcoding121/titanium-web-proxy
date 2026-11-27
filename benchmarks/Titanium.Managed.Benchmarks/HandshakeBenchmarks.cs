using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Titanium.Tls;

namespace Titanium.Managed.Benchmarks;

[Config(typeof(TlsBenchmarkConfig))]
public class HandshakeBenchmarks
{
    private X509Certificate2 _certificate = null!;

    [GlobalSetup]
    public void Setup() => _certificate = TlsBenchHarness.CreateEcdsa();

    [GlobalCleanup]
    public void Cleanup() => _certificate.Dispose();

    [Arguments(StackKind.Platform, TransportKind.Loopback)]
    [Arguments(StackKind.Managed, TransportKind.Loopback)]
    [Arguments(StackKind.Platform, TransportKind.Memory)]
    [Arguments(StackKind.Managed, TransportKind.Memory)]
    [Benchmark(OperationsPerInvoke = 20)]
    public void FullHandshake(StackKind stack, TransportKind transport)
    {
        for (int i = 0; i < 20; i++)
        {
            using OpenedConnection connection = TlsBenchHarness.Open(stack, transport, _certificate, gear: null);
        }
    }
}

[Config(typeof(TlsBenchmarkConfig))]
public class ResumedHandshakeBenchmarks
{
    private X509Certificate2 _certificate = null!;
    private SessionGear? _gear;

    [Params(StackKind.Platform, StackKind.Managed)]
    public StackKind Stack { get; set; }

    [Params(TransportKind.Loopback, TransportKind.Memory)]
    public TransportKind Transport { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _certificate = TlsBenchHarness.CreateEcdsa();
        if (Stack == StackKind.Managed)
        {
            _gear = new SessionGear();
        }

        using (OpenedConnection primed = TlsBenchHarness.Open(Stack, Transport, _certificate, _gear))
        {
            TlsBenchHarness.Exchange(primed.Client, primed.Server, new byte[1]);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _gear?.Dispose();
        _certificate.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 20)]
    public void ResumedHandshake()
    {
        for (int i = 0; i < 20; i++)
        {
            using OpenedConnection connection = TlsBenchHarness.Open(Stack, Transport, _certificate, _gear);
            if (Stack == StackKind.Managed && !connection.Resumed)
            {
                throw new InvalidOperationException("The managed handshake did not resume.");
            }
        }
    }
}

[Config(typeof(TlsBenchmarkConfig))]
public class BulkBenchmarks
{
    private X509Certificate2 _certificate = null!;
    private OpenedConnection _connection = null!;
    private byte[] _chunk = null!;
    private byte[] _readBuffer = null!;

    [Params(StackKind.Platform, StackKind.Managed)]
    public StackKind Stack { get; set; }

    [Params(TransportKind.Loopback, TransportKind.Memory)]
    public TransportKind Transport { get; set; }

    [Params(16 * 1024, 64 * 1024)]
    public int Chunk { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _certificate = TlsBenchHarness.CreateEcdsa();
        _connection = TlsBenchHarness.Open(Stack, Transport, _certificate, gear: null);
        _chunk = new byte[Chunk];
        _readBuffer = new byte[64 * 1024];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        _certificate.Dispose();
    }

    [Benchmark]
    public void Bulk256MiB() => TlsBenchHarness.Transfer(_connection.Client, _connection.Server, _chunk, _readBuffer, TlsBenchHarness.BulkBytes);
}

[Config(typeof(TlsBenchmarkConfig))]
public class SmallExchangeBenchmarks
{
    private X509Certificate2 _certificate = null!;
    private OpenedConnection _connection = null!;
    private byte[] _small100 = null!;
    private byte[] _small1024 = null!;
    private Thread? _echo;

    [Params(StackKind.Platform, StackKind.Managed)]
    public StackKind Stack { get; set; }

    [Params(TransportKind.Loopback, TransportKind.Memory)]
    public TransportKind Transport { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _certificate = TlsBenchHarness.CreateEcdsa();
        _connection = TlsBenchHarness.Open(Stack, Transport, _certificate, gear: null);
        _small100 = new byte[100];
        _small1024 = new byte[1024];
        _echo = new Thread(Echo)
        {
            IsBackground = true,
        };
        _echo.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        _echo?.Join();
        _certificate.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public void Small100()
    {
        for (int i = 0; i < 50; i++)
        {
            _connection.Client.Write(_small100);
            ReadFull(_connection.Client, _small100);
        }
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public void Small1024()
    {
        for (int i = 0; i < 50; i++)
        {
            _connection.Client.Write(_small1024);
            ReadFull(_connection.Client, _small1024);
        }
    }

    private void Echo()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int count = _connection.Server.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    return;
                }

                _connection.Server.Write(buffer, 0, count);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static void ReadFull(Stream source, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = source.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                throw new EndOfStreamException("The exchange ended early.");
            }

            read += count;
        }
    }
}
