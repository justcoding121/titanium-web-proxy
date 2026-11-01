using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Titanium.Plus.Audit;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Plus.Security;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Dashboard;

/// <summary>Everything the dashboard shows beyond destination states. Every member is optional.</summary>
public sealed class DashboardOptions
{
    /// <summary>
    ///     Secret that signs a person in read-only (<c>dashboardViewerSecret</c>). It must differ from the shared secret,
    ///     which signs in an operator. Null or empty leaves the viewer role off.
    /// </summary>
    public string? ViewerSecret { get; init; }

    /// <summary>The live route list for the Routes view.</summary>
    public IEnumerable<RouteConfig>? Routes { get; init; }

    /// <summary>Config identity, reload status, and certificate inventory from the host.</summary>
    public IPlusHostStatus? HostStatus { get; init; }

    /// <summary>Rate, error ratio, and latency percentiles.</summary>
    public DashboardMetrics? Metrics { get; init; }

    /// <summary>Recent ejections and probe failures.</summary>
    public HealthEventLog? HealthEvents { get; init; }

    /// <summary>Clock for sessions and certificate expiry. Tests pass a manual one.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Overrides the idle timeout (default 30 minutes).</summary>
    public TimeSpan? SessionIdleTimeout { get; init; }

    /// <summary>Overrides the session cap (default 64).</summary>
    public int? MaxSessions { get; init; }

    /// <summary>
    ///     When true (default), the shared secret and viewer secret still sign people in (break-glass). When false,
    ///     only OIDC and local users may sign in through the form; a wrong secret is refused like any other bad login.
    /// </summary>
    public bool BreakGlass { get; init; } = true;

    /// <summary>Path to the local users file (<c>user:role:iterations:salt:hash</c>). Null leaves local login off.</summary>
    public string? UsersFile { get; init; }

    /// <summary>OIDC SSO for the dashboard. Null leaves the OIDC code path unconstructed.</summary>
    public DashboardOidcOptions? Oidc { get; init; }

    /// <summary>Host reload callback (same path as SIGHUP). Null hides the Config reload button.</summary>
    public Func<CancellationToken, Task<PlusReloadResult>>? RequestReload { get; init; }

    /// <summary>Route/cluster edit service from the CLI host. Null hides edit actions.</summary>
    public IConfigEditor? ConfigEditor { get; init; }

    /// <summary>When true with <see cref="ConfigEditor"/>, operators see route and cluster edit links.</summary>
    public bool DashboardEditing { get; init; }
}

/// <summary>
///     Authenticated HTML admin: overview, routes and clusters, health and ejections, config and reload, certificates,
///     and audit. A person signs in on <c>/login</c> and gets an <c>HttpOnly</c>, <c>SameSite=Strict</c> session cookie;
///     scripts keep using <c>X-Titanium-Control-Secret</c>. Changes need the operator role and, for cookie sessions, the
///     per-session CSRF token. It listens on the control plane's host (and TLS certificate, when set) and shares its
///     audit trail and failed-login throttle.
/// </summary>
public sealed class DashboardHost : IDisposable
{
    public const string SessionCookie = "titanium_plus_session";
    public const string CsrfHeader = "X-Titanium-Csrf";
    public const string PollHeader = "X-Titanium-Poll";
    public const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private const int MaxBindAttempts = 8;
    private const int AuditRowsOnPage = 50;
    private const int MaxLoginBodyBytes = 4096;
    private const string PlainTextUtf8 = "text/plain; charset=utf-8";
    private const string LoginPath = "/login";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly KeyValuePair<string, string>[] SecurityHeaders =
    [
        new("Content-Security-Policy", ContentSecurityPolicy),
        new("X-Content-Type-Options", "nosniff"),
        new("Cache-Control", "no-store"),
        new("Referrer-Policy", "no-referrer"),
        new("X-Frame-Options", "DENY"),
    ];

    private readonly ControlPlaneServer _controlPlane;
    private readonly DrainOperations _operations;
    private readonly PrometheusMetricsExporter _metrics;
    private readonly IClusterManager? _clusters;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _time;
    private readonly int? _requestedPort;
    private readonly LocalUsersStore? _localUsers;
    private readonly DashboardOidc? _oidc;
    private readonly AuthThrottle _userThrottle;
    private ControlHttpServer? _server;

