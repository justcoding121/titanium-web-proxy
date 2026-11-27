#!/usr/bin/env bash
# Append one lifecycle-proof row to a JSON array (create the file if missing).
# Usage: append-lifecycle-proof.sh <proof.json> <product> <rid> <format> <phase> \
#          <from_version> <to_version> <installed_version_after> <state_restored>
set -euo pipefail

if [[ $# -ne 9 ]]; then
  echo "usage: append-lifecycle-proof.sh <proof.json> <product> <rid> <format> <phase> <from_version> <to_version> <installed_version_after> <state_restored>" >&2
  exit 2
fi

python3 - "$@" <<'PY'
import json, pathlib, sys
path = pathlib.Path(sys.argv[1])
row = {
    "product": sys.argv[2],
    "rid": sys.argv[3],
    "format": sys.argv[4],
    "phase": sys.argv[5],
    "from_version": sys.argv[6],
    "to_version": sys.argv[7],
    "installed_version_after": sys.argv[8],
    "state_restored": sys.argv[9] == "true",
}
rows = []
if path.is_file() and path.stat().st_size > 0:
    rows = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(rows, list):
        raise SystemExit(f"{path}: expected a JSON array")
rows.append(row)
path.parent.mkdir(parents=True, exist_ok=True)
path.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
print(f"appended {row['product']}/{row['rid']}/{row['format']}/{row['phase']}")
PY