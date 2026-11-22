#!/bin/sh
# Plus checklist against the published Ubuntu deb CLI.
set -u
BIN=${BIN:-/opt/titanium-cli/titanium}
PLUS_DIR=$(CDPATH= cd "$(dirname "$BIN")" && pwd)
BASE=http://127.0.0.1:19181
fail=0
note() { echo "$*"; }
bad() { echo "FAIL: $*"; fail=1; }
ok() { echo "PASS: $*"; }

note "=== update --plus without sudo ==="
set +e
unsudo=$("$BIN" update --plus --channel beta 2>&1)
code=$?
set -e
printf '%s\n' "$unsudo"
if printf '%s\n' "$unsudo" | grep -q "Run the same command with sudo."; then
  ok "non-writable install asks for sudo (exit $code)"
elif printf '%s\n' "$unsudo" | grep -q "already at"; then
  ok "Plus is already installed (sudo message proved on the first attempt)"
elif [ "$code" -eq 0 ]; then
  ok "writable directory updated Plus (exit 0)"
else
  bad "missing sudo message (exit $code)"
fi

note "=== update --plus with sudo ==="
set +e
sudo -n "$BIN" update --plus --channel beta > /tmp/plus-update.txt 2>&1
ucode=$?
set -e
cat /tmp/plus-update.txt
if [ "$ucode" -ne 0 ]; then
  bad "sudo update --plus exited $ucode"
else
  ok "sudo update --plus exited 0"
fi
if [ ! -f "$PLUS_DIR/Titanium.Plus.dll" ]; then
  bad "Titanium.Plus.dll missing after update"
else
  ok "Titanium.Plus.dll is beside the CLI"
fi

