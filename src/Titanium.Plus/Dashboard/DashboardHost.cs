using System.Net;
using System.Text;
using System.Text.Json;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Dashboard;

/// <summary>Authenticated HTML admin for destination states / drain / metrics.</summary>
public sealed class DashboardHost : IDisposable
{
    private readonly ControlPlaneServer _controlPlane;
    private readonly DrainOperations _operations;
    private readonly PrometheusMetricsExporter _metrics;
    private readonly IClusterManager? _clusters;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public DashboardHost(
        ControlPlaneServer controlPlane,
        DrainOperations operations,
        PrometheusMetricsExporter metrics,
        IClusterManager? clusters)
    {
        _controlPlane = controlPlane;
        _operations = operations;
        _metrics = metrics;
        _clusters = clusters;
    }

    public string? Prefix { get; private set; }

    public void Start()
    {
        var uri = new Uri(_controlPlane.Prefix);
        var dashPort = uri.Port + 1;
        // Loopback-oriented dashboard over HttpListener; shared-secret auth, not public TLS.
#pragma warning disable S5332
        Prefix = $"http://{uri.Host}:{dashPort}/";
#pragma warning restore S5332
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
        try
        {
            _listener.Start();
            _ = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        }
        catch
        {
            _listener = null;
            Prefix = null;
            _cts.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        _listener?.Stop();
        _listener?.Close();
        _cts?.Dispose();
        _cts = null;
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

            try
            {
                await HandleRequestAsync(ctx, cancellationToken);
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
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        if (!Authorize(ctx.Request))
        {
            ctx.Response.StatusCode = 401;
            await WriteAsync(ctx.Response, "text/plain", "unauthorized", cancellationToken);
            return;
        }

        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(ctx.Response, "text/plain; version=0.0.4", _metrics.Render(), cancellationToken);
            return;
        }

        if (path.StartsWith("/api/snapshot", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSnapshotAsync(ctx, cancellationToken);
            return;
        }

        if (path.StartsWith("/drain/", StringComparison.OrdinalIgnoreCase) &&
            ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var id = Uri.UnescapeDataString(path["/drain/".Length..]);
            _operations.Drain(id);
            await WriteAsync(ctx.Response, "text/plain", "ok", cancellationToken);
            return;
        }

        if (path.StartsWith("/healthy/", StringComparison.OrdinalIgnoreCase) &&
            ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var id = Uri.UnescapeDataString(path["/healthy/".Length..]);
            _operations.MarkHealthy(id);
            await WriteAsync(ctx.Response, "text/plain", "ok", cancellationToken);
            return;
        }

        await WriteAsync(ctx.Response, "text/html; charset=utf-8", BuildHtml(), cancellationToken);
    }

    private async Task WriteSnapshotAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        var json = JsonSerializer.Serialize(new
        {
            destinationStates = snap.DestinationStates,
            clusters = snap.Clusters.Keys,
        });
        await WriteAsync(ctx.Response, "application/json", json, cancellationToken);
    }

    private bool Authorize(HttpListenerRequest request)
    {
        var header = request.Headers[ControlPlaneServer.SharedSecretHeader];
        return !string.IsNullOrEmpty(header) &&
               string.Equals(header, _controlPlane.SharedSecret, StringComparison.Ordinal);
    }

    private string BuildHtml()
    {
        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        var rows = new StringBuilder();
        foreach (var (id, state) in snap.DestinationStates.OrderBy(kv => kv.Key))
        {
            rows.Append("<tr><td>").Append(WebUtility.HtmlEncode(id)).Append("</td><td>")
                .Append(state).Append("</td><td>")
                .Append("<button onclick=\"post('/drain/").Append(Uri.EscapeDataString(id))
                .Append("')\">Drain</button> ")
                .Append("<button onclick=\"post('/healthy/").Append(Uri.EscapeDataString(id))
                .Append("')\">Healthy</button>")
                .Append("</td></tr>");
        }

        if (rows.Length == 0)
        {
            rows.Append("<tr><td colspan=\"3\">No destinations in snapshot.</td></tr>");
        }

        return new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
            .Append("<title>Titanium Plus Dashboard</title>")
            .Append("<style>body{font-family:system-ui,sans-serif;margin:1.5rem}")
            .Append("table{border-collapse:collapse;width:100%;max-width:720px}")
            .Append("th,td{border:1px solid #ccc;padding:.4rem .6rem;text-align:left}")
            .Append("code{background:#f4f4f4;padding:.1rem .3rem}</style></head><body>")
            .Append("<h1>Titanium Plus</h1><p>Authenticated with <code>")
            .Append(ControlPlaneServer.SharedSecretHeader)
            .Append("</code>. Control plane: <code>")
            .Append(WebUtility.HtmlEncode(_controlPlane.Prefix))
            .Append("</code></p><p><a href=\"/metrics\">Prometheus metrics</a> · ")
            .Append("<a href=\"/api/snapshot\">JSON snapshot</a></p><table>")
            .Append("<thead><tr><th>Destination</th><th>State</th><th>Actions</th></tr></thead><tbody>")
            .Append(rows)
            .Append("</tbody></table><script>")
            .Append("async function post(path){const secret=prompt('Control secret');")
            .Append("if(!secret)return;await fetch(path,{method:'POST',headers:{'")
            .Append(ControlPlaneServer.SharedSecretHeader)
            .Append("':secret}});location.reload();}</script></body></html>")
            .ToString();
    }

    private static async Task WriteAsync(
        HttpListenerResponse response,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }
}
