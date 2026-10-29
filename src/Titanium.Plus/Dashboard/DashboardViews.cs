using System.Globalization;
using System.Net;
using System.Text;

namespace Titanium.Plus.Dashboard;

/// <summary>Which sign-in controls the login page shows.</summary>
internal sealed record DashboardLoginFeatures(bool ShowSecret, bool ShowLocalUsers, bool ShowOidc);

/// <summary>The dashboard's views, and the one place that turns values into HTML text.</summary>
internal enum DashboardView
{
    Overview,
    Routes,
    Health,
    Config,
    Certificates,
    Audit,
}

/// <summary>
///     Server-rendered HTML for each view. Every dynamic value goes through <see cref="Enc"/>, so a destination id such
///     as <c>&lt;img src=x onerror=...&gt;</c> is text, never markup. There is no inline script, inline style, or
///     event-handler attribute: the page loads <c>/assets/dashboard.js</c> and <c>/assets/dashboard.css</c>, which the
///     Content-Security-Policy (<c>default-src 'self'</c>) allows.
/// </summary>
internal static class DashboardViews
{
    internal static readonly (DashboardView View, string Path, string Slug, string Title)[] Navigation =
    [
        (DashboardView.Overview, "/", "overview", "Overview"),
        (DashboardView.Routes, "/routes", "routes", "Routes and clusters"),
        (DashboardView.Health, "/health", "health", "Health and ejections"),
        (DashboardView.Config, "/config", "config", "Config and reload"),
        (DashboardView.Certificates, "/certificates", "certs", "Certificates"),
        (DashboardView.Audit, "/audit", "audit", "Audit"),
    ];

