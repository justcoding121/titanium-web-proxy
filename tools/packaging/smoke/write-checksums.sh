#!/usr/bin/env bash
# sha256 lines for every file in a staged artifact directory.
# Usage: write-checksums.sh <dir>
set -euo pipefail
dir="${1:?dir}"
py=python3
if ! command -v python3 >/dev/null 2>&1; then
  py=python
fi
"$py" - "$dir" <<'PY'
import hashlib, pathlib, sys
root = pathlib.Path(sys.argv[1])
lines = []
for path in sorted(p for p in root.iterdir() if p.is_file() and p.name != "SHA256SUMS"):
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    lines.append(f"{digest}  {path.name}")
(root / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")
PY
