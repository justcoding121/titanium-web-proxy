using System.Globalization;
using System.Net;
using System.Text;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;

namespace Titanium.Manager.Ui;

internal enum ManagerUiView
{
    Fleets,
    Fleet,
    Revisions,
    Diff,
    Editor,
    Rollouts,
    Rollout,
    Audit,
}

/// <summary>
///     Server-rendered HTML for the manager UI. Dynamic values go through <see cref="Enc"/>.
///     No inline script or style — assets load from <c>/assets/</c> under the same CSP as Plus dashboard.
/// </summary>
internal static class ManagerUiViews
{
    internal static readonly (ManagerUiView View, string PathHint, string Slug, string Title)[] Navigation =
    [
        (ManagerUiView.Fleets, "/", "fleets", "Fleets"),
        (ManagerUiView.Audit, "/audit", "audit", "Audit"),
    ];

    private const string HtmlCloseTd = "</td>";
    private const string HtmlCloseTdTr = "</td></tr>";
    private const string HtmlCloseTableSection = "</tbody></table></section>";
    private const string HtmlCloseDd = "</dd>";
    private const string HtmlCloseDiv = "</div>";

    public static string Enc(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string Login(string? error)
    {
        var sb = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>")
            .Append("<title>Titanium Manager - Sign in</title>")
            .Append("<link rel=\"stylesheet\" href=\"/assets/manager.css\"/></head>")
            .Append("<body data-testid=\"manager-login\" class=\"login\"><main>")
            .Append("<h1>Titanium Manager</h1>")
            .Append("<form method=\"post\" action=\"/login\" data-testid=\"login-form\">")
            .Append("<label for=\"token\">Admin token</label>")
            .Append("<input id=\"token\" name=\"token\" type=\"password\" autocomplete=\"current-password\" data-testid=\"login-token\" required/>")
            .Append("<button type=\"submit\" data-testid=\"login-submit\">Sign in</button></form>");

        if (error is not null)
        {
            sb.Append("<p class=\"error\" role=\"alert\" data-testid=\"login-error\">").Append(Enc(error)).Append("</p>");
        }

        return sb.Append("</main></body></html>").ToString();
    }

    public static string Page(
        ManagerUiView view,
        string role,
        string csrfToken,
        string title,
        string body,
        long? fleetId = null)
    {
        var slug = view.ToString().ToLowerInvariant();
        var sb = new StringBuilder()
            .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>")
            .Append("<title>Titanium Manager - ").Append(Enc(title)).Append("</title>")
            .Append("<link rel=\"stylesheet\" href=\"/assets/manager.css\"/></head>")
            .Append("<body data-testid=\"manager-ui\" data-view=\"").Append(Enc(slug))
            .Append("\" data-role=\"").Append(Enc(role))
            .Append("\" data-csrf=\"").Append(Enc(csrfToken)).Append('"');
        if (fleetId is not null)
        {
            sb.Append(" data-fleet-id=\"").Append(fleetId.Value.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        sb.Append('>')
            .Append("<header><h1>Titanium Manager</h1><nav aria-label=\"Views\">");
        foreach (var (_, path, navSlug, navTitle) in Navigation)
        {
            sb.Append("<a data-testid=\"nav-").Append(navSlug).Append("\" href=\"").Append(path).Append('"');
            if (((view is ManagerUiView.Fleets or ManagerUiView.Fleet or ManagerUiView.Revisions or ManagerUiView.Diff
                      or ManagerUiView.Editor or ManagerUiView.Rollouts or ManagerUiView.Rollout) && navSlug == "fleets")
                || (view == ManagerUiView.Audit && navSlug == "audit"))
            {
                sb.Append(" aria-current=\"page\"");
            }

            sb.Append('>').Append(Enc(navTitle)).Append("</a>");
        }

        if (fleetId is not null)
        {
            var fid = fleetId.Value.ToString(CultureInfo.InvariantCulture);
            sb.Append("<a data-testid=\"nav-fleet\" href=\"/fleets/").Append(fid).Append("\">Fleet</a>")
                .Append("<a data-testid=\"nav-revisions\" href=\"/fleets/").Append(fid).Append("/revisions\">Revisions</a>")
                .Append("<a data-testid=\"nav-editor\" href=\"/fleets/").Append(fid).Append("/editor\">Editor</a>")
                .Append("<a data-testid=\"nav-rollouts\" href=\"/fleets/").Append(fid).Append("/rollouts\">Rollouts</a>");
        }

        sb.Append("</nav><span class=\"who\" data-testid=\"whoami\">").Append(Enc(role)).Append("</span>")
            .Append("<button type=\"button\" id=\"logout\" data-testid=\"btn-logout\">Sign out</button></header>")
            .Append("<p id=\"flash\" role=\"status\" data-testid=\"flash\"></p>")
            .Append("<main>").Append(body).Append("</main>")
            .Append("<script src=\"/assets/manager.js\"></script></body></html>");
        return sb.ToString();
    }

    public static string Fleets(IReadOnlyList<FleetRow> fleets, bool canEdit)
    {
        var sb = new StringBuilder("<section data-testid=\"fleets\">")
            .Append("<h2>Fleets</h2>");
        if (canEdit)
        {
            sb.Append("<form id=\"create-fleet\" data-testid=\"create-fleet-form\" class=\"inline-form\">")
                .Append("<label for=\"fleet-name\">Name</label>")
                .Append("<input id=\"fleet-name\" name=\"name\" type=\"text\" required data-testid=\"fleet-name\"/>")
                .Append("<button type=\"submit\" data-testid=\"btn-create-fleet\">Create fleet</button></form>");
        }

        sb.Append("<table><thead><tr><th>Id</th><th>Name</th><th>Created</th></tr></thead><tbody>");
        if (fleets.Count == 0)
        {
            sb.Append("<tr><td colspan=\"3\">No fleets yet.</td></tr>");
        }
        else
        {
            foreach (var f in fleets)
            {
                sb.Append("<tr data-testid=\"fleet-row-").Append(f.Id.ToString(CultureInfo.InvariantCulture)).Append("\">")
                    .Append("<td>").Append(f.Id.ToString(CultureInfo.InvariantCulture)).Append(HtmlCloseTd)
                    .Append("<td><a href=\"/fleets/").Append(f.Id.ToString(CultureInfo.InvariantCulture))
                    .Append("\" data-testid=\"fleet-link-").Append(f.Id.ToString(CultureInfo.InvariantCulture))
                    .Append("\">").Append(Enc(f.Name)).Append("</a></td>")
                    .Append("<td>").Append(Enc(f.CreatedAt.ToString("u", CultureInfo.InvariantCulture))).Append(HtmlCloseTdTr);
            }
        }

        return sb.Append(HtmlCloseTableSection).ToString();
    }

    public static string FleetDetail(
        FleetRow fleet,
        PublishedConfig? published,
        IReadOnlyList<AgentRow> agents)
    {
        var fid = fleet.Id.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder("<section data-testid=\"fleet-detail\">")
            .Append("<h2>").Append(Enc(fleet.Name)).Append(" <span class=\"muted\">#").Append(fid).Append("</span></h2>")
            .Append("<dl class=\"facts\">")
            .Append("<dt>Published revision</dt><dd data-testid=\"published-revision\">")
            .Append(published is null ? "none" : published.RevisionId.ToString(CultureInfo.InvariantCulture))
            .Append(HtmlCloseDd)
            .Append("<dt>Published hash</dt><dd data-testid=\"published-hash\">")
            .Append(Enc(published?.ContentHash ?? "-"))
            .Append("</dd></dl>")
            .Append("<p class=\"actions\">")
            .Append("<a data-testid=\"link-editor\" href=\"/fleets/").Append(fid).Append("/editor\">Open editor</a> · ")
            .Append("<a data-testid=\"link-revisions\" href=\"/fleets/").Append(fid).Append("/revisions\">Revisions</a> · ")
            .Append("<a data-testid=\"link-rollouts\" href=\"/fleets/").Append(fid).Append("/rollouts\">Rollouts</a></p>")
            .Append("<h3>Agents</h3>")
            .Append("<table data-testid=\"agents-table\"><thead><tr>")
            .Append("<th>Node</th><th>Status</th><th>Config hash</th><th>Desired rev</th><th>Last seen</th><th>Health</th>")
            .Append("</tr></thead><tbody>");

        if (agents.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\">No agents reported yet.</td></tr>");
        }
        else
        {
            var publishedHash = published?.ContentHash;
            foreach (var a in agents)
            {
                var drift = publishedHash is not null &&
                            !string.Equals(a.ConfigHash, publishedHash, StringComparison.OrdinalIgnoreCase);
                sb.Append("<tr data-testid=\"agent-row-").Append(Enc(a.NodeId)).Append('"');
                if (drift)
                {
                    sb.Append(" class=\"drift\"");
                }

                sb.Append("><td>").Append(Enc(a.NodeId)).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.Status)).Append(drift ? " (drift)" : "").Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.ConfigHash ?? "-")).Append(HtmlCloseTd)
                    .Append("<td>").Append(a.DesiredRevisionId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(HtmlCloseTd)
                    .Append("<td>").Append(a.LastSeen is null
                        ? "-"
                        : Enc(a.LastSeen.Value.ToString("u", CultureInfo.InvariantCulture))).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.HealthJson ?? "-")).Append(HtmlCloseTdTr);
            }
        }

        return sb.Append(HtmlCloseTableSection).ToString();
    }

    public static string Revisions(long fleetId, IReadOnlyList<RevisionRow> revisions)
    {
        var fid = fleetId.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder("<section data-testid=\"revisions\">")
            .Append("<h2>Revisions</h2>")
            .Append("<table><thead><tr><th>Id</th><th>Hash</th><th>Author</th><th>Parent</th><th>Created</th><th></th></tr></thead><tbody>");
        if (revisions.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\">No revisions yet. Use the editor to create one.</td></tr>");
        }
        else
        {
            foreach (var r in revisions)
            {
                var rid = r.Id.ToString(CultureInfo.InvariantCulture);
                sb.Append("<tr data-testid=\"revision-row-").Append(rid).Append("\">")
                    .Append("<td>").Append(rid).Append(HtmlCloseTd)
                    .Append("<td><code>").Append(Enc(r.ContentHash)).Append("</code></td>")
                    .Append("<td>").Append(Enc(r.Author)).Append(HtmlCloseTd)
                    .Append("<td>").Append(r.ParentId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(r.CreatedAt.ToString("u", CultureInfo.InvariantCulture))).Append(HtmlCloseTd)
                    .Append("<td>");
                if (r.ParentId is long parent)
                {
                    sb.Append("<a data-testid=\"diff-link-").Append(rid).Append("\" href=\"/fleets/").Append(fid)
                        .Append("/diff?from=").Append(parent.ToString(CultureInfo.InvariantCulture))
                        .Append("&to=").Append(rid).Append("\">Diff</a>");
                }

                sb.Append(HtmlCloseTdTr);
            }
        }

        return sb.Append(HtmlCloseTableSection).ToString();
    }

    public static string Diff(long fleetId, long fromId, long toId, string unified)
    {
        var sb = new StringBuilder("<section data-testid=\"diff\">")
            .Append("<h2>Diff rev ").Append(fromId.ToString(CultureInfo.InvariantCulture))
            .Append(" → ").Append(toId.ToString(CultureInfo.InvariantCulture)).Append("</h2>")
            .Append("<pre data-testid=\"diff-text\" class=\"diff\">").Append(Enc(unified)).Append("</pre>")
            .Append("<p><a href=\"/fleets/").Append(fleetId.ToString(CultureInfo.InvariantCulture))
            .Append("/revisions\">Back to revisions</a></p></section>");
        return sb.ToString();
    }

    public static string Editor(
        long fleetId,
        string yaml,
        DraftSummary summary,
        bool canEdit)
    {
        var fid = fleetId.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder("<section data-testid=\"editor\" class=\"editor\">")
            .Append("<h2>Config editor</h2>")
            .Append("<p class=\"hint\">Edit YAML or use the side panel to add/edit routes and clusters, then validate and publish.</p>")
            .Append("<div class=\"editor-grid\">")
            .Append("<div class=\"editor-main\">")
            .Append("<label for=\"draft-yaml\">Draft YAML</label>")
            .Append("<textarea id=\"draft-yaml\" data-testid=\"draft-yaml\" spellcheck=\"false\"");
        if (!canEdit)
        {
            sb.Append(" readonly");
        }

        sb.Append('>').Append(Enc(yaml)).Append("</textarea>")
            .Append("<div class=\"actions\">");
        if (canEdit)
        {
            sb.Append("<button type=\"button\" data-testid=\"btn-validate\" data-act=\"validate\">Validate</button> ")
                .Append("<button type=\"button\" data-testid=\"btn-diff-published\" data-act=\"diff-published\">Diff vs published</button> ")
                .Append("<button type=\"button\" data-testid=\"btn-publish\" data-act=\"publish\">Publish</button>");
        }

        sb.Append(HtmlCloseDiv)
            .Append("<pre id=\"editor-result\" data-testid=\"editor-result\" class=\"result\"></pre>")
            .Append("</div><aside class=\"editor-side\" data-testid=\"editor-side\">")
            .Append("<h3>Routes</h3><ul id=\"route-list\" data-testid=\"route-list\">");
        foreach (var r in summary.Routes)
        {
            sb.Append("<li data-testid=\"route-item-").Append(Enc(r.Id)).Append("\">")
                .Append("<button type=\"button\" data-act=\"select-route\" data-id=\"").Append(Enc(r.Id))
                .Append("\">").Append(Enc(r.Id)).Append("</button> ")
                .Append("<span class=\"muted\">").Append(Enc(r.Path ?? "")).Append(" → ")
                .Append(Enc(r.ClusterId ?? "")).Append("</span></li>");
        }

        sb.Append("</ul>");
        if (canEdit)
        {
            sb.Append("<div class=\"stub-form\" data-testid=\"route-form\">")
                .Append("<label for=\"route-id\">Route id</label>")
                .Append("<input id=\"route-id\" data-testid=\"route-id\" type=\"text\"/>")
                .Append("<label for=\"route-path\">Path</label>")
                .Append("<input id=\"route-path\" data-testid=\"route-path\" type=\"text\" placeholder=\"/api\"/>")
                .Append("<label for=\"route-cluster\">Cluster id</label>")
                .Append("<input id=\"route-cluster\" data-testid=\"route-cluster\" type=\"text\"/>")
                .Append("<button type=\"button\" data-testid=\"btn-upsert-route\" data-act=\"upsert-route\">Add / update route</button> ")
                .Append("<button type=\"button\" data-testid=\"btn-remove-route\" data-act=\"remove-route\">Remove route</button>")
                .Append(HtmlCloseDiv);
        }

        sb.Append("<h3>Clusters</h3><ul id=\"cluster-list\" data-testid=\"cluster-list\">");
        foreach (var c in summary.Clusters)
        {
            sb.Append("<li data-testid=\"cluster-item-").Append(Enc(c.Id)).Append("\">")
                .Append(Enc(c.Id)).Append(" <span class=\"muted\">(")
                .Append(c.DestinationCount.ToString(CultureInfo.InvariantCulture))
                .Append(" dest)</span></li>");
        }

        sb.Append("</ul>");
        if (canEdit)
        {
            sb.Append("<div class=\"stub-form\" data-testid=\"cluster-form\">")
                .Append("<label for=\"cluster-id\">Cluster id</label>")
                .Append("<input id=\"cluster-id\" data-testid=\"cluster-id\" type=\"text\"/>")
                .Append("<button type=\"button\" data-testid=\"btn-upsert-cluster\" data-act=\"upsert-cluster\">Add cluster stub</button> ")
                .Append("<button type=\"button\" data-testid=\"btn-remove-cluster\" data-act=\"remove-cluster\">Remove cluster</button>")
                .Append(HtmlCloseDiv);
        }

        sb.Append("</aside></div>")
            .Append("<p><a href=\"/fleets/").Append(fid).Append("\">Back to fleet</a></p></section>");
        return sb.ToString();
    }

    public static string Rollouts(long fleetId, IReadOnlyList<RolloutRow> rollouts)
    {
        var fid = fleetId.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder("<section data-testid=\"rollouts\">")
            .Append("<h2>Rollouts</h2>")
            .Append("<table><thead><tr><th>Id</th><th>Revision</th><th>State</th><th>Pause</th><th>Updated</th></tr></thead><tbody>");
        if (rollouts.Count == 0)
        {
            sb.Append("<tr><td colspan=\"5\">No rollouts yet.</td></tr>");
        }
        else
        {
            foreach (var r in rollouts)
            {
                var rid = r.Id.ToString(CultureInfo.InvariantCulture);
                sb.Append("<tr data-testid=\"rollout-row-").Append(rid).Append("\">")
                    .Append("<td><a href=\"/fleets/").Append(fid).Append("/rollouts/").Append(rid)
                    .Append("\" data-testid=\"rollout-link-").Append(rid).Append("\">").Append(rid).Append("</a></td>")
                    .Append("<td>").Append(r.RevisionId.ToString(CultureInfo.InvariantCulture)).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(r.State)).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(r.PauseReason ?? "-")).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(r.UpdatedAt.ToString("u", CultureInfo.InvariantCulture))).Append(HtmlCloseTdTr);
            }
        }

        return sb.Append(HtmlCloseTableSection).ToString();
    }

    public static string RolloutDetail(long fleetId, RolloutRow rollout, IReadOnlyList<RolloutStepRow> steps, bool canEdit)
    {
        var fid = fleetId.ToString(CultureInfo.InvariantCulture);
        var rid = rollout.Id.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder("<section data-testid=\"rollout-detail\">")
            .Append("<h2>Rollout #").Append(rid).Append("</h2>")
            .Append("<dl class=\"facts\">")
            .Append("<dt>Revision</dt><dd>").Append(rollout.RevisionId.ToString(CultureInfo.InvariantCulture)).Append(HtmlCloseDd)
            .Append("<dt>Previous</dt><dd>")
            .Append(rollout.PreviousRevisionId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(HtmlCloseDd)
            .Append("<dt>State</dt><dd data-testid=\"rollout-state\">").Append(Enc(rollout.State)).Append(HtmlCloseDd)
            .Append("<dt>Pause reason</dt><dd>").Append(Enc(rollout.PauseReason ?? "-")).Append(HtmlCloseDd)
            .Append("</dl>");

        if (canEdit)
        {
            sb.Append("<div class=\"actions\" data-testid=\"rollout-actions\">")
                .Append("<button type=\"button\" data-testid=\"btn-pause-rollout\" data-act=\"pause-rollout\" data-id=\"")
                .Append(rid).Append("\">Pause</button> ")
                .Append("<button type=\"button\" data-testid=\"btn-resume-rollout\" data-act=\"resume-rollout\" data-id=\"")
                .Append(rid).Append("\">Resume</button> ")
                .Append("<button type=\"button\" data-testid=\"btn-rollback-rollout\" data-act=\"rollback-rollout\" data-id=\"")
                .Append(rid).Append("\">Rollback</button>")
                .Append(HtmlCloseDiv);
        }

        sb.Append("<h3>Steps</h3>")
            .Append("<table><thead><tr><th>#</th><th>Selector</th><th>Max unavail</th><th>Bake</th><th>State</th></tr></thead><tbody>");
        foreach (var s in steps)
        {
            sb.Append("<tr><td>").Append(s.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(HtmlCloseTd)
                .Append("<td><code>").Append(Enc(s.LabelSelectorJson)).Append("</code></td>")
                .Append("<td>").Append(s.MaxUnavailable.ToString(CultureInfo.InvariantCulture)).Append(HtmlCloseTd)
                .Append("<td>").Append(s.BakeSeconds.ToString(CultureInfo.InvariantCulture)).Append("s</td>")
                .Append("<td>").Append(Enc(s.State)).Append(HtmlCloseTdTr);
        }

        if (steps.Count == 0)
        {
            sb.Append("<tr><td colspan=\"5\">No steps.</td></tr>");
        }

        sb.Append("</tbody></table>")
            .Append("<p><a href=\"/fleets/").Append(fid).Append("/rollouts\">All rollouts</a></p></section>");
        return sb.ToString();
    }

    public static string Audit(IReadOnlyList<AuditRow> rows)
    {
        var sb = new StringBuilder("<section data-testid=\"audit\">")
            .Append("<h2>Audit</h2>")
            .Append("<table><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Target</th><th>Result</th><th>Detail</th></tr></thead><tbody>");
        if (rows.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\">No audit events yet.</td></tr>");
        }
        else
        {
            foreach (var a in rows)
            {
                sb.Append("<tr data-testid=\"audit-row-").Append(a.Id.ToString(CultureInfo.InvariantCulture)).Append("\">")
                    .Append("<td>").Append(Enc(a.Time.ToString("u", CultureInfo.InvariantCulture))).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.Actor)).Append(HtmlCloseTd)
                    .Append("<td data-testid=\"audit-action\">").Append(Enc(a.Action)).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.Target)).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.Result)).Append(HtmlCloseTd)
                    .Append("<td>").Append(Enc(a.Detail ?? "")).Append(HtmlCloseTdTr);
            }
        }

        return sb.Append(HtmlCloseTableSection).ToString();
    }

    public static string RoleName(AdminRole role) => AdminRoleParser.ToApi(role);
}
