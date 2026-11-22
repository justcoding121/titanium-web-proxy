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
            b.className = act === 'drain' ? 'danger' : 'secondary';
            b.setAttribute('data-act', act);
            b.setAttribute('data-id', id);
            b.setAttribute('data-testid', 'btn-' + act + '-' + id);
            b.disabled = !canOperate;
            if (!canOperate) { b.title = 'Viewer role cannot operate'; }
            return b;
          }
          function badge(text) {
            var lower = String(text).toLowerCase();
            var kind = 'ok';
            if (lower.indexOf('unhealthy') >= 0 || lower.indexOf('fail') >= 0 || lower.indexOf('expir') >= 0) { kind = 'bad'; }
            else if (lower.indexOf('drain') >= 0 || lower.indexOf('eject') >= 0) { kind = 'warn'; }
            var mark = kind === 'warn' ? '\u25D0 ' : '\u25CF ';
            var span = el('span', mark + text);
            span.className = 'badge ' + kind;
            return span;
          }
          var history = {};
          function spark(name, value) {
            var host = document.querySelector('[data-spark="' + name + '"]');
            if (!host) { return; }
            if (!history[name]) { history[name] = []; }
            history[name].push(Number(value) || 0);
            if (history[name].length > 60) { history[name].shift(); }
            var pts = history[name];
            var max = 1;
            for (var i = 0; i < pts.length; i++) { if (pts[i] > max) { max = pts[i]; } }
            var parts = [];
            for (var j = 0; j < pts.length; j++) {
              var x = pts.length === 1 ? 0 : (j / (pts.length - 1)) * 100;
              var y = 28 - (pts[j] / max) * 26;
              parts.push(num(x, 2) + ',' + num(y, 2));
            }
            var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.setAttribute('viewBox', '0 0 100 28');
            svg.setAttribute('preserveAspectRatio', 'none');
            var line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
            line.setAttribute('points', parts.join(' '));
            line.setAttribute('class', 'sparkline');
            svg.appendChild(line);
            host.replaceChildren(svg);
          }
          function tone(name, level) {
            var card = document.querySelector('[data-card="' + name + '"]');
            if (!card) { return; }
            card.classList.remove('warn', 'bad');
            if (level) { card.classList.add(level); }
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
              spark('rps', d.requestsPerSecond);
              spark('error-ratio', d.errorRatio);
              spark('p99', d.latency.p99Seconds);
              var errorLevel = d.errorRatio > 0.05 ? 'bad' : d.errorRatio > 0.01 ? 'warn' : '';
              var p99 = d.latency.p99Seconds;
              var p99Level = p99 > 3 ? 'bad' : p99 > 1 ? 'warn' : '';
              tone('error-ratio', errorLevel);
              tone('p99', p99Level);
            },
            routes: function (d) {
              fill('routes-body', d.routes, 10, 'No routes configured. Add a route from the edit panel below.', function (r) {
                var tr = row([r.id, r.match, r.action, r.clusterId, r.transforms.join('; '), r.requests, r.errors,
                  seconds(r.p50Seconds), seconds(r.p95Seconds), seconds(r.p99Seconds)]);
                tr.setAttribute('data-testid', 'route-row-' + r.id);
                return tr;
              });
              fill('clusters-body', d.clusters, 6, 'No clusters configured. Add a cluster from the edit panel below.', function (c) {
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
              fill('destinations-body', d.destinations, 5, 'No destinations in snapshot. Add a cluster from the edit panel below.', function (x) {
                var state = el('td');
                state.appendChild(badge(x.state));
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
              if (!r.ok) { markLive(false); return null; }
              return r.json();
            }).then(function (d) {
              if (d) { render(d); markLive(true); }
            }).catch(function () { markLive(false); }).then(function () { busy = false; });
          }

          var flashTimer = 0;
          function say(text, ok) {
            if (!flash) { return; }
            flash.textContent = text;
            flash.className = ok ? 'ok' : 'bad';
            window.clearTimeout(flashTimer);
            flashTimer = window.setTimeout(function () { flash.textContent = ''; flash.className = ''; }, 8000);
          }
          var live = document.querySelector('[data-testid="live-status"]');
          function markLive(ok) {
            if (!live) { return; }
            live.textContent = ok ? 'Live, updated 0 s ago' : 'Disconnected, retrying';
            live.className = ok ? 'live' : 'live bad';
          }

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
              if (act === 'drain' && !window.confirm('Drain ' + id + '? Traffic stops going to this destination.')) { return; }
              post('/' + act + '/' + encodeURIComponent(id), function (r, text) {
                var okMsg = act === 'drain' ? 'Draining ' + id
                  : act === 'reload' ? 'Reload ' + text
                  : act === 'clear-overlay' ? 'Dashboard edits cleared'
                  : 'Marked healthy: ' + id;
                say(r.ok ? okMsg : 'Refused (' + r.status + '): ' + text, r.ok);
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
        :root {
          color-scheme: light;
          --bg: #f3f6f9; --surface: #ffffff; --text: #1b2430; --muted: #4a596b;
          --line: #d3dbe4; --accent: #0b4f8a; --on-accent: #ffffff;
          --ok: #0d6b38; --ok-bg: #e6f6ec; --warn: #7a4e00; --warn-bg: #fff3d6;
          --bad: #8f1d1d; --bad-bg: #fdeaea; --focus: #0b4f8a;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            color-scheme: dark;
            --bg: #10161d; --surface: #1a2330; --text: #e7eef6; --muted: #b7c3d1;
            --line: #314155; --accent: #8cbcff; --on-accent: #0c1b30;
            --ok: #8ee0b0; --ok-bg: #143224; --warn: #f0c36a; --warn-bg: #3a2d10;
            --bad: #ffb4b4; --bad-bg: #3f1d1d; --focus: #8cbcff;
          }
        }
        body { font-family: system-ui, sans-serif; margin: 0; background: var(--bg); color: var(--text); line-height: 1.45; }
        .skip { position: absolute; left: .5rem; top: -3rem; background: var(--surface); color: var(--text); padding: .4rem .7rem; z-index: 3; }
        .skip:focus { top: .5rem; }
        main { padding: 1rem 1.25rem 2rem; }
        header { position: sticky; top: 0; z-index: 2; display: flex; flex-wrap: wrap; align-items: center; gap: .65rem 1rem; padding: .7rem 1.25rem; background: var(--surface); border-bottom: 1px solid var(--line); }
        header h1 { margin: 0; font-size: 1.2rem; }
        nav { display: flex; flex-wrap: wrap; gap: .3rem; flex: 1 1 auto; }
        @media (max-width: 720px) { nav { flex: 1 1 100%; } }
        nav a { color: var(--accent); text-decoration: none; padding: .35rem .6rem; border-radius: .4rem; }
        nav a[aria-current="page"] { background: var(--accent); color: var(--on-accent); font-weight: 650; }
        .who { font-size: .8rem; color: var(--muted); border: 1px solid var(--line); border-radius: 999px; padding: .1rem .55rem; }
        .live { font-size: .8rem; color: var(--ok); }
        .live.bad { color: var(--bad); }
        .table-scroll { overflow: auto; max-height: 70vh; margin-bottom: 1.5rem; max-width: 100%; }
        table { border-collapse: collapse; width: max-content; min-width: 100%; background: var(--surface); }
        th, td { border-bottom: 1px solid var(--line); padding: .45rem .6rem; text-align: left; vertical-align: top; white-space: nowrap; }
        th { position: sticky; top: 0; background: var(--surface); z-index: 1; }
        tbody tr:hover { background: color-mix(in srgb, var(--accent) 8%, var(--surface)); }
        .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(13.5rem, 1fr)); gap: .75rem; }
        .card { background: var(--surface); border: 1px solid var(--line); border-radius: .6rem; padding: .75rem .9rem; display: flex; flex-direction: column; gap: .15rem; }
        .card .label { font-size: .8rem; color: var(--muted); }
        .card .value { font-size: 1.6rem; font-weight: 650; }
        .card .threshold, .hint { color: var(--muted); font-size: .85rem; }
        .card.warn { border-color: var(--warn); background: var(--warn-bg); }
        .card.bad { border-color: var(--bad); background: var(--bad-bg); }
        .spark { height: 28px; }
        .spark svg { width: 100%; height: 28px; display: block; }
        .sparkline { fill: none; stroke: var(--accent); stroke-width: 1.5; }
        .badge { display: inline-block; border-radius: 999px; padding: .05rem .5rem; font-size: .85rem; border: 1px solid var(--line); }
        .badge.ok { color: var(--ok); background: var(--ok-bg); border-color: var(--ok); }
        .badge.warn { color: var(--warn); background: var(--warn-bg); border-color: var(--warn); }
        .badge.bad { color: var(--bad); background: var(--bad-bg); border-color: var(--bad); }
        .banner.warn { border: 1px solid var(--warn); color: var(--warn); background: var(--warn-bg); padding: .6rem .8rem; border-radius: .4rem; }
        .facts { display: grid; grid-template-columns: max-content 1fr; gap: .35rem 1rem; background: var(--surface); border: 1px solid var(--line); border-radius: .6rem; padding: .8rem 1rem; }
        .facts dt { font-weight: 650; }
        .facts dd { margin: 0; overflow-wrap: anywhere; }
        #flash { min-height: 1.2rem; margin: .6rem 1.25rem 0; }
        #flash.ok { color: var(--ok); }
        #flash.bad { color: var(--bad); }
        tr.expired td { color: var(--bad); }
        tr.expiring td { color: var(--warn); }
        .login { min-height: 100vh; }
        .login main { max-width: 22rem; margin: 8vh auto; background: var(--surface); border: 1px solid var(--line); border-radius: .6rem; padding: 1.25rem; }
        .login form { display: flex; flex-direction: column; gap: .55rem; }
        .login label { font-weight: 650; }
        .login input, textarea#edit-json { border: 1px solid var(--line); border-radius: .4rem; padding: .45rem .55rem; background: var(--bg); color: var(--text); }
        .error { color: var(--bad); }
        a { color: var(--accent); }
        button { cursor: pointer; border: 1px solid var(--accent); background: var(--accent); color: var(--on-accent); border-radius: .4rem; padding: .35rem .7rem; }
        button.secondary { background: transparent; color: var(--text); border-color: var(--line); }
        button.danger { background: var(--bad); border-color: var(--bad); color: #fff; }
        @media (prefers-color-scheme: dark) { button.danger { color: #2a0c0c; } }
        button:disabled { cursor: not-allowed; background: var(--line); color: var(--muted); border-color: var(--line); }
        button:focus-visible, a:focus-visible, input:focus-visible, textarea:focus-visible { outline: 3px solid var(--focus); outline-offset: 2px; }
        textarea#edit-json { width: 100%; box-sizing: border-box; font-family: ui-monospace, monospace; }
        #edit-result { white-space: pre-wrap; min-height: 1.2rem; }
        @media (prefers-reduced-motion: reduce) { * { scroll-behavior: auto; } }
        """;
}
