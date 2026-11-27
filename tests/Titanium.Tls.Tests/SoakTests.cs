using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SoakTests
{
    private const int InFlightCap = 200;

    [TestMethod]
    [TestCategory("Slow")]
    [Timeout(2700000)]
    public async Task ThirtyMinutesOfHandshakesStayBounded()
    {
        int seconds = 1800;
        string? configured = Environment.GetEnvironmentVariable("TITANIUM_SOAK_SECONDS");
        if (int.TryParse(configured, out int parsed) && parsed > 0)
        {
            seconds = parsed;
        }

        for (int i = 0; i < 4; i++)
        {
            using HandshakePair warmup = HandshakePair.Run();
            Exchange(warmup);
        }

        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        var errors = new ConcurrentQueue<string>();
        int inFlight = 0;
        int maxInFlight = 0;
        int completed = 0;
        int aborted = 0;
        int reset = 0;
        using var gate = new SemaphoreSlim(InFlightCap);
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        Task slow = SlowLoris(deadline, errors);

        while (DateTime.UtcNow < deadline && errors.IsEmpty)
        {
            await gate.WaitAsync();
            int current = Interlocked.Increment(ref inFlight);
            int seen = Volatile.Read(ref maxInFlight);
            while (current > seen && Interlocked.CompareExchange(ref maxInFlight, current, seen) != seen)
            {
                seen = Volatile.Read(ref maxInFlight);
            }

            int kind = Random.Shared.Next(3);
            _ = Task.Run(() =>
            {
                try
                {
                    if (kind == 0)
                    {
                        using HandshakePair pair = HandshakePair.Run();
                        Exchange(pair);
                        Interlocked.Increment(ref completed);
                    }
                    else if (kind == 1)
                    {
                        AbortAfterHello();
                        Interlocked.Increment(ref aborted);
                    }
                    else
                    {
                        ResetAfterHello();
                        Interlocked.Increment(ref reset);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                    gate.Release();
                }
            });
        }

        DateTime drain = DateTime.UtcNow.AddMinutes(2);
        while (Volatile.Read(ref inFlight) > 0 && DateTime.UtcNow < drain)
        {
            await Task.Delay(50);
        }

        await slow;
        GC.Collect();
        long after = GC.GetTotalMemory(true);
        Assert.AreEqual(0, errors.Count, errors.TryPeek(out string? first) ? first : string.Empty);
        Assert.AreEqual(0, Volatile.Read(ref inFlight));
        Assert.IsTrue(Volatile.Read(ref maxInFlight) <= InFlightCap);
        Assert.IsTrue(completed > 0);
        Assert.IsTrue(aborted > 0);
        Assert.IsTrue(reset > 0);
        Assert.IsTrue(after < baseline + (32L * 1024L * 1024L), after + " against " + baseline);
    }

    private static void Exchange(HandshakePair pair)
    {
        var plain = new byte[64];
        plain[0] = 7;
        var record = new byte[128];
        var output = new TlsOutput();
        int written = pair.Client.ProtectApplication(plain, record);
        pair.Server.Advance(record.AsSpan(0, written), out int consumed, output);
        Assert.AreEqual(written, consumed);
        var read = new byte[64];
        Assert.AreEqual(64, pair.Server.ReadPlaintext(read));
        Assert.AreEqual(7, read[0]);
    }

    private static void AbortAfterHello()
    {
        using var client = new TlsEngine(TlsRole.Client);
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] hello = output.Bytes.ToArray();
        server.Advance(hello.AsSpan(0, 1), out _, output);
    }

    private static void ResetAfterHello()
    {
        using var client = new TlsEngine(TlsRole.Client);
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        Assert.AreEqual(TlsEngineState.WaitClientHelloDecision, server.State);
    }

    private static async Task SlowLoris(DateTime deadline, ConcurrentQueue<string> errors)
    {
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                using var client = new TlsEngine(TlsRole.Client);
                using var server = new TlsEngine(TlsRole.Server);
                var output = new TlsOutput();
                client.Advance([], out _, output);
                byte[] hello = output.Bytes.ToArray();
                for (int i = 0; i < hello.Length && DateTime.UtcNow < deadline; i++)
                {
                    server.Advance(hello.AsSpan(i, 1), out _, output);
                    await Task.Delay(1000);
                    if (server.State == TlsEngineState.Closed)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex.GetType().Name + ": " + ex.Message);
        }
    }
}
