#!/usr/bin/env bash
# Load the amd64 slice of a smoked OCI archive and run one process health check.
# Manager answers /v1/healthz. The proxy and gateway images have no shell; the
# chart install is their runtime proof, so here they only have to start.
# Usage: image-health.sh <image.oci.tar> <local-tag> <kind>
# kind is proxy, gateway, or manager.
set -euo pipefail
oci="${1:?oci}"
tag="${2:?tag}"
kind="${3:?kind}"
if [[ "$oci" == "-" ]]; then
  docker image inspect "$tag" >/dev/null
else
  skopeo copy --override-os linux --override-arch amd64 "oci-archive:$oci" "docker-daemon:$tag"
fi
case "$kind" in
  manager)
    docker rm -f smoke-manager >/dev/null 2>&1 || true
    docker run -d --name smoke-manager -p 7700:7700 \
      -e TITANIUM_MANAGER_ADMIN_TOKEN=smoke \
      --tmpfs /var/lib/titanium-manager --tmpfs /tmp \
      "$tag"
    ok=0
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
      if curl -fsS http://127.0.0.1:7700/v1/healthz >/dev/null 2>&1; then
        ok=1
        break
      fi
      sleep 2
    done
    docker logs smoke-manager || true
    docker rm -f smoke-manager >/dev/null 2>&1 || true
    test "$ok" = "1"
    ;;
  proxy|gateway)
    cid="$(docker create "$tag")"
    docker rm "$cid" >/dev/null
    echo "$kind image loads"
    ;;
esac
