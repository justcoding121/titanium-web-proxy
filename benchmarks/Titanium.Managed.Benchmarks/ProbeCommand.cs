using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Tls;

namespace Titanium.Managed.Benchmarks;

internal static class ProbeCommand
{
    public static void Run(string[] args)
    {
        string kind = args.Length > 0 ? args[0] : "all";
        if (kind is "ladder")
        {
            PrintLadder();
            return;
        }

        if (kind is "quic")
        {
            PrintQuic();
            return;
        }

        if (kind is "machine" or "all")
        {
            PrintMachine();
        }

        if (kind is "alloc" or "all")
        {
            PrintAllocations();
        }

        if (kind is "rsa" or "all")
        {
            PrintRsa();
        }

        if (kind is "resume" or "all")
        {
            PrintResume();
        }

        if (kind is "idle" or "all")
        {
            int count = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 50_000;
            PrintIdle(count);
        }

        if (kind is "storm" or "all")
        {
            int count = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 10_000;
            PrintStorm(count);
        }
    }

    private static void PrintLadder()
    {
        Span<byte> scalar = stackalloc byte[32];
        Span<byte> point = stackalloc byte[32];
        Span<byte> output = stackalloc byte[32];
        RandomNumberGenerator.Fill(scalar);
        point[0] = 9;
        X25519.ScalarMult(scalar, point, output);
        var watch = Stopwatch.StartNew();
        const int count = 200;
        for (int i = 0; i < count; i++)
        {
            X25519.ScalarMult(scalar, point, output);
        }

        watch.Stop();
        Console.WriteLine($"ladder calls={count} us-per-call={watch.Elapsed.TotalMicroseconds / count:F0} first={output[0]}");
    }

    private static void PrintQuic()
    {
        using X509Certificate2 certificate = TlsBenchHarness.CreateEcdsa();
        var watch = Stopwatch.StartNew();
        QuicBenchHarness.Handshake(StackKind.Managed, certificate).GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"managed handshake ms={watch.Elapsed.TotalMilliseconds:F1}");
        watch.Restart();
        using var stop = new CancellationTokenSource();
        QuicBenchHarness.Opened opened = QuicBenchHarness.Open(StackKind.Managed, certificate).GetAwaiter().GetResult();
        Task echo = QuicBenchHarness.Echo(opened, stop.Token);
        byte[] payload = new byte[QuicBenchHarness.SmallBytes];
        for (int i = 0; i < 50; i++)
        {
            QuicBenchHarness.Exchange(opened, payload).GetAwaiter().GetResult();
        }

