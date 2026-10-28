using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Titanium.Plus.Audit;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Dashboard;

/// <summary>
/// Authenticated HTML admin for destination states / drain / metrics / recent audit events. It listens on the
/// control plane's host (and TLS certificate, when set) and shares its audit trail and failed-login throttle.
/// </summary>
public sealed class DashboardHost : IDisposable
{
    private const int MaxBindAttempts = 8;
    private const int AuditRowsOnPage = 50;

    private static readonly JsonSerializerOptions AuditJson = new() { WriteIndented = false };

    private readonly ControlPlaneServer _controlPlane;
    private readonly DrainOperations _operations;
    private readonly PrometheusMetricsExporter _metrics;
    private readonly IClusterManager? _clusters;
    private readonly int? _requestedPort;
    private ControlHttpServer? _server;

    public DashboardHost(
        ControlPlaneServer controlPlane,
        DrainOperations operations,
        PrometheusMetricsExporter metrics,
        IClusterManager? clusters,
        int? dashboardPort = null)
    {
        _controlPlane = controlPlane;
        _operations = operations;
        _metrics = metrics;
        _clusters = clusters;
        _requestedPort = dashboardPort is > 0 and < 65536 ? dashboardPort : null;
    }

    public string? Prefix { get; private set; }

    public int? BoundPort { get; private set; }

    /// <summary>The audit trail this dashboard shows (shared with the control plane).</summary>
    public AuditLog Audit => _controlPlane.Security.Audit;

    public void Start()
    {
        Exception? last = null;
        var attempts = _requestedPort.HasValue ? 1 : MaxBindAttempts;
        var address = ControlHttpServer.ResolveBindAddress(_controlPlane.Host);

        for (var i = 0; i < attempts; i++)
        {
            var server = new ControlHttpServer(address, _requestedPort ?? 0, _controlPlane.Certificate, HandleAsync);
            try
            {
                server.Start();
            }
            catch (SocketException ex)
            {
                last = ex;
                server.Dispose();
                continue;
            }

            _server = server;
            BoundPort = server.Port;
            var uri = new Uri(_controlPlane.Prefix);
            // Loopback by default; plain HTTP off loopback only with controlPlane.allowInsecureNonLoopback.
#pragma warning disable S5332
            Prefix = $"{uri.Scheme}://{uri.Host}:{BoundPort}/";
#pragma warning restore S5332
            return;
        }

        throw new InvalidOperationException(
            _requestedPort.HasValue
                ? $"Dashboard failed to bind controlPlane.dashboardPort={_requestedPort.Value}."
                : "Dashboard failed to bind an ephemeral port after retries.",
            last);
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
        BoundPort = null;
    }

