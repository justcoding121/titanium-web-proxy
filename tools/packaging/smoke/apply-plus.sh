#!/usr/bin/env bash
# Apply a Plus zip the way a user does: titanium update --plus against a local feed.
# Then prove Plus loaded and a Plus command runs.
# Usage: apply-plus.sh <cli-dir> <plus-zip> <titanium-bin>
set -euo pipefail
cli_dir="${1:?cli dir}"
plus_zip="${2:?plus zip}"
bin="${3:?titanium}"
cli_dir="$(cd "$cli_dir" && pwd)"
plus_zip="$(cd "$(dirname "$plus_zip")" && pwd)/$(basename "$plus_zip")"

if [[ ! -f "$plus_zip" ]]; then
  echo "ASSERT: Plus zip missing" >&2
  exit 1
fi

feed="$(mktemp -d)"
cp "$plus_zip" "$feed/plus.zip"
size="$(wc -c < "$feed/plus.zip" | tr -d ' ')"
sha="$(sha256sum "$feed/plus.zip" | awk '{print $1}')"
python3 - "$feed" "$size" "$sha" <<'PY'
import json, sys
feed, size, sha = sys.argv[1:]
manifest = {
  "version": "0.0.0-smoke",
  "channel": "beta",
  "products": {
    "plus": {
      "version": "0.0.0-smoke",
      "bundle": {
        "url": "http://127.0.0.1:9/plus.zip",
        "sha256": sha,
        "size": int(size),
      },
    }
  },
}
open(feed + "/release-manifest.json", "w", encoding="utf-8").write(json.dumps(manifest))
PY

port=8765
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$feed" >/tmp/plus-feed.log 2>&1 &
httpd=$!
trap 'kill "$httpd" 2>/dev/null || true; rm -rf "$feed"' EXIT
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if curl -fsS "http://127.0.0.1:$port/release-manifest.json" >/dev/null 2>&1; then
    break
  fi
  sleep 0.2
done
python3 - "$feed/release-manifest.json" "$port" <<'PY'
import json, sys
path, port = sys.argv[1:]
doc = json.load(open(path, encoding="utf-8"))
doc["products"]["plus"]["bundle"]["url"] = f"http://127.0.0.1:{port}/plus.zip"
json.dump(doc, open(path, "w", encoding="utf-8"))
PY

TITANIUM_UPDATE_FEED="http://127.0.0.1:$port/release-manifest.json" \
  "$bin" update --plus --channel beta

if [[ ! -f "$cli_dir/Titanium.Plus.dll" ]]; then
  echo "ASSERT: Titanium.Plus.dll was not applied beside the CLI" >&2
  exit 1
fi
if [[ -f "$cli_dir/Titanium.Plus.files" ]]; then
  while IFS= read -r name || [[ -n "$name" ]]; do
    name="${name//$'\r'/}"
    [[ -z "$name" ]] && continue
    case "$name" in
      titanium|titanium.exe|twp|twp.exe)
        echo "ASSERT: Plus bundle collides with MIT CLI file $name" >&2
        exit 1
        ;;
    esac
    if [[ ! -f "$cli_dir/$name" ]]; then
      echo "ASSERT: Plus file list entry missing after apply: $name" >&2
      exit 1
    fi
  done < "$cli_dir/Titanium.Plus.files"
fi

ver="$("$bin" version)"
echo "$ver"
if ! grep -Eq 'Plus: [0-9]' <<< "$ver"; then
  echo "ASSERT: titanium version did not report a loaded Plus build" >&2
  exit 1
fi
hash_out="$("$bin" plus hash-password --user alice --role operator smoke-secret)"
echo "$hash_out"
if ! grep -q '^alice:operator:' <<< "$hash_out"; then
  echo "ASSERT: plus hash-password did not print a user line" >&2
  exit 1
fi
echo "plus apply ok"
