using System.Net;
using System.Text;
using System.Text.Json;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.ControlPlane;

/// <summary>
/// Loopback-default control plane with shared-secret header auth (GET/PUT snapshot).
/// </summary>
public sealed class ControlPlaneServer : IDisposable
{
    public const string SharedSecretHeader = "X-Titanium-Control-Secret";

    private readonly IClusterManager? _clusters;
    private readonly string _host;
    private readonly int _port;
    private readonly string _sharedSecret;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public ControlPlaneServer(IClusterManager? clusters, string host, int port, string sharedSecret)
    {
        _clusters = clusters;
        _host = host;
        _port = port;
        _sharedSecret = sharedSecret;
    }

    public string Prefix => $"http://{_host}:{_port}/";

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!Authorize(ctx.Request))
            {
                ctx.Response.StatusCode = 401;
                await WriteAsync(ctx.Response, "unauthorized");
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Equals("/v1/snapshot", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
                    var json = JsonSerializer.Serialize(new
                    {
                        clusters = snap.Clusters.Keys,
                        destinationStates = snap.DestinationStates,
                    });
                    ctx.Response.ContentType = "application/json";
                    await WriteAsync(ctx.Response, json);
                    return;
                }

                if (ctx.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    // Skeleton: accept body and acknowledge; full Apply wiring uses ClusterManager.
                    ctx.Response.StatusCode = 202;
                    await WriteAsync(ctx.Response, "{\"status\":\"accepted\",\"bytes\":" + body.Length + "}");
                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            await WriteAsync(ctx.Response, "not found");
        }
        catch
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private bool Authorize(HttpListenerRequest request)
    {
        var header = request.Headers[SharedSecretHeader];
        return !string.IsNullOrEmpty(header) &&
               string.Equals(header, _sharedSecret, StringComparison.Ordinal);
    }

    private static async Task WriteAsync(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
