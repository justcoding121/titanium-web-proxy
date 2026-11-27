#!/usr/bin/env bash
# Compare lifecycle-proof JSON against tools/ci/expected-lifecycle.json.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
expected="${EXPECTED_LIFECYCLE:-$here/expected-lifecycle.json}"

if [[ "${1:-}" == "--self-test" ]]; then
  ps=(pwsh powershell)
  for bin in "${ps[@]}"; do
    if command -v "$bin" >/dev/null 2>&1; then
      exec "$bin" -NoProfile -File "$here/assert-lifecycle-proof.ps1" -SelfTest
    fi
  done
  echo "no powershell for assert-lifecycle-proof --self-test" >&2
  exit 2
fi

proof="${1:?lifecycle-proof.json path}"
python3 - "$expected" "$proof" <<'PY'
import json, pathlib, sys

def required(path):
    doc = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    keys = []
    for entry in doc.get("entries") or []:
        if entry.get("exception"):
            continue
        for phase in entry.get("phases") or []:
            key = f"{entry['product']}|{entry['rid']}|{entry['format']}|{phase}"
            if key not in keys:
                keys.append(key)
    return keys

exp, proof_path = sys.argv[1], sys.argv[2]
need = required(exp)
rows = json.loads(pathlib.Path(proof_path).read_text(encoding="utf-8"))
present = {
    f"{r['product']}|{r['rid']}|{r['format']}|{r['phase']}" for r in rows
}
missing = [k for k in need if k not in present]
if missing:
    print("missing lifecycle proof tuples:", file=sys.stderr)
    for m in missing:
        print(f"  {m}", file=sys.stderr)
    raise SystemExit(1)
print(f"lifecycle proof ok ({len(need)} tuples)")
PY