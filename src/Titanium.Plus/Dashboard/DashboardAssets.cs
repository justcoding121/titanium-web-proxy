namespace Titanium.Plus.Dashboard;

/// <summary>
///     The dashboard's script and stylesheet, served from <c>/assets/</c> so the page needs no inline script or
///     style (Content-Security-Policy <c>default-src 'self'</c>). The script polls <c>/api/*</c> every two seconds and
///     writes values with <c>textContent</c> only, never <c>innerHTML</c>, so server data cannot become markup.
/// </summary>
internal static class DashboardAssets
{
    public const string ScriptPath = "/assets/dashboard.js";
    public const string StylePath = "/assets/dashboard.css";

    public const string Script = """
        (function () {
          'use strict';
          var body = document.body;
          var view = body.getAttribute('data-view');
          var csrf = body.getAttribute('data-csrf') || '';
          var canOperate = body.getAttribute('data-role') === 'operator';
          var flash = document.getElementById('flash');
          var POLL_MS = 2000;
          var busy = false;

          function el(tag, text) {
            var e = document.createElement(tag);
            if (text !== undefined && text !== null) { e.textContent = String(text); }
            return e;
          }
          function num(x, digits) { var f = Math.pow(10, digits); return String(Math.round(x * f) / f); }
          function seconds(s) {
            if (s === null || s === undefined) { return '-'; }
            return s < 1 ? num(s * 1000, 2) + ' ms' : num(s, 3) + ' s';
          }
          function ratio(r) { return r === null || r === undefined ? '-' : num(r * 100, 2) + ' %'; }
          function setField(name, text) {
            var node = document.querySelector('[data-field="' + name + '"]');
            if (node) { node.textContent = String(text); }
          }
          function fill(id, items, cols, empty, build) {
            var tbody = document.getElementById(id);
            if (!tbody) { return; }
            var frag = document.createDocumentFragment();
            if (!items.length) {
              var tr = el('tr'); var td = el('td', empty); td.colSpan = cols; tr.appendChild(td); frag.appendChild(tr);
            } else {
              for (var i = 0; i < items.length; i++) { frag.appendChild(build(items[i])); }
            }
            tbody.replaceChildren(frag);
          }
          function row(cells) {
            var tr = el('tr');
            for (var i = 0; i < cells.length; i++) {
              var c = cells[i];
              tr.appendChild(c !== null && typeof c === 'object' ? c : el('td', c));
            }
            return tr;
          }
          function button(act, id, label) {
            var b = el('button', label);
            b.type = 'button';
            b.setAttribute('data-act', act);
            b.setAttribute('data-id', id);
            b.setAttribute('data-testid', 'btn-' + act + '-' + id);
            b.disabled = !canOperate;
            return b;
          }
          function address(d) { return d.address ? (d.https ? 'https' : 'http') + '://' + d.address + ':' + d.port : '-'; }

          var renderers = {
            overview: function (d) {
              setField('rps', num(d.requestsPerSecond, 2) + ' /s');
              setField('error-ratio', ratio(d.errorRatio));
              setField('p50', seconds(d.latency.p50Seconds));
              setField('p95', seconds(d.latency.p95Seconds));
              setField('p99', seconds(d.latency.p99Seconds));
              setField('active-connections', d.activeConnections);
              setField('active-requests', d.activeRequests);
              setField('total-requests', d.totalRequests);
            },
            routes: function (d) {
              fill('routes-body', d.routes, 10, 'No routes configured.', function (r) {
                var tr = row([r.id, r.match, r.action, r.clusterId, r.transforms.join('; '), r.requests, r.errors,
                  seconds(r.p50Seconds), seconds(r.p95Seconds), seconds(r.p99Seconds)]);
                tr.setAttribute('data-testid', 'route-row-' + r.id);
                return tr;
              });
              fill('clusters-body', d.clusters, 6, 'No clusters configured.', function (c) {
                var tls = [];
                if (c.serverName) { tls.push('sni ' + c.serverName); }
                if (c.clientCertificate) { tls.push('client certificate'); }
                if (c.caBundle) { tls.push('CA bundle'); }
                if (c.acceptAnyServerCertificate) { tls.push('accepts any server certificate'); }
                if (!c.verifyHostname) { tls.push('hostname not verified'); }
                var tr = row([c.id, c.algorithm, c.healthy + ' / ' + c.destinations, c.activeHealth ? 'yes' : 'no',
                  c.passiveHealth ? 'yes' : 'no', tls.length ? tls.join(', ') : 'default']);
                tr.setAttribute('data-testid', 'cluster-row-' + c.id);
                return tr;
              });
              fill('destinations-body', d.destinations, 5, 'No destinations in snapshot.', function (x) {
                var state = el('td', x.state);
                state.setAttribute('data-testid', 'dest-state-' + x.id);
                var actions = el('td');
                actions.appendChild(button('drain', x.id, 'Drain'));
                actions.appendChild(document.createTextNode(' '));
                actions.appendChild(button('healthy', x.id, 'Healthy'));
                var tr = row([x.id, x.clusterId, address(x), state, actions]);
                tr.setAttribute('data-testid', 'dest-row-' + x.id);
                return tr;
              });
            },
            health: function (d) {
              fill('health-body', d, 5, 'No ejections or probe failures recorded.', function (e) {
                return row([e.time, e.destination, e.kind, e.source, e.detail]);
              });
            },
            config: function (d) {
              if (!d.available) { return; }
              setField('schema-version', d.schemaVersion);
              setField('config-hash', d.configHash);
              setField('loaded', d.loaded);
              setField('last-reload', d.lastReload || 'never');
              setField('last-result', d.lastResult || '-');
              setField('last-error', d.lastError || '-');
            },
            certs: function (d) {
              fill('certs-body', d, 7, 'No certificates to report.', function (c) {
                var tr = row([c.name, c.role, c.subject, c.issuer, c.notAfter || '-',
                  c.daysLeft === null ? '-' : c.daysLeft, c.error ? c.status + ': ' + c.error : c.status]);
                tr.className = c.status;
                return tr;
              });
            },
            audit: function (d) {
              fill('audit-body', d, 6, 'No audit events yet.', function (e) {
                return row([e.time, e.actor, e.remote, e.action, e.target, e.result]);
              });
            }
          };

          function tick() {
            var render = renderers[view];
            if (!render || busy || document.hidden) { return Promise.resolve(); }
            busy = true;
            return fetch('/api/' + view, {
              credentials: 'same-origin',
              cache: 'no-store',
              headers: { 'Accept': 'application/json', 'X-Titanium-Poll': '1' }
            }).then(function (r) {
              if (r.status === 401) { window.location.href = '/login'; return null; }
              return r.ok ? r.json() : null;
            }).then(function (d) {
              if (d) { render(d); }
            }).catch(function () { /* the next tick retries */ }).then(function () { busy = false; });
          }

          function say(text) { if (flash) { flash.textContent = text; } }

          function post(path, done) {
            return fetch(path, {
              method: 'POST',
              credentials: 'same-origin',
              headers: { 'X-Titanium-Csrf': csrf }
            }).then(function (r) {
              return r.text().then(function (text) { done(r, text); });
            }).catch(function () { say('Request failed.'); });
          }

          function edit(mode) {
            var form = document.querySelector('form[data-kind]');
            var area = document.getElementById('edit-json');
            var out = document.getElementById('edit-result');
            if (!form || !area || !out) { return; }
            var kind = form.getAttribute('data-kind');
            var id = form.getAttribute('data-id');
            if (mode === 'delete' && !window.confirm('Delete ' + kind + ' ' + id + '?')) { return; }
            out.textContent = 'Working...';
            fetch('/edit/' + kind + '/' + mode + (id ? '/' + encodeURIComponent(id) : ''), {
              method: 'POST',
              credentials: 'same-origin',
              headers: { 'X-Titanium-Csrf': csrf, 'Content-Type': 'application/json' },
              body: mode === 'delete' ? '' : area.value
            }).then(function (r) {
              return r.text().then(function (text) {
                var d = null;
                try { d = JSON.parse(text); } catch (x) { d = null; }
                if (r.ok && d && d.ok) {
                  out.textContent = mode === 'validate' ? 'Valid.' : mode === 'delete' ? 'Deleted.' : 'Saved.';
                  if (mode !== 'validate') { window.setTimeout(function () { window.location.href = '/routes'; }, 600); }
                } else {
                  out.textContent = d && d.errors && d.errors.length ? d.errors.join('\n') : 'Refused (' + r.status + '): ' + text;
                }
              });
            }).catch(function () { out.textContent = 'Request failed.'; });
          }

          document.addEventListener('click', function (event) {
            var target = event.target;
            if (!target || !target.closest) { return; }
            var editButton = target.closest('button[data-edit]');
            if (editButton) { edit(editButton.getAttribute('data-edit')); return; }
            var b = target.closest('button[data-act]');
            if (b && !b.disabled) {
              var act = b.getAttribute('data-act');
              var id = b.getAttribute('data-id');
              post('/' + act + '/' + encodeURIComponent(id), function (r, text) {
                var okMsg = act === 'drain' ? 'Draining ' + id
                  : act === 'reload' ? 'Reload ' + text
                  : act === 'clear-overlay' ? 'Dashboard edits cleared'
                  : 'Marked healthy: ' + id;
                say(r.ok ? okMsg : 'Refused (' + r.status + '): ' + text);
                if (r.ok && act === 'clear-overlay') { window.location.reload(); }
                tick();
              });
              return;
            }
            if (target.closest('#logout')) {
              post('/logout', function () { window.location.href = '/login'; });
            }
          });

          tick();
          window.setInterval(tick, POLL_MS);
        })();
        """;

