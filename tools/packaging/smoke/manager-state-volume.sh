#!/usr/bin/env bash
# Prove titanium-manager keeps SQLite state across image upgrades on a named volume.
set -euo pipefail
tag="${1:-titanium-manager:ci}"
vol="${MANAGER_STATE_VOLUME:-twp-manager-smoke-vol}"
token="${MANAGER_ADMIN_TOKEN:-smoke-volume-upgrade}"
fleet_name="smoke-lifecycle-fleet"
prev_ver="${PREV_RELEASE_VERSION:-}"
if [[ -z "$prev_ver" ]] && command -v gh >/dev/null 2>&1 && [[ -n "${GH_TOKEN:-}" ]]; then
  local_tag="$(gh api "repos/${GITHUB_REPOSITORY}/releases?per_page=15" \
    --jq '[.[] | select(.draft==false)][0].tag_name' 2>/dev/null || true)"
  if [[ -n "$local_tag" && "$local_tag" != "null" ]]; then
    prev_ver="${local_tag#v}"
  fi
fi
if [[ -z "$prev_ver" ]]; then
  echo "skip manager state volume (PREV_RELEASE_VERSION unset)"
  exit 0
fi
prev_image="ghcr.io/justcoding121/titanium-manager:${prev_ver}"
if ! docker pull "$prev_image" >/dev/null 2>&1; then
  echo "skip manager state volume (previous image $prev_image unavailable)"
  exit 0
fi

docker rm -f mgr-prev mgr-cand smoke-manager >/dev/null 2>&1 || true
docker volume rm -f "$vol" >/dev/null 2>&1 || true

# Match image-health.sh: writable rootfs + tmpfs for state/tmp. --read-only
# breaks older published manager images that write outside /var/lib.
docker run -d --name mgr-prev -p 17701:7700 \
  -e TITANIUM_MANAGER_ADMIN_TOKEN="$token" \
  -v "${vol}:/var/lib/titanium-manager" \
  --tmpfs /tmp \
  "$prev_image"
ok=0
for _ in $(seq 1 20); do
  if curl -fsS http://127.0.0.1:17701/v1/healthz >/dev/null 2>&1; then ok=1; break; fi
  sleep 2
done
if [[ "$ok" != "1" ]]; then
  echo "ASSERT: prev manager healthz failed ($prev_image)" >&2
  docker logs mgr-prev 2>&1 | tail -40 >&2 || true
  docker rm -f mgr-prev >/dev/null 2>&1 || true
  exit 1
fi
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer ${token}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"${fleet_name}\"}" http://127.0.0.1:17701/v1/fleets)"
if [[ "$code" != "201" && "$code" != "200" ]]; then
  echo "ASSERT: could not create fleet on prev manager (HTTP $code)" >&2
  docker logs mgr-prev 2>&1 | tail -20 >&2 || true
  docker rm -f mgr-prev >/dev/null 2>&1 || true
  exit 1
fi
docker rm -f mgr-prev >/dev/null

docker run -d --name mgr-cand -p 17702:7700 \
  -e TITANIUM_MANAGER_ADMIN_TOKEN="$token" \
  -v "${vol}:/var/lib/titanium-manager" \
  --tmpfs /tmp \
  "$tag"
ok=0
for _ in $(seq 1 20); do
  if curl -fsS http://127.0.0.1:17702/v1/healthz >/dev/null 2>&1; then ok=1; break; fi
  sleep 2
done
if [[ "$ok" != "1" ]]; then
  echo "ASSERT: candidate manager healthz failed ($tag)" >&2
  docker logs mgr-cand 2>&1 | tail -40 >&2 || true
  docker rm -f mgr-cand >/dev/null 2>&1 || true
  exit 1
fi
body="$(curl -fsS -H "Authorization: Bearer ${token}" http://127.0.0.1:17702/v1/fleets)"
echo "$body" | grep -q "$fleet_name" || { echo "ASSERT: fleet missing after candidate upgrade" >&2; echo "$body" >&2; docker logs mgr-cand 2>&1 | tail -20 >&2 || true; docker rm -f mgr-cand >/dev/null 2>&1 || true; exit 1; }
docker rm -f mgr-cand >/dev/null
docker volume rm -f "$vol" >/dev/null 2>&1 || true
echo "manager state volume upgrade ok ($prev_ver -> candidate)"