    public static string Enc(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string Seconds(double? seconds) => seconds switch
    {
        null => "-",
        < 1 => (seconds.Value * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " ms",
        _ => seconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + " s",
    };

    public static string Ratio(double? ratio) =>
        ratio is null ? "-" : (ratio.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %";

    public static string Rate(double perSecond) => perSecond.ToString("0.##", CultureInfo.InvariantCulture) + " /s";

    public static string Login(string? error, DashboardLoginFeatures? features = null)
    {
        features ??= new DashboardLoginFeatures(ShowSecret: true, ShowLocalUsers: false, ShowOidc: false);
        var sb = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>")
            .Append("<title>Titanium Plus - Sign in</title>")
            .Append("<link rel=\"stylesheet\" href=\"/assets/dashboard.css\"/></head>")
            .Append("<body data-testid=\"plus-login\" class=\"login\"><main>")
            .Append("<h1>Titanium Plus</h1>");

        if (features.ShowOidc)
        {
            sb.Append("<p><a href=\"/login/oidc\" data-testid=\"login-oidc\">Sign in with SSO</a></p>");
        }

        if (features.ShowLocalUsers || features.ShowSecret)
        {
            sb.Append("<form method=\"post\" action=\"/login\" data-testid=\"login-form\">");
            if (features.ShowLocalUsers)
            {
                sb.Append("<label for=\"user\">Username</label>")
                    .Append("<input id=\"user\" name=\"user\" type=\"text\" autocomplete=\"username\" data-testid=\"login-user\"/>")
                    .Append("<label for=\"password\">Password</label>")
                    .Append("<input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" data-testid=\"login-password\"/>");
            }

            if (features.ShowSecret)
            {
                sb.Append("<label for=\"secret\">").Append(features.ShowLocalUsers ? "Break-glass secret" : "Secret").Append("</label>")
                    .Append("<input id=\"secret\" name=\"secret\" type=\"password\" autocomplete=\"current-password\" data-testid=\"login-secret\"");
                if (!features.ShowLocalUsers)
                {
                    sb.Append(" required");
                }

                sb.Append("/>");
            }

            sb.Append("<button type=\"submit\" data-testid=\"login-submit\">Sign in</button></form>");
        }

        if (error is not null)
        {
            sb.Append("<p class=\"error\" role=\"alert\" data-testid=\"login-error\">").Append(Enc(error)).Append("</p>");
        }

        return sb.Append("</main></body></html>").ToString();
    }

    public static string Page(DashboardView view, string role, string csrfToken, string body)
    {
        var current = Array.Find(Navigation, n => n.View == view);
        var sb = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>")
            .Append("<title>Titanium Plus - ").Append(Enc(current.Title)).Append("</title>")
            .Append("<link rel=\"stylesheet\" href=\"/assets/dashboard.css\"/></head>")
            .Append("<body data-testid=\"plus-dashboard\" data-view=\"").Append(current.Slug)
            .Append("\" data-role=\"").Append(Enc(role))
            .Append("\" data-csrf=\"").Append(Enc(csrfToken)).Append("\">")
            .Append("<header><h1>Titanium Plus</h1><nav aria-label=\"Views\">");
        foreach (var (v, path, slug, title) in Navigation)
        {
            sb.Append("<a data-testid=\"nav-").Append(slug).Append("\" href=\"").Append(path).Append('"');
            if (v == view)
            {
                sb.Append(" aria-current=\"page\"");
            }

            sb.Append('>').Append(Enc(title)).Append("</a>");
        }

        sb.Append("</nav><span class=\"who\" data-testid=\"whoami\">").Append(Enc(role)).Append("</span>")
            .Append("<button type=\"button\" id=\"logout\" data-testid=\"btn-logout\">Sign out</button></header>")
            .Append("<p id=\"flash\" role=\"status\" data-testid=\"flash\"></p>")
            .Append("<main>").Append(body).Append("</main>")
            .Append("<script src=\"/assets/dashboard.js\"></script></body></html>");
        return sb.ToString();
    }

    public static string Overview(DashboardOverview o)
    {
        var sb = new StringBuilder("<section class=\"cards\" data-testid=\"overview\">");
        Card(sb, "rps", "Requests per second", Rate(o.RequestsPerSecond));
        Card(sb, "error-ratio", "Error ratio", Ratio(o.ErrorRatio));
        Card(sb, "p50", "p50 latency", Seconds(o.Latency.P50Seconds));
        Card(sb, "p95", "p95 latency", Seconds(o.Latency.P95Seconds));
        Card(sb, "p99", "p99 latency", Seconds(o.Latency.P99Seconds));
        Card(sb, "active-connections", "Active connections", o.ActiveConnections.ToString(CultureInfo.InvariantCulture));
        Card(sb, "active-requests", "Active requests", o.ActiveRequests.ToString(CultureInfo.InvariantCulture));
        Card(sb, "total-requests", "Requests since start", o.TotalRequests.ToString(CultureInfo.InvariantCulture));
        sb.Append("</section><p class=\"hint\">Rate averages the last ").Append(o.RateWindowSeconds)
            .Append(" s. Error ratio and percentiles cover the last ").Append(o.StatsWindowSeconds)
            .Append(" s. An error is a 5xx answer or no answer.</p>")
            .Append("<p><a data-testid=\"link-metrics\" href=\"/metrics\">Prometheus metrics</a> &middot; ")
            .Append("<a data-testid=\"link-snapshot\" href=\"/api/snapshot\">JSON snapshot</a> &middot; ")
            .Append("<a data-testid=\"link-audit\" href=\"/api/audit\">JSON audit</a></p>");
        return sb.ToString();
    }

    public static string Routes(RoutesPageData data, bool canOperate)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>Routes</h2><table data-testid=\"routes-table\"><thead><tr><th>Route</th><th>Match</th><th>Action</th>")
            .Append("<th>Cluster</th><th>Transforms</th><th>Requests</th><th>Errors</th><th>p50</th><th>p95</th><th>p99</th></tr></thead>")
            .Append("<tbody id=\"routes-body\">");
        if (data.Routes.Count == 0)
        {
            sb.Append("<tr><td colspan=\"10\">No routes configured.</td></tr>");
        }

        foreach (var r in data.Routes)
        {
            sb.Append("<tr data-testid=\"route-row-").Append(Enc(r.Id)).Append("\"><td>").Append(Enc(r.Id))
                .Append("</td><td>").Append(Enc(r.Match)).Append("</td><td>").Append(Enc(r.Action))
                .Append("</td><td>").Append(Enc(r.ClusterId)).Append("</td><td>").Append(Enc(string.Join("; ", r.Transforms)))
                .Append("</td><td>").Append(r.Requests).Append("</td><td>").Append(r.Errors)
                .Append("</td><td>").Append(Seconds(r.P50Seconds)).Append("</td><td>").Append(Seconds(r.P95Seconds))
                .Append("</td><td>").Append(Seconds(r.P99Seconds)).Append("</td></tr>");
        }

        sb.Append("</tbody></table><h2>Clusters</h2><table data-testid=\"clusters-table\"><thead><tr><th>Cluster</th>")
            .Append("<th>Balancing</th><th>Healthy</th><th>Active probe</th><th>Passive health</th><th>Upstream TLS</th></tr></thead>")
            .Append("<tbody id=\"clusters-body\">");
        if (data.Clusters.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\">No clusters configured.</td></tr>");
        }

        foreach (var c in data.Clusters)
        {
            sb.Append("<tr data-testid=\"cluster-row-").Append(Enc(c.Id)).Append("\"><td>").Append(Enc(c.Id))
                .Append("</td><td>").Append(Enc(c.Algorithm)).Append("</td><td>")
                .Append(c.Healthy).Append(" / ").Append(c.Destinations)
                .Append("</td><td>").Append(YesNo(c.ActiveHealth)).Append("</td><td>").Append(YesNo(c.PassiveHealth))
                .Append("</td><td>").Append(Enc(TlsSummary(c))).Append("</td></tr>");
        }

        sb.Append("</tbody></table><h2>Destinations</h2>")
            .Append("<table data-testid=\"destinations-table\"><thead><tr><th>Destination</th><th>Cluster</th><th>Address</th>")
            .Append("<th>State</th><th>Actions</th></tr></thead><tbody id=\"destinations-body\">");
        if (data.Destinations.Count == 0)
        {
            sb.Append("<tr><td colspan=\"5\">No destinations in snapshot.</td></tr>");
        }

        foreach (var d in data.Destinations)
        {
            var id = Enc(d.Id);
            sb.Append("<tr data-testid=\"dest-row-").Append(id).Append("\"><td>").Append(id)
                .Append("</td><td>").Append(Enc(d.ClusterId)).Append("</td><td>").Append(Enc(AddressOf(d)))
                .Append("</td><td data-testid=\"dest-state-").Append(id).Append("\">").Append(Enc(d.State))
                .Append("</td><td><button type=\"button\" data-act=\"drain\" data-id=\"").Append(id)
                .Append("\" data-testid=\"btn-drain-").Append(id).Append('"').Append(canOperate ? "" : " disabled")
                .Append(">Drain</button> <button type=\"button\" data-act=\"healthy\" data-id=\"").Append(id)
                .Append("\" data-testid=\"btn-healthy-").Append(id).Append('"').Append(canOperate ? "" : " disabled")
                .Append(">Healthy</button></td></tr>");
        }

        return sb.Append("</tbody></table>").ToString();
    }