    private async Task<ControlHttpResponse> HandleAsync(ControlHttpRequest request, CancellationToken cancellationToken)
    {
        var denied = _controlPlane.Security.Authenticate(
            request, ControlPlaneSecurity.DashboardSurface, _controlPlane.SharedSecret, out var actor);
        if (denied is not null)
        {
            return denied;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        var path = request.Path;
        var remote = request.RemoteAddress?.ToString();
        var isPost = request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);

        if (path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return ControlHttpResponse.Text(200, "text/plain; version=0.0.4", _metrics.Render());
        }

        if (path.StartsWith("/api/snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotResponse();
        }

        if (path.StartsWith("/api/audit", StringComparison.OrdinalIgnoreCase))
        {
            return ControlHttpResponse.Text(200, "application/json", JsonSerializer.Serialize(
                Audit.Recent().Select(e => new
                {
                    time = e.Time.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    e.Actor,
                    e.Remote,
                    e.Action,
                    e.Target,
                    e.Result,
                }).ToList(),
                new JsonSerializerOptions(AuditJson) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }

        if (isPost && path.StartsWith("/drain/", StringComparison.OrdinalIgnoreCase))
        {
            return Mutate(actor, remote, AuditActions.Drain, path["/drain/".Length..], _operations.Drain);
        }

        if (isPost && path.StartsWith("/healthy/", StringComparison.OrdinalIgnoreCase))
        {
            return Mutate(actor, remote, AuditActions.Healthy, path["/healthy/".Length..], _operations.MarkHealthy);
        }

        // The page itself is the dashboard "login": a person opened it with the shared secret.
        Audit.Record(actor, remote, AuditActions.Login, path, AuditResults.Ok);
        return ControlHttpResponse.Text(200, "text/html; charset=utf-8", BuildHtml());
    }

    private ControlHttpResponse Mutate(string actor, string? remote, string action, string escapedId, Action<string> operation)
    {
        var id = Uri.UnescapeDataString(escapedId);
        try
        {
            operation(id);
        }
        catch (ArgumentException)
        {
            Audit.Record(actor, remote, action, id, AuditResults.Rejected);
            return ControlHttpResponse.Text(400, "text/plain; charset=utf-8", "bad request");
        }

        Audit.Record(actor, remote, action, id, AuditResults.Ok);
        return ControlHttpResponse.Text(200, "text/plain; charset=utf-8", "ok");
    }

    private ControlHttpResponse SnapshotResponse()
    {
        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        var json = JsonSerializer.Serialize(new
        {
            destinationStates = snap.DestinationStates,
            clusters = snap.Clusters.Keys,
        });
        return ControlHttpResponse.Text(200, "application/json", json);
    }

    private string BuildHtml()
    {
        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        var rows = new StringBuilder();
        foreach (var (id, state) in snap.DestinationStates.OrderBy(kv => kv.Key))
        {
            var enc = WebUtility.HtmlEncode(id);
            var esc = Uri.EscapeDataString(id);
            rows.Append("<tr data-testid=\"dest-row-").Append(enc).Append("\"><td>").Append(enc)
                .Append("</td><td data-testid=\"dest-state-").Append(enc).Append("\">")
                .Append(state).Append("</td><td>")
                .Append("<button data-testid=\"btn-drain-").Append(enc)
                .Append("\" onclick=\"post('/drain/").Append(esc).Append("')\">Drain</button> ")
                .Append("<button data-testid=\"btn-healthy-").Append(enc)
                .Append("\" onclick=\"post('/healthy/").Append(esc).Append("')\">Healthy</button>")
                .Append("</td></tr>");
        }

        if (rows.Length == 0)
        {
            rows.Append("<tr><td colspan=\"3\">No destinations in snapshot.</td></tr>");
        }

        var auditRows = new StringBuilder();
        foreach (var e in Audit.Recent(AuditRowsOnPage))
        {
            auditRows.Append("<tr><td>")
                .Append(WebUtility.HtmlEncode(e.Time.UtcDateTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(e.Actor))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(e.Remote))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(e.Action))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(e.Target))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(e.Result))
                .Append("</td></tr>");
        }

        if (auditRows.Length == 0)
        {
            auditRows.Append("<tr><td colspan=\"6\">No audit events yet.</td></tr>");
        }

        return new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
            .Append("<title>Titanium Plus Dashboard</title>")
            .Append("<style>body{font-family:system-ui,sans-serif;margin:1.5rem}")
            .Append("table{border-collapse:collapse;width:100%;max-width:720px}")
            .Append("th,td{border:1px solid #ccc;padding:.4rem .6rem;text-align:left}")
            .Append("code{background:#f4f4f4;padding:.1rem .3rem}</style></head><body data-testid=\"plus-dashboard\">")
            .Append("<h1>Titanium Plus</h1><p>Authenticated with <code>")
            .Append(ControlPlaneServer.SharedSecretHeader)
            .Append("</code>. Control plane: <code>")
            .Append(WebUtility.HtmlEncode(_controlPlane.Prefix))
            .Append("</code></p><p><a data-testid=\"link-metrics\" href=\"/metrics\">Prometheus metrics</a> · ")
            .Append("<a data-testid=\"link-snapshot\" href=\"/api/snapshot\">JSON snapshot</a> · ")
            .Append("<a data-testid=\"link-audit\" href=\"/api/audit\">JSON audit</a></p><table>")
            .Append("<thead><tr><th>Destination</th><th>State</th><th>Actions</th></tr></thead><tbody>")
            .Append(rows)
            .Append("</tbody></table><h2>Recent audit events</h2><table data-testid=\"audit-table\">")
            .Append("<thead><tr><th>Time (UTC)</th><th>Actor</th><th>Remote</th><th>Action</th><th>Target</th><th>Result</th></tr></thead><tbody>")
            .Append(auditRows)
            .Append("</tbody></table><script>")
            .Append("async function post(path){const secret=prompt('Control secret');")
            .Append("if(!secret)return;await fetch(path,{method:'POST',headers:{'")
            .Append(ControlPlaneServer.SharedSecretHeader)
            .Append("':secret}});location.reload();}</script></body></html>")
            .ToString();
    }
}