    public const string Style = """
        :root { color-scheme: light dark; --line: #8886; --bad: #c0392b; --warn: #b9770e; --ok: #1e8449; }
        body { font-family: system-ui, sans-serif; margin: 1.25rem; }
        header { display: flex; flex-wrap: wrap; align-items: center; gap: 1rem; }
        header h1 { margin: 0; font-size: 1.4rem; }
        nav { display: flex; flex-wrap: wrap; gap: .75rem; flex: 1; }
        nav a[aria-current="page"] { font-weight: 700; text-decoration: none; }
        table { border-collapse: collapse; width: 100%; margin-bottom: 1.5rem; }
        th, td { border: 1px solid var(--line); padding: .35rem .55rem; text-align: left; vertical-align: top; overflow-wrap: anywhere; }
        .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr)); gap: .75rem; }
        .card { border: 1px solid var(--line); border-radius: .4rem; padding: .6rem .8rem; display: flex; flex-direction: column; }
        .card .label { font-size: .8rem; opacity: .75; }
        .card .value { font-size: 1.5rem; font-weight: 600; }
        .hint { opacity: .75; font-size: .9rem; }
        .banner.warn { border: 1px solid var(--warn); color: var(--warn); padding: .6rem .8rem; border-radius: .4rem; }
        .facts { display: grid; grid-template-columns: max-content 1fr; gap: .35rem 1rem; }
        .facts dt { font-weight: 600; }
        .facts dd { margin: 0; overflow-wrap: anywhere; }
        #flash { min-height: 1.2rem; }
        tr.expired td { color: var(--bad); }
        tr.expiring td { color: var(--warn); }
        .login main { max-width: 22rem; margin: 6rem auto; }
        .login form { display: flex; flex-direction: column; gap: .6rem; }
        .error { color: var(--bad); }
        button { cursor: pointer; }
        button:disabled { cursor: not-allowed; }
        textarea#edit-json { width: 100%; box-sizing: border-box; font-family: ui-monospace, monospace; }
        #edit-result { white-space: pre-wrap; min-height: 1.2rem; }
        """;
}