        stop.Cancel();
        opened.DisposeAsync().AsTask().GetAwaiter().GetResult();
        echo.GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"managed small-50 ms={watch.Elapsed.TotalMilliseconds:F1}");
        watch.Restart();
        QuicBenchHarness.Opened bulk = QuicBenchHarness.Open(StackKind.Managed, certificate).GetAwaiter().GetResult();
        QuicBenchHarness.Transfer(bulk, new byte[QuicBenchHarness.ChunkBytes], QuicBenchHarness.BulkBytes).GetAwaiter().GetResult();
        bulk.DisposeAsync().AsTask().GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"managed bulk-1MiB ms={watch.Elapsed.TotalMilliseconds:F1}");
        if (!QuicBenchHarness.PlatformListenerAllowed)
        {
            return;
        }

        watch.Restart();
        QuicBenchHarness.Handshake(StackKind.Platform, certificate).GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"platform handshake ms={watch.Elapsed.TotalMilliseconds:F1}");
        watch.Restart();
        using var platformStop = new CancellationTokenSource();
        QuicBenchHarness.Opened platform = QuicBenchHarness.Open(StackKind.Platform, certificate).GetAwaiter().GetResult();
        Task platformEcho = QuicBenchHarness.Echo(platform, platformStop.Token);
        for (int i = 0; i < 50; i++)
        {
            QuicBenchHarness.Exchange(platform, payload).GetAwaiter().GetResult();
        }

        platformStop.Cancel();
        platform.DisposeAsync().AsTask().GetAwaiter().GetResult();
        platformEcho.GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"platform small-50 ms={watch.Elapsed.TotalMilliseconds:F1}");
        watch.Restart();
        QuicBenchHarness.Opened platformBulk = QuicBenchHarness.Open(StackKind.Platform, certificate).GetAwaiter().GetResult();
        QuicBenchHarness.Transfer(platformBulk, new byte[QuicBenchHarness.ChunkBytes], QuicBenchHarness.BulkBytes).GetAwaiter().GetResult();
        platformBulk.DisposeAsync().AsTask().GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"platform bulk-1MiB ms={watch.Elapsed.TotalMilliseconds:F1}");
    }

    private static void PrintMachine()
    {
        Console.WriteLine($"machine os={Environment.OSVersion} processors={Environment.ProcessorCount} sdk-runtime={Environment.Version} 64bit={Environment.Is64BitProcess}");
        Console.WriteLine($"power={ReadPowerPlan()}");
    }

    private static void PrintAllocations()
    {
        using X509Certificate2 certificate = TlsBenchHarness.CreateEcdsa();
        var payload = new byte[16 * 1024];
        foreach (TransportKind transport in new[] { TransportKind.Memory, TransportKind.Loopback })
        {
            foreach (StackKind stack in new[] { StackKind.Platform, StackKind.Managed })
            {
                PrintOneAllocation(certificate, payload, transport, stack);
            }
        }
    }

    private static void PrintOneAllocation(X509Certificate2 certificate, byte[] payload, TransportKind transport, StackKind stack)
    {
            using OpenedConnection connection = TlsBenchHarness.Open(stack, transport, certificate, gear: null);
            var echo = new Thread(() =>
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    while (true)
                    {
                        int count = connection.Server.Read(buffer, 0, buffer.Length);
                        if (count == 0)
                        {
                            return;
                        }

                        connection.Server.Write(buffer, 0, count);
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
            })
            {
                IsBackground = true,
            };
            echo.Start();
            connection.Client.Write(payload);
            ReadFull(connection.Client, payload);
            long beforeWrite = GC.GetAllocatedBytesForCurrentThread();
            connection.Client.Write(payload);
            long writeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeWrite;
            long beforeRead = GC.GetAllocatedBytesForCurrentThread();
            ReadFull(connection.Client, payload);
            long readBytes = GC.GetAllocatedBytesForCurrentThread() - beforeRead;
            Console.WriteLine($"alloc transport={transport} stack={stack} write={writeBytes} read={readBytes}");
            connection.Dispose();
            echo.Join();
    }

    private static void PrintRsa()
    {
        using X509Certificate2 certificate = TlsBenchHarness.CreateRsa();
        try
        {
            using OpenedConnection connection = TlsBenchHarness.Open(StackKind.Managed, TransportKind.Memory, certificate, gear: null);
            Console.WriteLine("rsa managed-to-managed=completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"rsa managed-to-managed=failed {ex.GetType().Name}: {ex.Message}");
        }

        long platform = TimeOne(StackKind.Platform, certificate);
        Console.WriteLine($"rsa platform-full-handshake-us={platform}");
    }

    private static void PrintResume()
    {
        using X509Certificate2 certificate = TlsBenchHarness.CreateEcdsa();
        using var gear = new SessionGear();
        using (OpenedConnection primed = TlsBenchHarness.Open(StackKind.Managed, TransportKind.Memory, certificate, gear))
        {
            TlsBenchHarness.Exchange(primed.Client, primed.Server, new byte[1]);
        }

        using OpenedConnection resumed = TlsBenchHarness.Open(StackKind.Managed, TransportKind.Memory, certificate, gear);
        Console.WriteLine($"resume managed={resumed.Resumed}");
    }

    private static void PrintIdle(int count)
    {
        using X509Certificate2 certificate = TlsBenchHarness.CreateEcdsa();
        foreach (StackKind stack in new[] { StackKind.Platform, StackKind.Managed })
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
            long workingBefore = Process.GetCurrentProcess().WorkingSet64;
            var held = new List<OpenedConnection>(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    held.Add(TlsBenchHarness.Open(stack, TransportKind.Memory, certificate, gear: null));
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
                long workingAfter = Process.GetCurrentProcess().WorkingSet64;
                Console.WriteLine($"idle stack={stack} count={count} gc-bytes-per-connection={(memoryAfter - memoryBefore) / count} working-set-delta={workingAfter - workingBefore}");
            }
            finally
            {
                foreach (OpenedConnection connection in held)
                {
                    connection.Dispose();
                }
            }
        }
    }

    private static void PrintStorm(int count)
    {
        using X509Certificate2 certificate = TlsBenchHarness.CreateEcdsa();
        foreach (StackKind stack in new[] { StackKind.Platform, StackKind.Managed })
        {
            using OpenedConnection quiet = TlsBenchHarness.Open(stack, TransportKind.Memory, certificate, gear: null);
            var payload = new byte[100];
            var latencies = new long[32];
            Exception? stormError = null;
            int started = 0;
            var gate = new ManualResetEventSlim(false);
            var threads = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                threads[i] = new Thread(() =>
                {
                    gate.Wait();
                    try
                    {
                        using OpenedConnection connection = TlsBenchHarness.Open(stack, TransportKind.Memory, certificate, gear: null);
                        Interlocked.Increment(ref started);
                    }
                    catch (Exception ex)
                    {
                        stormError ??= ex;
                    }
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }

            gate.Set();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < latencies.Length; i++)
            {
                long start = watch.ElapsedTicks;
                TlsBenchHarness.Exchange(quiet.Client, quiet.Server, payload);
                latencies[i] = watch.ElapsedTicks - start;
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            Array.Sort(latencies);
            long p99 = latencies[(int)(latencies.Length * 0.99)];
            double us = p99 * 1_000_000.0 / Stopwatch.Frequency;
            Console.WriteLine($"storm stack={stack} requested={count} completed={Volatile.Read(ref started)} quiet-p99-us={us:F0} error={(stormError is null ? "none" : stormError.GetType().Name)}");
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

    private static long TimeOne(StackKind stack, X509Certificate2 certificate)
    {
        var watch = Stopwatch.StartNew();
        using OpenedConnection connection = TlsBenchHarness.Open(stack, TransportKind.Memory, certificate, gear: null);
        return (long)(watch.Elapsed.TotalMicroseconds);
    }

    private static string ReadPowerPlan()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = "/getactivescheme",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            process.Start();
            string text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return text.Length == 0 ? "unknown" : text;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
}
