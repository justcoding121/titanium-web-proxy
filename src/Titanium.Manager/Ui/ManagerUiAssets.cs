namespace Titanium.Manager.Ui;

/// <summary>
///     Manager UI script and stylesheet served from <c>/assets/</c> so pages need no inline script or style
///     (Content-Security-Policy <c>default-src 'self'</c>).
/// </summary>
internal static class ManagerUiAssets
{
    public const string ScriptPath = "/assets/manager.js";
    public const string StylePath = "/assets/manager.css";

    public const string Script = """
        (function () {
          'use strict';
          var body = document.body;
          var view = body.getAttribute('data-view') || '';
          var csrf = body.getAttribute('data-csrf') || '';
          var fleetId = body.getAttribute('data-fleet-id') || '';
          var canEdit = body.getAttribute('data-role') === 'editor' || body.getAttribute('data-role') === 'admin';
          var flash = document.getElementById('flash');

          function say(text) { if (flash) { flash.textContent = text; } }

          function api(path, opts) {
            opts = opts || {};
            var headers = opts.headers || {};
            headers['X-Titanium-Csrf'] = csrf;
            if (opts.json) {
              headers['Content-Type'] = 'application/json';
            }
            return fetch(path, {
              method: opts.method || 'GET',
              credentials: 'same-origin',
              headers: headers,
              body: opts.body
            }).then(function (r) {
              return r.text().then(function (text) {
                var data = null;
                try { data = text ? JSON.parse(text) : null; } catch (e) { data = text; }
                return { ok: r.ok, status: r.status, data: data, text: text };
              });
            });
          }

          function yamlBox() { return document.getElementById('draft-yaml'); }

          function refreshEditorLists(summary) {
            var routes = document.getElementById('route-list');
            var clusters = document.getElementById('cluster-list');
            if (routes && summary && summary.routes) {
              routes.replaceChildren();
              summary.routes.forEach(function (r) {
                var li = document.createElement('li');
                li.setAttribute('data-testid', 'route-item-' + r.id);
                var b = document.createElement('button');
                b.type = 'button';
                b.setAttribute('data-act', 'select-route');
                b.setAttribute('data-id', r.id);
                b.textContent = r.id;
                var span = document.createElement('span');
                span.className = 'muted';
                span.textContent = ' ' + (r.path || '') + ' → ' + (r.clusterId || '');
                li.appendChild(b);
                li.appendChild(span);
                routes.appendChild(li);
              });
            }
            if (clusters && summary && summary.clusters) {
              clusters.replaceChildren();
              summary.clusters.forEach(function (c) {
                var li = document.createElement('li');
                li.setAttribute('data-testid', 'cluster-item-' + c.id);
                li.textContent = c.id + ' (' + c.destinationCount + ' dest)';
                clusters.appendChild(li);
              });
            }
          }

          function patchDraft(op, fields) {
            var box = yamlBox();
            if (!box) { return Promise.resolve(); }
            var payload = { yaml: box.value, op: op };
            Object.keys(fields || {}).forEach(function (k) { payload[k] = fields[k]; });
            return api('/ui/draft/patch', { method: 'POST', json: true, body: JSON.stringify(payload) })
              .then(function (r) {
                if (!r.ok) {
                  say('Draft patch failed (' + r.status + '): ' + (r.data && r.data.error ? r.data.error : r.text));
                  return;
                }
                box.value = r.data.yaml || '';
                refreshEditorLists(r.data.summary);
                say('Draft updated.');
              });
          }

          function showResult(obj) {
            var el = document.getElementById('editor-result');
            if (!el) { return; }
            el.textContent = typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2);
          }

          function validateDraft() {
            var box = yamlBox();
            if (!box || !fleetId) { return; }
            return api('/v1/fleets/' + fleetId + '/validate', {
              method: 'POST',
              json: true,
              body: JSON.stringify({ yaml: box.value })
            }).then(function (r) {
              showResult(r.data || r.text);
              say(r.ok ? 'Validation ok.' : 'Validation failed.');
            });
          }

          function diffPublished() {
            var box = yamlBox();
            if (!box || !fleetId) { return; }
            return api('/ui/fleets/' + fleetId + '/diff-draft', {
              method: 'POST',
              json: true,
              body: JSON.stringify({ yaml: box.value })
            }).then(function (r) {
              showResult(r.data && r.data.unified ? r.data.unified : (r.data || r.text));
              say(r.ok ? 'Diff ready.' : 'Diff failed (' + r.status + ').');
            });
          }

          function publishDraft() {
            var box = yamlBox();
            if (!box || !fleetId || !canEdit) { return; }
            return api('/v1/fleets/' + fleetId + '/revisions', {
              method: 'POST',
              json: true,
              body: JSON.stringify({ yaml: box.value })
            }).then(function (r) {
              if (!r.ok) {
                showResult(r.data || r.text);
                say('Create revision failed (' + r.status + ').');
                return null;
              }
              var revId = r.data.id;
              return api('/v1/fleets/' + fleetId + '/revisions/' + revId + '/publish', {
                method: 'POST',
                json: true,
                body: '{}'
              }).then(function (p) {
                showResult(p.data || p.text);
                if (p.ok) {
                  say('Published revision ' + revId + ' (rollout ' + (p.data && p.data.id ? p.data.id : '') + ').');
                  window.setTimeout(function () {
                    window.location.href = '/fleets/' + fleetId + '/rollouts/' + (p.data && p.data.id ? p.data.id : '');
                  }, 400);
                } else {
                  say('Publish failed (' + p.status + ').');
                }
              });
            });
          }

          function rolloutAction(act, id) {
            if (!fleetId || !id) { return; }
            return api('/v1/fleets/' + fleetId + '/rollouts/' + id + '/' + act, {
              method: 'POST',
              json: true,
              body: '{}'
            }).then(function (r) {
              say(r.ok ? (act + ' ok') : (act + ' failed (' + r.status + '): ' + r.text));
              if (r.ok) { window.location.reload(); }
            });
          }

          document.addEventListener('click', function (event) {
            var target = event.target;
            if (!target || !target.closest) { return; }
            if (target.closest('#logout')) {
              api('/logout', { method: 'POST' }).then(function () { window.location.href = '/login'; });
              return;
            }
            var b = target.closest('button[data-act]');
            if (!b || b.disabled) { return; }
            var act = b.getAttribute('data-act');
            var id = b.getAttribute('data-id') || '';
            if (act === 'validate') { validateDraft(); return; }
            if (act === 'diff-published') { diffPublished(); return; }
            if (act === 'publish') { publishDraft(); return; }
            if (act === 'pause-rollout') { rolloutAction('pause', id); return; }
            if (act === 'resume-rollout') { rolloutAction('resume', id); return; }
            if (act === 'rollback-rollout') { rolloutAction('rollback', id); return; }
            if (act === 'select-route') {
              var rid = document.getElementById('route-id');
              if (rid) { rid.value = id; }
              return;
            }
            if (act === 'upsert-route') {
              patchDraft('upsert-route', {
                id: (document.getElementById('route-id') || {}).value || '',
                path: (document.getElementById('route-path') || {}).value || '',
                clusterId: (document.getElementById('route-cluster') || {}).value || ''
              });
              return;
            }
            if (act === 'remove-route') {
              patchDraft('remove-route', { id: (document.getElementById('route-id') || {}).value || '' });
              return;
            }
            if (act === 'upsert-cluster') {
              patchDraft('upsert-cluster', { id: (document.getElementById('cluster-id') || {}).value || '' });
              return;
            }
            if (act === 'remove-cluster') {
              patchDraft('remove-cluster', { id: (document.getElementById('cluster-id') || {}).value || '' });
            }
          });

          var createFleet = document.getElementById('create-fleet');
          if (createFleet) {
            createFleet.addEventListener('submit', function (e) {
              e.preventDefault();
              var name = (document.getElementById('fleet-name') || {}).value || '';
              api('/ui/fleets', {
                method: 'POST',
                json: true,
                body: JSON.stringify({ name: name })
              }).then(function (r) {
                if (r.ok && r.data && r.data.id) {
                  window.location.href = '/fleets/' + r.data.id;
                } else {
                  say('Create fleet failed (' + r.status + '): ' + r.text);
                }
              });
            });
          }
        })();
        """;