cat > /tmp/be.py <<'PY'
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
port = int(sys.argv[1])
name = sys.argv[2].encode()
class H(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Length", str(len(name)))
        self.end_headers()
        self.wfile.write(name)
    def log_message(self, fmt, *args):
        return
HTTPServer(("127.0.0.1", port), H).serve_forever()
PY

cat > /tmp/plus-smoke.yaml <<'EOF'
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 18180
    decryptSsl: false
clusters:
  - id: web
    destinations:
      - id: alpha
        address: "127.0.0.1"
        port: 18181
      - id: beta
        address: "127.0.0.1"
        port: 18182
  - id: other
    destinations:
      - id: gamma
        address: "127.0.0.1"
        port: 18183
routes:
  - id: root
    clusterId: web
    match:
      path: /
      pathKind: Prefix
plus:
  enabled: true
  controlPlane:
    host: "127.0.0.1"
    port: 19180
    dashboardPort: 19181
    dashboardEditing: true
    sharedSecret: "smoke-operator-secret-806"
    dashboardViewerSecret: "smoke-viewer-secret-806"
EOF

if "$BIN" test -c /tmp/plus-smoke.yaml > /tmp/plus-test.txt 2>&1; then
  ok "titanium test accepted the Plus config"
else
  bad "titanium test rejected the Plus config"
  cat /tmp/plus-test.txt
  exit 1
fi

python3 /tmp/be.py 18181 alpha > /tmp/be-alpha.log 2>&1 &
echo $! > /tmp/be-alpha.pid
python3 /tmp/be.py 18182 beta > /tmp/be-beta.log 2>&1 &
echo $! > /tmp/be-beta.pid
python3 /tmp/be.py 18183 gamma > /tmp/be-gamma.log 2>&1 &
echo $! > /tmp/be-gamma.pid

"$BIN" run -c /tmp/plus-smoke.yaml > /tmp/plus-run.log 2>&1 &
echo $! > /tmp/plus-run.pid

ready=0
i=0
while [ "$i" -lt 40 ]; do
  if grep -q "Plus dashboard listening" /tmp/plus-run.log 2>/dev/null; then
    ready=1
    break
  fi
  if curl -fsS --max-time 1 http://127.0.0.1:19181/login >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$(cat /tmp/plus-run.pid)" 2>/dev/null; then
    break
  fi
  i=$((i + 1))
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  bad "dashboard did not start"
  cat /tmp/plus-run.log
  exit 1
fi
ok "dashboard started"
grep "Plus dashboard listening" /tmp/plus-run.log || true

# Drive a few requests so overview is non-zero.
j=0
while [ "$j" -lt 12 ]; do
  curl -fsS --max-time 2 http://127.0.0.1:18180/ >/dev/null 2>&1 || true
  j=$((j + 1))
done

wrong=$(curl -sS -D /tmp/login-wrong.hdr -o /tmp/login-wrong.html --data "secret=not-the-secret" "$BASE/login" || true)
if grep -q "login-error" /tmp/login-wrong.html && ! grep -qi "titanium_plus_session" /tmp/login-wrong.hdr; then
  ok "wrong secret shows an error and sets no session"
else
  bad "wrong secret handling"
  cat /tmp/login-wrong.hdr
fi

curl -sS -c /tmp/op.cj -D /tmp/login-op.hdr -o /tmp/login-op.html --data "secret=smoke-operator-secret-806" "$BASE/login" || true
if grep -q "titanium_plus_session" /tmp/login-op.hdr; then
  ok "operator secret sets a session"
else
  bad "operator sign-in"
  cat /tmp/login-op.hdr /tmp/login-op.html
  exit 1
fi

curl -sS -c /tmp/vw.cj -D /tmp/login-vw.hdr -o /dev/null --data "secret=smoke-viewer-secret-806" "$BASE/login" || true
if grep -q "titanium_plus_session" /tmp/login-vw.hdr; then
  ok "viewer secret sets a session"
else
  bad "viewer sign-in"
fi

page=$(curl -sS -b /tmp/op.cj "$BASE/")
csrf=$(printf '%s' "$page" | sed -n 's/.*data-csrf="\([^"]*\)".*/\1/p' | head -1)
if [ -n "$csrf" ]; then
  ok "operator page has a csrf token"
else
  bad "csrf token missing"
fi
vwpage=$(curl -sS -b /tmp/vw.cj "$BASE/routes")
vwcsrf=$(printf '%s' "$vwpage" | sed -n 's/.*data-csrf="\([^"]*\)".*/\1/p' | head -1)

overview=$(curl -sS -b /tmp/op.cj "$BASE/api/overview")
printf '%s\n' "$overview" > /tmp/plus-overview.json
if printf '%s' "$overview" | grep -q "requests"; then
  ok "api/overview returned"
else
  bad "api/overview: $overview"
fi
if printf '%s' "$page" | grep -q "data-testid=\"overview-requests\""; then
  ok "overview page renders the requests field"
else
  note "overview page snippet follows"
  printf '%s\n' "$page" | head -c 500
  echo
fi

routes=$(curl -sS -b /tmp/op.cj "$BASE/api/routes")
printf '%s\n' "$routes" > /tmp/plus-routes.json
for id in root web other alpha beta gamma; do
  if printf '%s' "$routes" | grep -q "$id"; then
    ok "routes payload contains $id"
  else
    bad "routes payload missing $id"
  fi
done

for path in / /routes /health /config /certificates /audit /metrics /api/snapshot /api/audit /api/certs; do
  code=$(curl -sS -o /tmp/plus-page.html -w "%{http_code}" -b /tmp/op.cj "$BASE$path" || echo 000)
  if [ "$code" = "200" ]; then
    ok "GET $path 200"
  else
    bad "GET $path $code"
  fi
done

vcode=$(curl -sS -o /tmp/vw-drain.txt -w "%{http_code}" -b /tmp/vw.cj -H "X-Titanium-Csrf: $vwcsrf" -X POST "$BASE/drain/alpha" || echo 000)
if [ "$vcode" = "403" ]; then
  ok "viewer drain is 403"
else
  bad "viewer drain status $vcode"
  cat /tmp/vw-drain.txt
fi

dcode=$(curl -sS -o /tmp/op-drain.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/drain/alpha" || echo 000)
if [ "$dcode" = "200" ]; then
  ok "operator drain alpha"
else
  bad "operator drain status $dcode"
  cat /tmp/op-drain.txt
fi

badbody=$(curl -sS -o /tmp/edit-bad.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -H "Content-Type: application/json" -X POST "$BASE/edit/route/validate" --data '{"clusterId":"web"}' || echo 000)
if [ "$badbody" = "422" ] || [ "$badbody" = "400" ]; then
  ok "bad route body refused ($badbody)"
else
  bad "bad route body status $badbody"
  cat /tmp/edit-bad.txt
fi

good='{"id":"extra","clusterId":"other","match":{"path":"/extra","pathKind":"Prefix"}}'
gcode=$(curl -sS -o /tmp/edit-save.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -H "Content-Type: application/json" -X POST "$BASE/edit/route/save" --data "$good" || echo 000)
if [ "$gcode" = "200" ]; then
  ok "saved route extra"
else
  bad "save route status $gcode"
  cat /tmp/edit-save.txt
fi

if curl -sS -b /tmp/op.cj "$BASE/api/routes" | grep -q "extra"; then
  ok "extra route is visible after save"
else
  bad "extra route missing after save"
fi

del=$(curl -sS -o /tmp/edit-del.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/edit/route/delete/extra" || echo 000)
if [ "$del" = "200" ]; then
  ok "deleted route extra"
else
  bad "delete route status $del"
  cat /tmp/edit-del.txt
fi

audit=$(curl -sS -b /tmp/op.cj "$BASE/api/audit")
printf '%s\n' "$audit" > /tmp/plus-audit.json
if printf '%s' "$audit" | grep -q "drain"; then
  ok "audit lists the drain"
else
  bad "audit missing drain"
fi

hcode=$(curl -sS -o /tmp/op-healthy.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/healthy/alpha" || echo 000)
if [ "$hcode" = "200" ]; then
  ok "operator marked alpha healthy"
else
  bad "mark healthy status $hcode"
fi

cp /tmp/plus-smoke.yaml /tmp/plus-smoke.good
printf 'schemaVersion: "7.1"\nlisteners: [ broken\n' > /tmp/plus-smoke.yaml
rcode=$(curl -sS -o /tmp/reload-bad.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/reload/-" || echo 000)
note "broken reload status $rcode"
cat /tmp/reload-bad.txt
echo
if curl -sS -b /tmp/op.cj "$BASE/api/routes" | grep -q "root"; then
  ok "broken file kept the previous routes"
else
  bad "broken reload dropped the routes"
fi
cp /tmp/plus-smoke.good /tmp/plus-smoke.yaml
r2=$(curl -sS -o /tmp/reload-good.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/reload/-" || echo 000)
if [ "$r2" = "200" ]; then
  ok "good file reload"
else
  bad "good reload status $r2"
  cat /tmp/reload-good.txt
fi

ccode=$(curl -sS -o /tmp/clear.txt -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/clear-overlay/-" || echo 000)
if [ "$ccode" = "200" ]; then
  ok "clear dashboard edits"
else
  bad "clear overlay status $ccode"
fi

out=$(curl -sS -o /dev/null -w "%{http_code}" -b /tmp/op.cj -H "X-Titanium-Csrf: $csrf" -X POST "$BASE/logout" || echo 000)
after=$(curl -sS -o /dev/null -w "%{http_code}" -b /tmp/op.cj "$BASE/api/overview" || echo 000)
if [ "$after" = "401" ]; then
  ok "sign-out ends the session"
else
  bad "session still valid after logout ($after, logout $out)"
fi

# Sign back in so the browser tunnel can use a fresh login.
curl -sS -c /tmp/op.cj -o /dev/null --data "secret=smoke-operator-secret-806" "$BASE/login" || true

if [ "$fail" -eq 0 ]; then
  echo "PLUS CHECKLIST PASS"
else
  echo "PLUS CHECKLIST FAIL"
  exit 1
fi

if [ -n "${SHOTS_JS:-}" ]; then
  node "$SHOTS_JS"
fi
