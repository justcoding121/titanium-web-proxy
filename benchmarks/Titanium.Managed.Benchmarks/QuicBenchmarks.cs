using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Titanium.Managed.Benchmarks;

[Config(typeof(TlsBenchmarkConfig))]
public class QuicHandshakeBenchmarks
{
    private X509Certificate2 _certificate = null!;

    public static IEnumerable<StackKind> Stacks() => QuicBenchHarness.Stacks();

    [ParamsSource(nameof(Stacks))]
    public StackKind Stack { get; set; }

    [GlobalSetup]
    public void Setup() => _certificate = TlsBenchHarness.CreateEcdsa();

    [GlobalCleanup]
    public void Cleanup() => _certificate.Dispose();

    [Benchmark(OperationsPerInvoke = 5)]
    public async Task Handshake()
    {
        for (int i = 0; i < 5; i++)
        {
            await QuicBenchHarness.Handshake(Stack, _certificate).ConfigureAwait(false);
        }
    }
}

[Config(typeof(TlsBenchmarkConfig))]
public class QuicSmallFlowBenchmarks
{
    private X509Certificate2 _certificate = null!;

    private QuicBenchHarness.Opened _opened = null!;

    private byte[] _payload = null!;

    private CancellationTokenSource _stop = null!;

    private Task? _echo;

    public static IEnumerable<StackKind> Stacks() => QuicBenchHarness.Stacks();

    [ParamsSource(nameof(Stacks))]
    public StackKind Stack { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _certificate = TlsBenchHarness.CreateEcdsa();
        _payload = new byte[QuicBenchHarness.SmallBytes];
        _opened = await QuicBenchHarness.Open(Stack, _certificate).ConfigureAwait(false);
        _stop = new CancellationTokenSource();
        _echo = QuicBenchHarness.Echo(_opened, _stop.Token);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _stop.Cancel();
        await _opened.DisposeAsync().ConfigureAwait(false);
        if (_echo is not null)
        {
            await _echo.ConfigureAwait(false);
        }

        _stop.Dispose();
        _certificate.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public async Task Small100()
    {
        for (int i = 0; i < 50; i++)
        {
            await QuicBenchHarness.Exchange(_opened, _payload).ConfigureAwait(false);
        }
    }
}

[Config(typeof(TlsBenchmarkConfig))]
public class QuicBulkBenchmarks
{
    private X509Certificate2 _certificate = null!;

    private QuicBenchHarness.Opened _opened = null!;

    private byte[] _chunk = null!;

    public static IEnumerable<StackKind> Stacks() => QuicBenchHarness.Stacks();

    [ParamsSource(nameof(Stacks))]
    public StackKind Stack { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _certificate = TlsBenchHarness.CreateEcdsa();
        _chunk = new byte[QuicBenchHarness.ChunkBytes];
        _opened = await QuicBenchHarness.Open(Stack, _certificate).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _opened.DisposeAsync().ConfigureAwait(false);
        _certificate.Dispose();
    }

    [Benchmark]
    public Task Bulk1MiB() => QuicBenchHarness.Transfer(_opened, _chunk);
}
