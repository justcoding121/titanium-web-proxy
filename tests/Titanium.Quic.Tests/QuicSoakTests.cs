#pragma warning disable CA1416 // The test names the BCL stream type. It does not open a platform listener.
#pragma warning disable CA2022 // The server reads until the peer closes, which is a short count.
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
[DoNotParallelize]
public sealed class QuicSoakTests
{
    private const int InFlightCap = 32;

    [TestMethod]
    [TestCategory("Slow")]
    [Timeout(2700000)]
    public async Task ThirtyMinutesOfConnectionsStayBounded()
    {
        int seconds = 1800;
        string? configured = Environment.GetEnvironmentVariable("TITANIUM_SOAK_SECONDS");
        if (int.TryParse(configured, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            seconds = parsed;
        }

        using X509Certificate2 certificate = Certificate();
        ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            HandshakeTimeout = TimeSpan.FromSeconds(15),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
            }),
        });
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();
        var servers = new ConcurrentBag<Task>();
        Task accept = AcceptLoop(listener, stop.Token, errors, servers);
        for (int i = 0; i < 4; i++)
        {
            await Exchange(listener.LocalEndPoint, stop.Token);
        }

        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        int inFlight = 0;
        int maxInFlight = 0;
        int completed = 0;
        int aborted = 0;
        int reset = 0;
        int delayed = 0;
        using var gate = new SemaphoreSlim(InFlightCap);
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        Task slow = SlowLoris(listener.LocalEndPoint, deadline, errors);

        while (DateTime.UtcNow < deadline && errors.IsEmpty)
        {
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                continue;
            }
            int current = Interlocked.Increment(ref inFlight);
            int seen = Volatile.Read(ref maxInFlight);
            while (current > seen && Interlocked.CompareExchange(ref maxInFlight, current, seen) != seen)
            {
                seen = Volatile.Read(ref maxInFlight);
            }

            int kind = Random.Shared.Next(3);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (kind == 0)
                    {
                        await Exchange(listener.LocalEndPoint, stop.Token);
                        Interlocked.Increment(ref completed);
                    }
                    else if (kind == 1)
                    {
                        await Abort(listener.LocalEndPoint, stop.Token);
                        Interlocked.Increment(ref aborted);
                    }
                    else
                    {
                        await ResetDuringHandshake(listener.LocalEndPoint, stop.Token);
                        Interlocked.Increment(ref reset);
                    }
                }
                catch (Exception ex) when (Expected(ex))
                {
                    if (kind == 2)
                    {
                        Interlocked.Increment(ref reset);
                    }
                    else if (kind == 1)
                    {
                        Interlocked.Increment(ref aborted);
                    }
                    else
                    {
                        Interlocked.Increment(ref delayed);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex.ToString());
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
        stop.Cancel();
        try
        {
            await accept;
        }
        catch (Exception ex) when (Expected(ex))
        {
        }

        try
        {
            await Task.WhenAll(servers.ToArray());
        }
        catch (Exception ex) when (Expected(ex))
        {
        }

        await listener.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long after = GC.GetTotalMemory(true);
        string counts = completed + " completed, " + aborted + " aborted, " + reset + " reset, " + delayed + " delayed";
        Assert.AreEqual(0, errors.Count, errors.TryPeek(out string? first) ? first : string.Empty);
        Assert.AreEqual(0, Volatile.Read(ref inFlight), counts);
        Assert.IsTrue(Volatile.Read(ref maxInFlight) <= InFlightCap, counts);
        Assert.IsTrue(completed > 0, counts);
        Assert.IsTrue(aborted > 0, counts);
        Assert.IsTrue(reset > 0, counts);
        Assert.IsTrue(after < baseline + (64L * 1024L * 1024L), after + " against " + baseline);
    }

    private static async Task AcceptLoop(ManagedQuicListener listener, CancellationToken token, ConcurrentQueue<string> errors, ConcurrentBag<Task> servers)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                ManagedQuicConnection server;
                try
                {
                    server = await listener.AcceptConnectionAsync(token);
                }
                catch (QuicException)
                {
                    continue;
                }

                servers.Add(Serve(server, token, errors));
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
        }
    }

    private static async Task Serve(ManagedQuicConnection server, CancellationToken token, ConcurrentQueue<string> errors)
    {
        try
        {
            await using (server)
            {
                ManagedQuicStream inbound = await server.AcceptInboundStreamAsync(token);
                var buffer = new byte[256];
                while (true)
                {
                    int count = await inbound.ReadAsync(buffer, token);
                    if (count == 0)
                    {
                        break;
                    }
                }

                await server.CloseAsync(0, token);
            }
        }
        catch (Exception ex) when (Expected(ex))
        {
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex.ToString());
        }
    }

    private static async Task Exchange(IPEndPoint remote, CancellationToken token)
    {
        await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(remote), token);
        ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
        await outbound.WriteAsync(new byte[] { 7, 8, 9, 10 }, token);
        await client.CloseAsync(0, token);
    }

    private static async Task Abort(IPEndPoint remote, CancellationToken token)
    {
        ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(remote), token);
        try
        {
            await client.CloseAsync(1, token);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static async Task ResetDuringHandshake(IPEndPoint remote, CancellationToken token)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        ValueTask<ManagedQuicConnection> connecting = ManagedQuicConnection.ConnectAsync(Client(remote), handshake.Token);
        handshake.Cancel();
        ManagedQuicConnection client = await connecting;
        await client.DisposeAsync();
    }

    private static async Task SlowLoris(IPEndPoint remote, DateTime deadline, ConcurrentQueue<string> errors)
    {
        try
        {
            await using ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(Client(remote), CancellationToken.None);
            ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, CancellationToken.None);
            var one = new byte[] { 1 };
            while (DateTime.UtcNow < deadline)
            {
                await outbound.WriteAsync(one);
                await Task.Delay(1000);
            }

            await client.CloseAsync(0);
        }
        catch (Exception ex) when (Expected(ex))
        {
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex.ToString());
        }
    }

    private static ManagedQuicClientConnectionOptions Client(IPEndPoint remote) => new()
    {
        RemoteEndPoint = remote,
        HandshakeTimeout = TimeSpan.FromSeconds(15),
        ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = "localhost" },
    };

    private static bool Expected(Exception exception) =>
        exception is OperationCanceledException or QuicException or ObjectDisposedException;

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
