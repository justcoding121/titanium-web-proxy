using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
/// An in-process origin for callouts: HTTP/1.1 <c>GET /hello</c> answers "world", and an h2c endpoint serves gRPC
/// <c>echo.Echo/Unary</c> ("pong:" + message), <c>echo.Echo/Stream</c> ("echo:" + each message), and answers any
/// other method with a trailers-only UNIMPLEMENTED (12).
/// </summary>
internal sealed class CalloutOrigin : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CalloutOrigin(WebApplication app, int httpPort, int grpcPort)
    {
        _app = app;
        HttpTarget = "127.0.0.1:" + httpPort;
        GrpcTarget = "127.0.0.1:" + grpcPort;
    }

    public string HttpTarget { get; }

    public string GrpcTarget { get; }

    public static async Task<CalloutOrigin> StartAsync()
    {
        var httpPort = FreePort();
        var grpcPort = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, httpPort, o => o.Protocols = HttpProtocols.Http1);
            kestrel.Listen(IPAddress.Loopback, grpcPort, o => o.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();
        app.MapGet("/hello", (HttpContext context) =>
        {
            context.Response.Headers["x-origin"] = "yes";
            return "world";
        });
        app.MapPost("/echo.Echo/{method}", async (HttpContext context, string method) =>
        {
            context.Response.ContentType = "application/grpc";
            switch (method)
            {
                case "Unary":
                {
                    var message = await ReadFrameAsync(context.Request.Body);
                    await WriteFrameAsync(context.Response.Body, "pong:" + message);
                    context.Response.AppendTrailer("grpc-status", "0");
                    break;
                }
                case "Stream":
                {
                    await context.Response.StartAsync();
                    while (await ReadFrameAsync(context.Request.Body) is { } message)
                    {
                        await WriteFrameAsync(context.Response.Body, "echo:" + message);
                        await context.Response.Body.FlushAsync();
                    }

                    context.Response.AppendTrailer("grpc-status", "0");
                    break;
                }
                default:
                    context.Response.Headers["grpc-status"] = "12";
                    break;
            }
        });

        await app.StartAsync();
        return new CalloutOrigin(app, httpPort, grpcPort);
    }

    /// <summary>A loopback port nothing listens on, for callouts that must fail to connect.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static async Task<string?> ReadFrameAsync(Stream body)
    {
        var header = new byte[5];
        var read = 0;
        while (read < 5)
        {
            var n = await body.ReadAsync(header.AsMemory(read));
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        var message = new byte[BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1))];
        await body.ReadExactlyAsync(message);
        return Encoding.UTF8.GetString(message);
    }

    private static async Task WriteFrameAsync(Stream body, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var frame = new byte[bytes.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)bytes.Length);
        bytes.CopyTo(frame, 5);
        await body.WriteAsync(frame);
    }
}
