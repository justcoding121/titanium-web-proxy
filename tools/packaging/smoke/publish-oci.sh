#!/usr/bin/env bash
# Push the OCI layout that product-smoke already loaded, then fail if the
# registry digest is not the digest of that layout.
# Usage: publish-oci.sh <image.oci.tar> <tags-file>
# tags-file is one image reference per line (ghcr.io/org/name:tag).
# Set SKOPEO_INSECURE=1 for a local HTTP registry (publish-rehearsal).
set -euo pipefail
oci="${1:?oci tar}"
tags="${2:?tags file}"

index_digest() {
  python3 - "$1" <<'PY'
import json, sys, tarfile
path = sys.argv[1]
with tarfile.open(path) as tar:
    index = None
    for member in tar.getmembers():
        name = member.name.lstrip("./")
        if name == "index.json":
            index = json.load(tar.extractfile(member))
            break
    if index is None:
        sys.exit("oci archive has no index.json")
    manifests = index.get("manifests") or []
    if len(manifests) != 1:
        sys.exit(f"expected one index manifest, found {len(manifests)}")
    print(manifests[0]["digest"])
PY
}

skopeo_args=(--all)
inspect_tls=()
if [[ "${SKOPEO_INSECURE:-}" == "1" ]]; then
  skopeo_args+=(--dest-tls-verify=false --src-tls-verify=false)
  inspect_tls=(--tls-verify=false)
fi

remote_digest() {
  local ref="$1"
  python3 - "$ref" "${inspect_tls[*]-}" <<'PY'
import hashlib, subprocess, sys
ref = sys.argv[1]
extra = [a for a in sys.argv[2].split() if a] if len(sys.argv) > 2 and sys.argv[2] else []
cmd = ["skopeo", "inspect", "--raw", *extra, f"docker://{ref}"]
raw = subprocess.check_output(cmd)
if raw.endswith(b"\n"):
    raw = raw[:-1]
print("sha256:" + hashlib.sha256(raw).hexdigest())
PY
}

local="$(index_digest "$oci")"
echo "smoked index $local"
first=""
while IFS= read -r tag || [[ -n "$tag" ]]; do
  [[ -z "$tag" ]] && continue
  if [[ -z "$first" ]]; then
    skopeo copy "${skopeo_args[@]}" "oci-archive:$oci" "docker://$tag"
    first="$tag"
    pushed="$(remote_digest "$tag")"
    echo "pushed $tag $pushed"
    if [[ "$pushed" != "$local" ]]; then
      echo "ASSERT: pushed digest $pushed != smoked digest $local" >&2
      exit 1
    fi
  else
    skopeo copy "${skopeo_args[@]}" "docker://$first" "docker://$tag"
  fi
done < "$tags"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "digest=$local" >> "$GITHUB_OUTPUT"
fi
echo "published $local"