    public DashboardHost(
        ControlPlaneServer controlPlane,
        DrainOperations operations,
        PrometheusMetricsExporter metrics,
        IClusterManager? clusters,
        int? dashboardPort = null,
        DashboardOptions? options = null)
    {
        _controlPlane = controlPlane;
        _operations = operations;
        _metrics = metrics;
        _clusters = clusters;
        _options = options ?? new DashboardOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
        Sessions = new DashboardSessionStore(
            _time,
            _options.SessionIdleTimeout,
            _options.MaxSessions ?? DashboardSessionStore.DefaultMaxSessions);
        _requestedPort = dashboardPort is > 0 and < 65536 ? dashboardPort : null;
        _userThrottle = new AuthThrottle(timeProvider: _time);
        if (!string.IsNullOrWhiteSpace(_options.UsersFile))
        {
            _localUsers = new LocalUsersStore(_options.UsersFile);
        }

        if (_options.Oidc is { } oidc)
        {
            _oidc = DashboardOidc.Create(oidc, _time);
        }
    }

    /// <summary>True when the OIDC provider was constructed (tests assert default-off).</summary>
    internal bool HasOidc => _oidc is not null;

    /// <summary>True when a local users file was configured.</summary>
    internal bool HasLocalUsers => _localUsers is not null;

    public string? Prefix { get; private set; }

    public int? BoundPort { get; private set; }

    /// <summary>The audit trail this dashboard shows (shared with the control plane).</summary>
    public AuditLog Audit => _controlPlane.Security.Audit;

    /// <summary>Signed-in browsers.</summary>
    public DashboardSessionStore Sessions { get; }