    public const string Style = """
        :root { color-scheme: light dark; --line: #8886; --bad: #c0392b; --muted: #666; }
        body { font-family: system-ui, sans-serif; margin: 1.25rem; }
        header { display: flex; flex-wrap: wrap; align-items: center; gap: 1rem; }
        header h1 { margin: 0; font-size: 1.4rem; }
        nav { display: flex; flex-wrap: wrap; gap: .75rem; flex: 1; }
        nav a[aria-current="page"] { font-weight: 700; text-decoration: none; }
        table { border-collapse: collapse; width: 100%; margin-bottom: 1.5rem; }
        th, td { border: 1px solid var(--line); padding: .35rem .55rem; text-align: left; vertical-align: top; overflow-wrap: anywhere; }
        .facts { display: grid; grid-template-columns: max-content 1fr; gap: .35rem 1rem; }
        .facts dt { font-weight: 600; }
        .facts dd { margin: 0; overflow-wrap: anywhere; }
        #flash { min-height: 1.2rem; }
        .login main { max-width: 22rem; margin: 6rem auto; }
        .login form { display: flex; flex-direction: column; gap: .6rem; }
        .error { color: var(--bad); }
        .muted { color: var(--muted); font-size: .9rem; }
        .hint { opacity: .8; font-size: .9rem; }
        .inline-form { display: flex; flex-wrap: wrap; gap: .5rem; align-items: end; margin-bottom: 1rem; }
        .editor-grid { display: grid; grid-template-columns: 1fr minmax(14rem, 18rem); gap: 1rem; }
        @media (max-width: 900px) { .editor-grid { grid-template-columns: 1fr; } }
        #draft-yaml { width: 100%; min-height: 28rem; font-family: ui-monospace, monospace; font-size: .85rem; }
        .diff, .result { white-space: pre-wrap; background: #0001; padding: .75rem; overflow: auto; max-height: 24rem; }
        .stub-form { display: flex; flex-direction: column; gap: .35rem; margin: .75rem 0 1.25rem; }
        tr.drift td { color: var(--bad); }
        button { cursor: pointer; }
        button:disabled { cursor: not-allowed; }
        .actions { margin: .75rem 0; }
        """;
}
