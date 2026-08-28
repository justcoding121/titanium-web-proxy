using System.Net;
using System.Text;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;

namespace Titanium.Plus.Dashboard;

/// <summary>Static HTML admin surface for config dump / destination states / drain.</summary>
public sealed class DashboardHost : IDisposable
{
    private readonly ControlPlaneServer _controlPlane;
    private readonly DrainOperations _operations;
    private readonly PrometheusMetricsExporter _metrics;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public DashboardHost(ControlPlaneServer controlPlane, DrainOperations operations, PrometheusMetricsExporter metrics)
    {
        _controlPlane = controlPlane;
        _operations = operations;
        _metrics = metrics;
    }

    public void Start()
    {
        // Serve dashboard on control-plane port + 1 when possible; skeleton shares documentation only.
        var uri = new Uri(_controlPlane.Prefix);
        var dashPort = uri.Port + 1;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{uri.Host}:{dashPort}/");
        try
        {
            _listener.Start();
            _ = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch
        {
            // Port may be in use; dashboard is best-effort in skeleton.
            _listener = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch
            {
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                var body = _metrics.Render();
                await WriteAsync(ctx.Response, "text/plain; version=0.0.4", body);
                continue;
            }

            if (path.StartsWith("/drain/", StringComparison.OrdinalIgnoreCase) &&
                ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var id = path["/drain/".Length..];
                _operations.Drain(id);
                await WriteAsync(ctx.Response, "text/plain", "ok");
                continue;
            }

            await WriteAsync(ctx.Response, "text/html; charset=utf-8", Html);
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"/><title>Titanium Plus Dashboard</title></head>
        <body>
          <h1>Titanium Plus</h1>
          <p>Control plane binds loopback by default and requires the shared-secret header.</p>
          <ul>
            <li><a href="/metrics">Prometheus metrics</a></li>
            <li>POST /drain/{destinationId} to drain</li>
          </ul>
        </body>
        </html>
        """;
}
