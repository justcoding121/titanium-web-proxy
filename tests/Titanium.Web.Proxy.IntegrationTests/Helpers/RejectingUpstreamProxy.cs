#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     Local HTTP proxy that always rejects CONNECT with a fixed status/body for issue #857 tests.
/// </summary>
internal sealed class RejectingUpstreamProxy : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly ConcurrentBag<Task> clientTasks = new();
    private readonly TcpListener listener;
    private readonly int statusCode;
    private readonly string reasonPhrase;
    private readonly string body;
    private readonly Task acceptTask;

    internal RejectingUpstreamProxy(int statusCode, string reasonPhrase, string body)
    {
        this.statusCode = statusCode;
        this.reasonPhrase = reasonPhrase;
        this.body = body;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        acceptTask = AcceptClientsAsync();
    }

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        listener.Stop();

        try
        {
            acceptTask.GetAwaiter().GetResult();
            Task.WaitAll(clientTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException)
        {
        }

        cancellationTokenSource.Dispose();
    }

    private async Task AcceptClientsAsync()
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationTokenSource.Token);
                clientTasks.Add(HandleClientAsync(client, cancellationTokenSource.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var headers = await ReadHeadersAsync(stream, cancellationToken);
            if (headers == null) return;

            var response =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                "Proxy-Authenticate: Basic realm=\"test\"\r\n" +
                "Connection: close\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var value = new byte[1];
        var matched = 0;
        var terminator = new byte[] { 13, 10, 13, 10 };

        while (buffer.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(value, cancellationToken);
            if (read == 0) return null;

            buffer.WriteByte(value[0]);
            matched = value[0] == terminator[matched] ? matched + 1 : value[0] == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
                return Encoding.ASCII.GetString(buffer.ToArray());
        }

        throw new InvalidDataException("Proxy request headers exceeded the test limit.");
    }
}
