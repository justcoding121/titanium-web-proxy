using System.Net;
using System.Text;
using System.Text.Json;
using Titanium.Manager.Api;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;

namespace Titanium.Manager.Ui;

/// <summary>
///     Server-rendered HTML admin for fleets, drafts, rollouts, and audit. Sign-in uses the bootstrap admin
///     token; the browser gets an HttpOnly SameSite=Strict session cookie and a per-session CSRF token.
/// </summary>
internal sealed class ManagerUiHost
{
    public const string SessionCookie = "titanium_manager_session";
    public const string CsrfHeader = "X-Titanium-Csrf";
    public const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private const int MaxLoginBodyBytes = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ManagerStore _store;
    private readonly ConfigWaitHub _waitHub;

    public ManagerUiHost(ManagerStore store, ConfigWaitHub waitHub, TimeProvider? timeProvider = null)
    {
        _store = store;
        _waitHub = waitHub;
        Sessions = new ManagerSessionStore(timeProvider);
    }

    public ManagerSessionStore Sessions { get; }

    /// <summary>
    ///     Returns true when the request was handled as a UI/asset/login route (including redirects).
    ///     API routes under <c>/v1/</c> are left to the caller.
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpListenerContext context, string method, string path)
    {
        if (method == "GET" && path.Equals(ManagerUiAssets.ScriptPath, StringComparison.Ordinal))
        {
            await WriteTextAsync(context, 200, ManagerUiAssets.Script, "text/javascript; charset=utf-8")
                .ConfigureAwait(false);
            return true;
        }

        if (method == "GET" && path.Equals(ManagerUiAssets.StylePath, StringComparison.Ordinal))
        {
            await WriteTextAsync(context, 200, ManagerUiAssets.Style, "text/css; charset=utf-8")
                .ConfigureAwait(false);
            return true;
        }

        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "GET")
            {
                var existing = FindSession(context.Request, touch: true);
                if (existing is not null)
                {
                    await RedirectAsync(context, "/").ConfigureAwait(false);
                    return true;
                }

                await WriteHtmlAsync(context, 200, ManagerUiViews.Login(null)).ConfigureAwait(false);
                return true;
            }

            if (method == "POST")
            {
                await SignInAsync(context).ConfigureAwait(false);
                return true;
            }

            await WriteTextAsync(context, 405, "method not allowed").ConfigureAwait(false);
            return true;
        }

        if (method == "POST" && path.Equals("/logout", StringComparison.OrdinalIgnoreCase))
        {
            var session = FindSession(context.Request, touch: false);
            if (session is not null && !RequireCsrf(context, session))
            {
                return true;
            }

            Sessions.Remove(ReadCookie(context.Request, SessionCookie));
            if (session is not null)
            {
                _store.Audit(session.Actor, "ui.logout", "-", "ok");
            }

            await WriteTextAsync(context, 200, "ok", extraHeaders: ClearCookie()).ConfigureAwait(false);
            return true;
        }

        // UI JSON helpers (session + CSRF)
        if (path.StartsWith("/ui/", StringComparison.OrdinalIgnoreCase))
        {
            var session = FindSession(context.Request, touch: true);
            if (session is null)
            {
                await WriteTextAsync(context, 401, "unauthorized").ConfigureAwait(false);
                return true;
            }

            if (method != "GET" && !RequireCsrf(context, session))
            {
                return true;
            }

            await HandleUiApiAsync(context, method, path, session).ConfigureAwait(false);
            return true;
        }

        // HTML pages
        if (IsHtmlPage(path) && (method == "GET" || method == "HEAD"))
        {
            var session = FindSession(context.Request, touch: true);
            if (session is null)
            {
                await RedirectAsync(context, "/login").ConfigureAwait(false);
                return true;
            }

            await HandlePageAsync(context, path, session).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Session from cookie, or null.</summary>
    public ManagerSession? FindSession(HttpListenerRequest request, bool touch) =>
        Sessions.Find(ReadCookie(request, SessionCookie), touch);

    public static bool IsHtmlPage(string path)
    {
        if (path is "/" or "")
        {
            return true;
        }

        if (path.Equals("/audit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith("/fleets", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // /fleets and /fleets/{id}/... pages — not /v1
        return true;
    }

    private async Task HandleUiApiAsync(
        HttpListenerContext context,
        string method,
        string path,
        ManagerSession session)
    {
        if (method == "POST" && path.Equals("/ui/fleets", StringComparison.OrdinalIgnoreCase))
        {
            if (session.Role < AdminRole.Editor)
            {
                await WriteTextAsync(context, 403, "forbidden").ConfigureAwait(false);
                return;
            }

            using var doc = await ReadJsonAsync(context).ConfigureAwait(false);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteJsonAsync(context, 400, new { error = "name required" }).ConfigureAwait(false);
                return;
            }

            var fleet = _store.CreateFleet(name!);
            _store.Audit(session.Actor, "fleet.create", $"fleet:{fleet.Id}", "ok", fleet.Name);
            await WriteJsonAsync(context, 201, new { id = fleet.Id, name = fleet.Name }).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path.Equals("/ui/draft/patch", StringComparison.OrdinalIgnoreCase))
        {
            if (session.Role < AdminRole.Editor)
            {
                await WriteTextAsync(context, 403, "forbidden").ConfigureAwait(false);
                return;
            }

            using var doc = await ReadJsonAsync(context).ConfigureAwait(false);
            var root = doc.RootElement;
            var yaml = root.TryGetProperty("yaml", out var y) ? y.GetString() ?? "" : "";
            var op = root.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
            var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            try
            {
                var next = op.ToLowerInvariant() switch
                {
                    "upsert-route" => ManagerUiYamlDraft.UpsertRoute(
                        yaml,
                        id ?? "",
                        root.TryGetProperty("path", out var p) ? p.GetString() : null,
                        root.TryGetProperty("clusterId", out var c) ? c.GetString() : null),
                    "remove-route" => ManagerUiYamlDraft.RemoveRoute(yaml, id ?? ""),
                    "upsert-cluster" => ManagerUiYamlDraft.UpsertCluster(yaml, id ?? ""),
                    "remove-cluster" => ManagerUiYamlDraft.RemoveCluster(yaml, id ?? ""),
                    _ => throw new InvalidOperationException("unknown op"),
                };
                var summary = ManagerUiYamlDraft.Summarize(next);
                await WriteJsonAsync(context, 200, new
                {
                    yaml = next,
                    summary = new
                    {
                        routes = summary.Routes.Select(r => new { id = r.Id, path = r.Path, clusterId = r.ClusterId }),
                        clusters = summary.Clusters.Select(c => new { id = c.Id, destinationCount = c.DestinationCount }),
                    },
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(context, 400, new { error = ex.Message }).ConfigureAwait(false);
            }

            return;
        }

        if (method == "POST" &&
            path.StartsWith("/ui/fleets/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/diff-draft", StringComparison.OrdinalIgnoreCase))
        {
            var mid = path["/ui/fleets/".Length..];
            mid = mid[..^"/diff-draft".Length].Trim('/');
            if (!long.TryParse(mid, out var fleetId) || _store.GetFleet(fleetId) is null)
            {
                await WriteTextAsync(context, 404, "fleet not found").ConfigureAwait(false);
                return;
            }

            using var doc = await ReadJsonAsync(context).ConfigureAwait(false);
            var yaml = doc.RootElement.TryGetProperty("yaml", out var y) ? y.GetString() ?? "" : "";
            var published = _store.GetPublished(fleetId);
            var from = published?.YamlText ?? "";
            var unified = YamlDiff.Unified(from, yaml, "published", "draft");
            await WriteJsonAsync(context, 200, new { unified }).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private async Task HandlePageAsync(HttpListenerContext context, string path, ManagerSession session)
    {
        var role = ManagerUiViews.RoleName(session.Role);
        var canEdit = session.Role >= AdminRole.Editor;
        var csrf = session.CsrfToken;

        if (path is "/" or "/fleets")
        {
            var html = ManagerUiViews.Page(
                ManagerUiView.Fleets,
                role,
                csrf,
                "Fleets",
                ManagerUiViews.Fleets(_store.ListFleets(), canEdit));
            await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
            return;
        }

        if (path.Equals("/audit", StringComparison.OrdinalIgnoreCase))
        {
            var html = ManagerUiViews.Page(
                ManagerUiView.Audit,
                role,
                csrf,
                "Audit",
                ManagerUiViews.Audit(_store.ListAudit()));
            await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
            return;
        }

        if (!path.StartsWith("/fleets/", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
            return;
        }

        var rest = path["/fleets/".Length..];
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !long.TryParse(segments[0], out var fleetId))
        {
            await WriteTextAsync(context, 400, "invalid fleet id").ConfigureAwait(false);
            return;
        }

        var fleet = _store.GetFleet(fleetId);
        if (fleet is null)
        {
            await WriteTextAsync(context, 404, "fleet not found").ConfigureAwait(false);
            return;
        }

        if (segments.Length == 1)
        {
            var html = ManagerUiViews.Page(
                ManagerUiView.Fleet,
                role,
                csrf,
                fleet.Name,
                ManagerUiViews.FleetDetail(fleet, _store.GetPublished(fleetId), _store.ListAgents(fleetId)),
                fleetId);
            await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
            return;
        }

        var action = segments[1];
        if (action.Equals("revisions", StringComparison.OrdinalIgnoreCase) && segments.Length == 2)
        {
            var html = ManagerUiViews.Page(
                ManagerUiView.Revisions,
                role,
                csrf,
                "Revisions",
                ManagerUiViews.Revisions(fleetId, _store.ListRevisions(fleetId)),
                fleetId);
            await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
            return;
        }

        if (action.Equals("diff", StringComparison.OrdinalIgnoreCase) && segments.Length == 2)
        {
            var qs = context.Request.QueryString;
            if (!long.TryParse(qs["from"], out var fromId) || !long.TryParse(qs["to"], out var toId))
            {
                await WriteTextAsync(context, 400, "from and to revision ids required").ConfigureAwait(false);
                return;
            }

            var from = _store.GetRevision(fleetId, fromId);
            var to = _store.GetRevision(fleetId, toId);
            if (from is null || to is null)
            {
                await WriteTextAsync(context, 404, "revision not found").ConfigureAwait(false);
                return;
            }

            var unified = YamlDiff.Unified(from.YamlText, to.YamlText, $"rev-{fromId}", $"rev-{toId}");
            var html = ManagerUiViews.Page(
                ManagerUiView.Diff,
                role,
                csrf,
                "Diff",
                ManagerUiViews.Diff(fleetId, fromId, toId, unified),
                fleetId);
            await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
            return;
        }

        if (action.Equals("editor", StringComparison.OrdinalIgnoreCase) && segments.Length == 2)
        {
            var published = _store.GetPublished(fleetId);
            var revisions = _store.ListRevisions(fleetId);
            var yaml = published?.YamlText
                       ?? revisions.FirstOrDefault()?.YamlText
                       ?? ManagerUiYamlDraft.EmptyDraft;
            DraftSummary summary;
            try
            {
                summary = ManagerUiYamlDraft.Summarize(yaml);
            }
            catch
            {
                summary = new DraftSummary([], []);
            }

            var html = ManagerUiViews.Page(
                ManagerUiView.Editor,
                role,
                csrf,
                "Editor",
                ManagerUiViews.Editor(fleetId, yaml, summary, canEdit),
                fleetId);
            await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
            return;
        }

        if (action.Equals("rollouts", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 2)
            {
                var html = ManagerUiViews.Page(
                    ManagerUiView.Rollouts,
                    role,
                    csrf,
                    "Rollouts",
                    ManagerUiViews.Rollouts(fleetId, _store.ListRollouts(fleetId)),
                    fleetId);
                await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 3 && long.TryParse(segments[2], out var rolloutId))
            {
                var rollout = _store.GetRollout(fleetId, rolloutId);
                if (rollout is null)
                {
                    await WriteTextAsync(context, 404, "rollout not found").ConfigureAwait(false);
                    return;
                }

                var html = ManagerUiViews.Page(
                    ManagerUiView.Rollout,
                    role,
                    csrf,
                    "Rollout",
                    ManagerUiViews.RolloutDetail(fleetId, rollout, _store.ListRolloutSteps(rolloutId), canEdit),
                    fleetId);
                await WriteHtmlAsync(context, 200, html).ConfigureAwait(false);
                return;
            }
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private async Task SignInAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > MaxLoginBodyBytes)
        {
            await WriteTextAsync(context, 413, "request too large").ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        var token = FormValue(body, "token") ?? FormValue(body, "secret");
        if (string.IsNullOrWhiteSpace(token))
        {
            await WriteHtmlAsync(context, 401, ManagerUiViews.Login("Admin token required.")).ConfigureAwait(false);
            return;
        }

        var admin = _store.FindAdminByToken(token);
        if (admin is null || !AdminRoleParser.TryParse(admin.Role, out var role))
        {
            _store.Audit("ui:anonymous", "ui.login", "/login", "denied");
            await WriteHtmlAsync(context, 401, ManagerUiViews.Login("Wrong admin token.")).ConfigureAwait(false);
            return;
        }

        var actor = $"ui:{admin.Name}:{AdminRoleParser.ToApi(role)}";
        var id = Sessions.Create(role, actor, out _);
        _store.Audit(actor, "ui.login", "/login", "ok");
        await RedirectAsync(context, "/", SessionCookieHeader(id)).ConfigureAwait(false);
    }

    private bool RequireCsrf(HttpListenerContext context, ManagerSession session)
    {
        var presented = context.Request.Headers[CsrfHeader]
                        ?? context.Request.Headers["X-CSRF-Token"];
        if (ManagerSessionStore.CsrfMatches(session, presented))
        {
            return true;
        }

        WriteTextAsync(context, 403, "csrf token missing or wrong").GetAwaiter().GetResult();
        return false;
    }

    /// <summary>Notifies wait hub after a UI-driven publish (when API is called through session).</summary>
    public void NotifyFleet(long fleetId) => _waitHub.NotifyFleet(fleetId);

    private static string? FormValue(string body, string name)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var key = idx < 0 ? part : part[..idx];
            if (!string.Equals(Uri.UnescapeDataString(key.Replace('+', ' ')), name, StringComparison.Ordinal))
            {
                continue;
            }

            var raw = idx < 0 ? "" : part[(idx + 1)..];
            return Uri.UnescapeDataString(raw.Replace('+', ' '));
        }

        return null;
    }

    private static string? ReadCookie(HttpListenerRequest request, string name)
    {
        var header = request.Headers["Cookie"];
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = part[..idx].Trim();
            if (!string.Equals(key, name, StringComparison.Ordinal))
            {
                continue;
            }

            return part[(idx + 1)..].Trim();
        }

        return null;
    }

    private static KeyValuePair<string, string> SessionCookieHeader(string id) =>
        new("Set-Cookie",
            $"{SessionCookie}={id}; Path=/; HttpOnly; SameSite=Strict; Max-Age=86400");

    private static KeyValuePair<string, string> ClearCookie() =>
        new("Set-Cookie", $"{SessionCookie}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0");

    private static async Task RedirectAsync(
        HttpListenerContext context,
        string location,
        KeyValuePair<string, string>? extra = null)
    {
        context.Response.StatusCode = 303;
        context.Response.Headers["Location"] = location;
        ApplySecurityHeaders(context);
        if (extra is { } h)
        {
            context.Response.Headers[h.Key] = h.Value;
        }

        context.Response.Close();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, int status, string html)
    {
        await WriteTextAsync(context, status, html, "text/html; charset=utf-8").ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        ApplySecurityHeaders(context);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task WriteTextAsync(
        HttpListenerContext context,
        int status,
        string text,
        string contentType = "text/plain; charset=utf-8",
        KeyValuePair<string, string>? extraHeaders = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        ApplySecurityHeaders(context);
        if (extraHeaders is { } h)
        {
            context.Response.Headers[h.Key] = h.Value;
        }

        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static void ApplySecurityHeaders(HttpListenerContext context)
    {
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }
}