    public static string Health(IReadOnlyList<HealthEventView> events)
    {
        var sb = new StringBuilder("<p class=\"hint\">The newest 200 ejections, recoveries, and failed probes, newest first.</p>")
            .Append("<table data-testid=\"health-table\"><thead><tr><th>Time (UTC)</th><th>Destination</th><th>Event</th>")
            .Append("<th>Source</th><th>Detail</th></tr></thead><tbody id=\"health-body\">");
        if (events.Count == 0)
        {
            sb.Append("<tr><td colspan=\"5\">No ejections or probe failures recorded.</td></tr>");
        }

        foreach (var e in events)
        {
            sb.Append("<tr><td>").Append(Enc(e.Time)).Append("</td><td>").Append(Enc(e.Destination))
                .Append("</td><td>").Append(Enc(e.Kind)).Append("</td><td>").Append(Enc(e.Source))
                .Append("</td><td>").Append(Enc(e.Detail)).Append("</td></tr>");
        }

        return sb.Append("</tbody></table>").ToString();
    }

    public static string Config(ConfigPageData c)
    {
        if (!c.Available)
        {
            return "<p data-testid=\"config-unavailable\">This host does not report config status.</p>";
        }

        var sb = new StringBuilder("<dl class=\"facts\" data-testid=\"config\">");
        Fact(sb, "schema-version", "Schema version", c.SchemaVersion);
        Fact(sb, "config-hash", "Config hash", c.ConfigHash);
        Fact(sb, "loaded", "Loaded (UTC)", c.Loaded);
        Fact(sb, "last-reload", "Last reload (UTC)", c.LastReload.Length == 0 ? "never" : c.LastReload);
        Fact(sb, "last-result", "Last reload result", c.LastResult.Length == 0 ? "-" : c.LastResult);
        Fact(sb, "last-error", "Last reload error", c.LastError.Length == 0 ? "-" : c.LastError);
        return sb.Append("</dl>").ToString();
    }