    private ControlPlaneSecurity Security => _controlPlane.Security;

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
        _oidc?.Dispose();
        _localUsers?.Dispose();
    }

    /// <summary>Handles one request. Internal so tests can call it without a socket.</summary>
    internal async Task<ControlHttpResponse> HandleAsync(ControlHttpRequest request, CancellationToken cancellationToken)
    {
        return Harden(await RouteAsync(request, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ControlHttpResponse> RouteAsync(ControlHttpRequest request, CancellationToken cancellationToken) // NOSONAR S3776 -- Dashboard routing keeps auth, OIDC, and static assets in one dispatch table.
    {
        var path = request.Path.Length > 1 ? request.Path.TrimEnd('/') : request.Path;
        var isGet = request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);
        var isPost = request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);

        if (isGet && path.Equals(DashboardAssets.ScriptPath, StringComparison.Ordinal))
        {
            return ControlHttpResponse.Text(200, "text/javascript; charset=utf-8", DashboardAssets.Script);
        }

        if (isGet && path.Equals(DashboardAssets.StylePath, StringComparison.Ordinal))
        {
            return ControlHttpResponse.Text(200, "text/css; charset=utf-8", DashboardAssets.Style);
        }

        var throttled = Security.CheckThrottle(request, ControlPlaneSecurity.DashboardSurface);
        if (throttled is not null)
        {
            return throttled;
        }

        if (_oidc is not null && isGet && path.Equals("/login/oidc", StringComparison.Ordinal))
        {
            return await _oidc.StartLoginAsync(request, _controlPlane.IsTls, cancellationToken).ConfigureAwait(false);
        }

        if (_oidc is not null && isGet && path.Equals("/login/oidc/callback", StringComparison.Ordinal))
        {
            return await CompleteOidcAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (path.Equals(LoginPath, StringComparison.Ordinal))
        {
            if (isPost)
            {
                return SignIn(request);
            }

            return isGet ? LoginPage(request) : MethodNotAllowed();
        }

        var isPoll = request.Header(PollHeader) is not null;
        var caller = Authenticate(request, isPoll, out var denied);
        if (denied is not null)
        {
            return denied;
        }

        if (caller is null)
        {
            return Anonymous(request, path, isGet);
        }

        var remote = request.RemoteAddress?.ToString();
        if (isPost)
        {
            return await PostAsync(request, path, caller, remote).ConfigureAwait(false);
        }

        if (!isGet)
        {
            return MethodNotAllowed();
        }

        return Get(path, caller, remote, isPoll);
    }

    /// <summary>
    ///     A browser opening a page goes to the login form. A call without credentials counts as a failed login (the same
    ///     rule as the control plane), except one that carries a session cookie: that is an expired session, not a
    ///     guess, and several open tabs must not lock their own user out when it lapses.
    /// </summary>
    private ControlHttpResponse Anonymous(ControlHttpRequest request, string path, bool isGet)
    {
        var isPage = isGet && !path.StartsWith("/api/", StringComparison.Ordinal) && !path.Equals("/metrics", StringComparison.Ordinal);
        if (isPage)
        {
            return Redirect(LoginPath);
        }

        return ReadCookie(request, SessionCookie) is not null
            ? ControlHttpResponse.Text(401, PlainTextUtf8, "unauthorized")
            : Security.RejectCredentials(request, ControlPlaneSecurity.DashboardSurface);
    }

    private ControlHttpResponse Get(string path, Caller caller, string? remote, bool isPoll)
    {
        if (path.Equals("/metrics", StringComparison.Ordinal))
        {
            return ControlHttpResponse.Text(200, "text/plain; version=0.0.4", _metrics.Render());
        }

        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return Api(path["/api/".Length..]);
        }

        if (path.StartsWith("/edit/", StringComparison.Ordinal))
        {
            return EditPage(path, caller);
        }

        var page = Array.Find(DashboardViews.Navigation, n => n.Path == path);
        if (page.Path is null)
        {
            return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
        }

        // A script that opens a page with the secret header is a login; a cookie session was audited at sign-in.
        if (!caller.ViaSession && !isPoll)
        {
            Audit.Record(caller.Actor, remote, AuditActions.Login, path, AuditResults.Ok);
        }

        return Html(200, DashboardViews.Page(
            page.View, RoleName(caller.Role), caller.Session?.CsrfToken ?? "", RenderBody(page.View, caller.Role)));
    }

    private string RenderBody(DashboardView view, DashboardRole role)
    {
        switch (view)
        {
            case DashboardView.Overview:
                return DashboardViews.Overview(Overview());
            case DashboardView.Routes:
                return (EditingEnabled && role == DashboardRole.Operator ? EditPanel() : "")
                    + DashboardViews.Routes(RoutesData(), role == DashboardRole.Operator);
            case DashboardView.Health:
                return DashboardViews.Health(DashboardData.Health(_options.HealthEvents));
            case DashboardView.Config:
                return DashboardViews.Config(
                    DashboardData.Config(_options.HostStatus),
                    canOperate: role == DashboardRole.Operator && _options.RequestReload is not null);
            case DashboardView.Certificates:
                return DashboardViews.Certificates(CertificateData());
            default:
                return DashboardViews.Audit(DashboardData.Audit(Audit.Recent(AuditRowsOnPage)));
        }
    }

    private ControlHttpResponse Api(string name) => name switch
    {
        "overview" => JsonResponse(Overview()),
        "routes" => JsonResponse(RoutesData()),
        "health" => JsonResponse(DashboardData.Health(_options.HealthEvents)),
        "config" => JsonResponse(DashboardData.Config(_options.HostStatus)),
        "certs" => JsonResponse(CertificateData()),
        "audit" => JsonResponse(Audit.Recent().Select(e => new
        {
            time = DashboardData.Iso(e.Time),
            e.Actor,
            e.Remote,
            e.Action,
            e.Target,
            e.Result,
        }).ToList()),
        "snapshot" => SnapshotResponse(),
        _ => ControlHttpResponse.Text(404, PlainTextUtf8, "not found"),
    };

    private DashboardOverview Overview() => _options.Metrics?.GetOverview() ?? new DashboardOverview
    {
        RateWindowSeconds = DashboardMetrics.RateWindowSeconds,
        StatsWindowSeconds = DashboardMetrics.WindowSeconds,
    };

    private RoutesPageData RoutesData() => DashboardData.Routes(_clusters, CopyRoutes(), _options.Metrics);

    /// <summary>The route list is replaced in place by a reload or a snapshot PUT, so read a copy and retry if it changes mid-copy.</summary>
    private RouteConfig[] CopyRoutes()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return _options.Routes?.ToArray() ?? [];
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Modified while copying; try again.
            }
        }

        return [];
    }

    private IReadOnlyList<CertificateView> CertificateData()
    {
        var own = new List<PlusCertificateInfo>();
        if (_controlPlane.Certificate is { } certificate)
        {
            own.Add(new PlusCertificateInfo
            {
                Name = "control plane and dashboard TLS",
                Role = "listener",
                Subject = certificate.Subject,
                Issuer = certificate.Issuer,
                NotAfterUtc = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            });
        }

        return DashboardData.Certificates(_options.HostStatus, own, _time.GetUtcNow());
    }

    private ControlHttpResponse SnapshotResponse()
    {
        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        return JsonResponse(new
        {
            destinationStates = snap.DestinationStates,
            clusters = snap.Clusters.Keys,
        });
    }

    private async Task<ControlHttpResponse> PostAsync(ControlHttpRequest request, string path, Caller caller, string? remote) // NOSONAR S3776 -- POST actions share CSRF, role checks, and audit in one path.
    {
        if (path.StartsWith("/edit/", StringComparison.Ordinal) || path.Equals("/clear-overlay", StringComparison.Ordinal)
            || path.StartsWith("/clear-overlay/", StringComparison.Ordinal))
        {
            return await EditPostAsync(request, path, caller, remote).ConfigureAwait(false);
        }

        // A trailing slash was trimmed from the path, so "/drain/" arrives as "/drain": a blank id, answered 400.
        var isDrain = path.StartsWith("/drain/", StringComparison.Ordinal) || path.Equals("/drain", StringComparison.Ordinal);
        var isHealthy = path.StartsWith("/healthy/", StringComparison.Ordinal) || path.Equals("/healthy", StringComparison.Ordinal);
        var isReload = path.StartsWith("/reload/", StringComparison.Ordinal) || path.Equals("/reload", StringComparison.Ordinal);
        var isLogout = path.Equals("/logout", StringComparison.Ordinal);
        if (!isDrain && !isHealthy && !isReload && !isLogout)
        {
            return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
        }

        string action;
        int prefixLength;
        if (isLogout)
        {
            action = AuditActions.Logout;
            prefixLength = 0;
        }
        else if (isReload)
        {
            action = AuditActions.Reload;
            prefixLength = "/reload/".Length;
        }
        else if (isDrain)
        {
            action = AuditActions.Drain;
            prefixLength = "/drain/".Length;
        }
        else
        {
            action = AuditActions.Healthy;
            prefixLength = "/healthy/".Length;
        }

        string target;
        if (isLogout || isReload)
        {
            target = "-";
        }
        else if (path.Length > prefixLength)
        {
            target = Uri.UnescapeDataString(path[prefixLength..]);
        }
        else
        {
            target = "";
        }

        // A cookie rides along on a forged cross-site request; a header secret does not, so only sessions need the token.
        if (caller.Session is { } session && !DashboardSessionStore.CsrfMatches(session, request.Header(CsrfHeader)))
        {
            Audit.Record(caller.Actor, remote, action, target, AuditResults.Denied);
            return ControlHttpResponse.Text(403, PlainTextUtf8, "csrf token missing or wrong");
        }

        if (isLogout)
        {
            Sessions.Remove(ReadCookie(request, SessionCookie));
            Audit.Record(caller.Actor, remote, AuditActions.Logout, "-", AuditResults.Ok);
            return ControlHttpResponse.Text(200, PlainTextUtf8, "ok", ClearCookie());
        }

        if (caller.Role != DashboardRole.Operator)
        {
            Audit.Record(caller.Actor, remote, action, target, AuditResults.Denied);
            return ControlHttpResponse.Text(403, PlainTextUtf8, "operator role required");
        }

        if (isReload)
        {
            return await ReloadAsync(caller, remote).ConfigureAwait(false);
        }

        try
        {
            if (isDrain)
            {
                _operations.Drain(target);
            }
            else
            {
                _operations.MarkHealthy(target);
            }
        }
        catch (ArgumentException)
        {
            Audit.Record(caller.Actor, remote, action, target, AuditResults.Rejected);
            return ControlHttpResponse.Text(400, PlainTextUtf8, "bad request");
        }

        Audit.Record(caller.Actor, remote, action, target, AuditResults.Ok);
        return ControlHttpResponse.Text(200, PlainTextUtf8, "ok");
    }

    private async Task<ControlHttpResponse> ReloadAsync(Caller caller, string? remote)
    {
        if (_options.RequestReload is null)
        {
            Audit.Record(caller.Actor, remote, AuditActions.Reload, "-", AuditResults.Unavailable);
            return ControlHttpResponse.Text(503, PlainTextUtf8, "reload is not available");
        }

        PlusReloadResult result;
        try
        {
            result = await _options.RequestReload(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Audit.Record(caller.Actor, remote, AuditActions.Reload, "-", AuditResults.Error);
            return ControlHttpResponse.Text(500, PlainTextUtf8, "reload failed");
        }

        if (!result.Ok)
        {
            Audit.Record(caller.Actor, remote, AuditActions.Reload, "-", AuditResults.Error);
            return ControlHttpResponse.Text(500, PlainTextUtf8,
                string.IsNullOrEmpty(result.Error) ? "reload failed" : result.Error);
        }

        Audit.Record(caller.Actor, remote, AuditActions.Reload, "-", AuditResults.Ok);
        return ControlHttpResponse.Text(200, PlainTextUtf8,
            string.IsNullOrEmpty(result.ConfigHash) ? "ok" : "ok " + result.ConfigHash);
    }

    /// <summary>Route and cluster editing is on when the host supplied an editor and the operator turned it on.</summary>
    private bool EditingEnabled => _options.DashboardEditing && _options.ConfigEditor is not null;

    private IReadOnlyList<ClusterConfig> CurrentClusters() =>
        (_clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty).Clusters.Values.ToList();

    private string EditPanel()
    {
        var status = _options.ConfigEditor!.GetOverlayStatus();
        return DashboardViews.EditPanel(
            CopyRoutes().Select(r => r.Id).Order(StringComparer.Ordinal).ToList(),
            CurrentClusters().Select(c => c.Id).Order(StringComparer.Ordinal).ToList(),
            new OverlayStatusView(
                status.HasOverlay,
                status.Suspended,
                status.SuspendedReasons ?? [],
                status.EditCount,
                status.LastEditUtc?.UtcDateTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                status.Orphans ?? []));
    }

    /// <summary><c>GET /edit/route[/id]</c> and <c>/edit/cluster[/id]</c>: the editor page. Operators only.</summary>
    private ControlHttpResponse EditPage(string path, Caller caller)
    {
        if (!EditingEnabled)
        {
            return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
        }

        if (caller.Role != DashboardRole.Operator)
        {
            return ControlHttpResponse.Text(403, PlainTextUtf8, "operator role required");
        }

        var parts = path["/edit/".Length..].Split('/', 2);
        var kind = parts[0];
        var id = parts.Length > 1 && parts[1].Length > 0 ? Uri.UnescapeDataString(parts[1]) : null;
        if (!DashboardEditing.IsKind(kind))
        {
            return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
        }

        string json;
        if (id is null)
        {
            json = DashboardEditing.Template(kind);
        }
        else if (kind == DashboardEditing.RouteKind)
        {
            var route = CopyRoutes().FirstOrDefault(r => r.Id == id);
            if (route is null)
            {
                return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
            }

            json = DashboardEditing.ToEditableJson(route);
        }
        else
        {
            var cluster = CurrentClusters().FirstOrDefault(c => c.Id == id);
            if (cluster is null)
            {
                return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
            }

            json = DashboardEditing.ToEditableJson(cluster);
        }

        return Html(200, DashboardViews.Page(
            DashboardView.Edit, RoleName(caller.Role), caller.Session?.CsrfToken ?? "", DashboardViews.EditForm(kind, id, json)));
    }

    /// <summary>
    ///     <c>POST /edit/{route|cluster}/{validate|save|delete}[/id]</c> with the item as the JSON body, and
    ///     <c>POST /clear-overlay</c>. Same CSRF and operator checks as the other actions; saves and deletes are audited.
    /// </summary>
    private async Task<ControlHttpResponse> EditPostAsync(ControlHttpRequest request, string path, Caller caller, string? remote)
    {
        var isClear = path.StartsWith("/clear-overlay", StringComparison.Ordinal);
        var action = isClear ? AuditActions.OverlayClear : AuditActions.ConfigEdit;
        if (!EditingEnabled)
        {
            return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
        }

        var parts = isClear ? [] : path["/edit/".Length..].Split('/', 3);
        var target = isClear ? "-" : string.Join('/', parts.Where(p => p.Length > 0).Select(Uri.UnescapeDataString));
        if (caller.Session is { } session && !DashboardSessionStore.CsrfMatches(session, request.Header(CsrfHeader)))
        {
            Audit.Record(caller.Actor, remote, action, target, AuditResults.Denied);
            return ControlHttpResponse.Text(403, PlainTextUtf8, "csrf token missing or wrong");
        }

        if (caller.Role != DashboardRole.Operator)
        {
            Audit.Record(caller.Actor, remote, action, target, AuditResults.Denied);
            return ControlHttpResponse.Text(403, PlainTextUtf8, "operator role required");
        }

        var editor = _options.ConfigEditor!;
        try
        {
            if (isClear)
            {
                var cleared = await DashboardEditing.ClearOverlayAsync(editor, caller.Actor, CancellationToken.None).ConfigureAwait(false);
                Audit.Record(caller.Actor, remote, action, target, cleared.Status == 200 ? AuditResults.Ok : AuditResults.Rejected);
                return ControlHttpResponse.Text(cleared.Status, "application/json", cleared.Json);
            }

            if (parts.Length < 2 || !DashboardEditing.IsKind(parts[0])
                || !Enum.TryParse<DashboardEditAction>(parts[1], ignoreCase: true, out var editAction))
            {
                return ControlHttpResponse.Text(404, PlainTextUtf8, "not found");
            }

            if (request.Body.Length > DashboardEditing.MaxBodyBytes)
            {
                return ControlHttpResponse.Text(413, PlainTextUtf8, "request too large");
            }

            var existingId = parts.Length > 2 && parts[2].Length > 0 ? Uri.UnescapeDataString(parts[2]) : null;
            var outcome = await DashboardEditing.RunAsync(
                editor, parts[0], editAction, existingId, request.BodyText(), CopyRoutes(), CurrentClusters(),
                caller.Actor, CancellationToken.None).ConfigureAwait(false);
            if (editAction != DashboardEditAction.Validate)
            {
                Audit.Record(caller.Actor, remote, action, target, outcome.Status == 200 ? AuditResults.Ok : AuditResults.Rejected);
            }

            return ControlHttpResponse.Text(outcome.Status, "application/json", outcome.Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Audit.Record(caller.Actor, remote, action, target, AuditResults.Error);
            return ControlHttpResponse.Text(500, PlainTextUtf8, "edit failed");
        }
    }

    private ControlHttpResponse LoginPage(ControlHttpRequest request)
    {
        var caller = Authenticate(request, isPoll: true, out var denied);
        if (denied is not null)
        {
            return denied;
        }

        return caller is null ? Html(200, DashboardViews.Login(null, LoginFeatures())) : Redirect("/");
    }

    private async Task<ControlHttpResponse> CompleteOidcAsync(ControlHttpRequest request, CancellationToken cancellationToken)
    {
        var remote = request.RemoteAddress?.ToString();
        var (response, identity, role) = await _oidc!.CompleteCallbackAsync(request, _controlPlane.IsTls, cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            if (response.Status is 401 or 403)
            {
                Security.Throttle.RecordFailure(remote ?? "unknown");
                Audit.Record($"{ControlPlaneSecurity.DashboardSurface}:oidc", remote, AuditActions.LoginFailed,
                    "/login/oidc/callback", AuditResults.Denied);
            }

            return response;
        }

        var label = identity.Email is { Length: > 0 } email ? email : identity.Subject!;
        var actor = $"{ControlPlaneSecurity.DashboardSurface}:oidc:{label}:{RoleName(role)}";
        var id = Sessions.Create(role, actor, DashboardAuthSource.Oidc, out _);
        Audit.Record(actor, remote, AuditActions.Login, "/login/oidc/callback", AuditResults.Ok);
        var headers = new List<KeyValuePair<string, string>>(response.Headers) { SessionCookieHeader(id), new("Location", "/") };
        return ControlHttpResponse.Text(303, PlainTextUtf8, "", headers.ToArray());
    }

    private ControlHttpResponse SignIn(ControlHttpRequest request)
    {
        var remote = request.RemoteAddress?.ToString() ?? "unknown";
        if (request.Body.Length > MaxLoginBodyBytes)
        {
            return ControlHttpResponse.Text(413, PlainTextUtf8, "request too large");
        }

        var body = request.BodyText();
        var user = FormValue(body, "user");
        var password = FormValue(body, "password");
        var presented = FormValue(body, "secret");

        if (_localUsers is not null && !string.IsNullOrEmpty(user))
        {
            return SignInLocal(request, remote, user, password);
        }

        if (!string.IsNullOrEmpty(presented))
        {
            return SignInBreakGlass(request, remote, presented);
        }

        return LoginRejected(request, "Sign-in failed.");
    }

    private ControlHttpResponse SignInLocal(ControlHttpRequest request, string remote, string user, string? password)
    {
        var userKey = "user:" + user;
        if (_userThrottle.IsBlocked(userKey, out var retryAfter, out var firstUser))
        {
            if (firstUser)
            {
                Audit.Record($"{ControlPlaneSecurity.DashboardSurface}:local:{user}", remote, AuditActions.LoginFailed,
                    LoginPath, AuditResults.Throttled);
            }

            return ControlHttpResponse.Text(429, PlainTextUtf8, "too many failed attempts",
                new KeyValuePair<string, string>("Retry-After", retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (_localUsers!.TryAuthenticate(user, password, out var record) && record is not null)
        {
            var actor = $"{ControlPlaneSecurity.DashboardSurface}:local:{record.User}:{RoleName(record.Role)}";
            var id = Sessions.Create(record.Role, actor, DashboardAuthSource.Local, out _);
            Audit.Record(actor, remote, AuditActions.Login, LoginPath, AuditResults.Ok);
            return Redirect("/", SessionCookieHeader(id));
        }

        _userThrottle.RecordFailure(userKey);
        return LoginRejected(request, "Wrong username or password.");
    }

    private ControlHttpResponse SignInBreakGlass(ControlHttpRequest request, string remote, string presented)
    {
        if (!_options.BreakGlass)
        {
            return LoginRejected(request, "Break-glass sign-in is disabled.");
        }

        DashboardRole? role = null;
        if (ConstantTime.StringEquals(presented, _controlPlane.SharedSecret))
        {
            role = DashboardRole.Operator;
        }
        else if (HasViewerSecret && ConstantTime.StringEquals(presented, _options.ViewerSecret))
        {
            role = DashboardRole.Viewer;
        }

        if (role is not { } granted)
        {
            return LoginRejected(request, "Wrong secret.");
        }

        var actor = $"{ControlPlaneSecurity.DashboardSurface}:break-glass:{RoleName(granted)}";
        var id = Sessions.Create(granted, actor, DashboardAuthSource.Secret, out _);
        Audit.Record(actor, remote, AuditActions.Login, LoginPath, AuditResults.Ok);
        return Redirect("/", SessionCookieHeader(id));
    }

    private ControlHttpResponse LoginRejected(ControlHttpRequest request, string message)
    {
        var rejected = Security.RejectCredentials(request, ControlPlaneSecurity.DashboardSurface);
        return new ControlHttpResponse
        {
            Status = rejected.Status,
            ContentType = "text/html; charset=utf-8",
            Body = System.Text.Encoding.UTF8.GetBytes(DashboardViews.Login(message, LoginFeatures())),
            Headers = rejected.Headers,
        };
    }

    private DashboardLoginFeatures LoginFeatures() => new(
        ShowSecret: _options.BreakGlass,
        ShowLocalUsers: _localUsers is not null,
        ShowOidc: _oidc is not null);

    private bool HasViewerSecret => !string.IsNullOrEmpty(_options.ViewerSecret);

    /// <summary>The caller, or null when anonymous. <paramref name="denied"/> is set for a wrong header secret.</summary>
    private Caller? Authenticate(ControlHttpRequest request, bool isPoll, out ControlHttpResponse? denied)
    {
        denied = null;
        var header = request.Header(ControlPlaneServer.SharedSecretHeader);
        if (!string.IsNullOrEmpty(header))
        {
            if (ConstantTime.StringEquals(header, _controlPlane.SharedSecret))
            {
                return new Caller(
                    DashboardRole.Operator,
                    $"{ControlPlaneSecurity.DashboardSurface}:shared-secret",
                    null);
            }

            if (HasViewerSecret && ConstantTime.StringEquals(header, _options.ViewerSecret))
            {
                return new Caller(
                    DashboardRole.Viewer,
                    $"{ControlPlaneSecurity.DashboardSurface}:viewer-secret",
                    null);
            }

            denied = Security.RejectCredentials(request, ControlPlaneSecurity.DashboardSurface);
            return null;
        }

        var session = Sessions.Find(ReadCookie(request, SessionCookie), touch: !isPoll);
        return session is null ? null : new Caller(session.Role, session.Actor, session);
    }

    private KeyValuePair<string, string> SessionCookieHeader(string id)
    {
        var maxAge = (int)Sessions.IdleTimeout.TotalSeconds;
        var secure = _controlPlane.IsTls ? "; Secure" : "";
        return new("Set-Cookie", $"{SessionCookie}={id}; Path=/; HttpOnly; SameSite=Strict; Max-Age={maxAge}{secure}");
    }

    private KeyValuePair<string, string> ClearCookie() =>
        new("Set-Cookie", $"{SessionCookie}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0{(_controlPlane.IsTls ? "; Secure" : "")}");

    private static string? ReadCookie(ControlHttpRequest request, string name)
    {
        var header = request.Header("Cookie");
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part.AsSpan(0, eq).Equals(name, StringComparison.Ordinal))
            {
                return part[(eq + 1)..];
            }
        }

        return null;
    }

    private static string? FormValue(string body, string name)
    {
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    private static string RoleName(DashboardRole role) => role == DashboardRole.Operator ? "operator" : "viewer";

    private static ControlHttpResponse Html(int status, string html) =>
        ControlHttpResponse.Text(status, "text/html; charset=utf-8", html);

    private static ControlHttpResponse JsonResponse(object value) =>
        ControlHttpResponse.Text(200, "application/json", JsonSerializer.Serialize(value, Json));

    private static ControlHttpResponse Redirect(string location, params KeyValuePair<string, string>[] extra) =>
        ControlHttpResponse.Text(303, PlainTextUtf8, "", [new("Location", location), .. extra]);

    private static ControlHttpResponse MethodNotAllowed() =>
        ControlHttpResponse.Text(405, PlainTextUtf8, "method not allowed");

    /// <summary>Adds the response headers every dashboard answer carries, including errors and redirects.</summary>
    private static ControlHttpResponse Harden(ControlHttpResponse response)
    {
        var headers = new List<KeyValuePair<string, string>>(response.Headers.Count + SecurityHeaders.Length);
        headers.AddRange(response.Headers);
        foreach (var header in SecurityHeaders.Where(h => !headers.Exists(existing =>
                     existing.Key.Equals(h.Key, StringComparison.OrdinalIgnoreCase))))
        {
            headers.Add(header);
        }

        return new ControlHttpResponse
        {
            Status = response.Status,
            ContentType = response.ContentType,
            Body = response.Body,
            Headers = headers,
        };
    }

    private sealed record Caller(DashboardRole Role, string Actor, DashboardSession? Session)
    {
        public bool ViaSession => Session is not null;
    }
}
