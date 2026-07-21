#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

internal sealed class FakeUpstreamProxy : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly ConcurrentBag<Task> clientTasks = new();
    private readonly TcpListener listener;
    private readonly int httpsTargetPort;
    private readonly Task acceptTask;

    internal FakeUpstreamProxy(int httpsTargetPort)
    {
        this.httpsTargetPort = httpsTargetPort;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        acceptTask = AcceptClientsAsync();
    }

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal ConcurrentQueue<string> ProxyAuthorizationValues { get; } = new();

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
            while (!cancellationToken.IsCancellationRequested)
            {
                var requestHeaders = await ReadHeadersAsync(stream, cancellationToken);
                if (requestHeaders == null) return;

                var requestLine = requestHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                var proxyAuthorization = GetHeaderValue(requestHeaders, "Proxy-Authorization") ?? string.Empty;
                ProxyAuthorizationValues.Enqueue(proxyAuthorization);

                if (proxyAuthorization.Length == 0)
                {
                    await Write407Async(stream, "NTLM", cancellationToken);
                    continue;
                }

                if (proxyAuthorization.Equals("NTLM t1", StringComparison.Ordinal))
                {
                    await Write407Async(stream, "NTLM challenge", cancellationToken);
                    continue;
                }

                if (!proxyAuthorization.Equals("NTLM t2", StringComparison.Ordinal))
                {
                    await WriteAsync(stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        cancellationToken);
                    return;
                }

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    await TunnelHttpsAsync(stream, cancellationToken);
                    return;
                }

                const string body = "authenticated plain HTTP";
                await WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}",
                    cancellationToken);
                return;
            }
        }
    }

    private async Task TunnelHttpsAsync(NetworkStream clientStream, CancellationToken cancellationToken)
    {
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, httpsTargetPort, cancellationToken);
        await WriteAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", cancellationToken);

        var targetStream = target.GetStream();
        var clientToTarget = clientStream.CopyToAsync(targetStream, cancellationToken);
        var targetToClient = targetStream.CopyToAsync(clientStream, cancellationToken);
        await Task.WhenAny(clientToTarget, targetToClient);
    }

    private static async Task Write407Async(NetworkStream stream, string challenge,
        CancellationToken cancellationToken)
    {
        const string body = "deny";
        await WriteAsync(stream,
            "HTTP/1.1 407 Proxy Authentication Required\r\n" +
            $"Proxy-Authenticate: {challenge}\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n{body}",
            cancellationToken);
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

    private static string? GetHeaderValue(string headers, string name)
    {
        var prefix = name + ":";
        return headers.Split(new[] { "\r\n" }, StringSplitOptions.None)
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Substring(prefix.Length).Trim();
    }

    private static Task WriteAsync(NetworkStream stream, string value, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).AsTask();
    }
}