    public static string Certificates(IReadOnlyList<CertificateView> certs)
    {
        var sb = new StringBuilder("<table data-testid=\"cert-table\"><thead><tr><th>Name</th><th>Role</th><th>Subject</th>")
            .Append("<th>Issuer</th><th>Expires (UTC)</th><th>Days left</th><th>Status</th></tr></thead><tbody id=\"certs-body\">");
        if (certs.Count == 0)
        {
            sb.Append("<tr><td colspan=\"7\">No certificates to report.</td></tr>");
        }

        foreach (var c in certs)
        {
            sb.Append("<tr class=\"").Append(Enc(c.Status)).Append("\"><td>").Append(Enc(c.Name))
                .Append("</td><td>").Append(Enc(c.Role)).Append("</td><td>").Append(Enc(c.Subject))
                .Append("</td><td>").Append(Enc(c.Issuer)).Append("</td><td>").Append(Enc(c.NotAfter ?? "-"))
                .Append("</td><td>").Append(c.DaysLeft?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append("</td><td>").Append(Enc(c.Error.Length > 0 ? c.Status + ": " + c.Error : c.Status)).Append("</td></tr>");
        }

        return sb.Append("</tbody></table>").ToString();
    }

    public static string Audit(IReadOnlyList<AuditView> entries)
    {
        var sb = new StringBuilder("<p class=\"hint\">The newest events first. The full ring is at <a href=\"/api/audit\">/api/audit</a>.</p>")
            .Append("<table data-testid=\"audit-table\"><thead><tr><th>Time (UTC)</th><th>Actor</th><th>Remote</th>")
            .Append("<th>Action</th><th>Target</th><th>Result</th></tr></thead><tbody id=\"audit-body\">");
        if (entries.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\">No audit events yet.</td></tr>");
        }

        foreach (var e in entries)
        {
            sb.Append("<tr><td>").Append(Enc(e.Time)).Append("</td><td>").Append(Enc(e.Actor))
                .Append("</td><td>").Append(Enc(e.Remote)).Append("</td><td>").Append(Enc(e.Action))
                .Append("</td><td>").Append(Enc(e.Target)).Append("</td><td>").Append(Enc(e.Result)).Append("</td></tr>");
        }

        return sb.Append("</tbody></table>").ToString();
    }

    private static void Card(StringBuilder sb, string key, string label, string value) =>
        sb.Append("<div class=\"card\"><span class=\"label\">").Append(Enc(label))
            .Append("</span><span class=\"value\" data-field=\"").Append(key).Append("\" data-testid=\"ov-").Append(key)
            .Append("\">").Append(Enc(value)).Append("</span></div>");

    private static void Fact(StringBuilder sb, string key, string label, string value) =>
        sb.Append("<dt>").Append(Enc(label)).Append("</dt><dd data-field=\"").Append(key)
            .Append("\" data-testid=\"cfg-").Append(key).Append("\">").Append(Enc(value)).Append("</dd>");

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string AddressOf(DestinationView d) =>
        d.Address.Length == 0 ? "-" : $"{(d.Https ? "https" : "http")}://{d.Address}:{d.Port.ToString(CultureInfo.InvariantCulture)}";

    private static string TlsSummary(ClusterView c)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c.ServerName))
        {
            parts.Add("sni " + c.ServerName);
        }

        if (c.ClientCertificate)
        {
            parts.Add("client certificate");
        }

        if (c.CaBundle)
        {
            parts.Add("CA bundle");
        }

        if (c.AcceptAnyServerCertificate)
        {
            parts.Add("accepts any server certificate");
        }

        if (!c.VerifyHostname)
        {
            parts.Add("hostname not verified");
        }

        return parts.Count == 0 ? "default" : string.Join(", ", parts);
    }
}
