#!/usr/bin/env bash
# Load bundled libmsquic, then the host libssl, inside a distro image.
# This is the startup sequence that aborts when a bundled libcrypto is older
# than the host libssl ("No usable version of libssl was found").
set -euo pipefail

PUBLISH_DIR="${1:?publish dir}"
IMAGE="${2:?container image}"
KIND="${3:?apt or apk}"

PUBLISH_DIR="$(cd "${PUBLISH_DIR}" && pwd)"

if compgen -G "${PUBLISH_DIR}/libssl.so*" >/dev/null || compgen -G "${PUBLISH_DIR}/libcrypto.so*" >/dev/null; then
  echo "error: ${PUBLISH_DIR} still contains OpenSSL (libssl/libcrypto must come from the host)" >&2
  exit 1
fi
if ! compgen -G "${PUBLISH_DIR}/libmsquic.so*" >/dev/null; then
  echo "error: ${PUBLISH_DIR} is missing libmsquic.so*" >&2
  exit 1
fi

case "${KIND}" in
  apt)
    SETUP='apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq python3 libnuma1 libssl3 || DEBIAN_FRONTEND=noninteractive apt-get install -y -qq python3 libnuma1 libssl3t64'
    ;;
  apk)
    SETUP='apk add --no-cache python3 numactl lttng-ust libssl3 libcrypto3'
    ;;
  *)
    echo "error: kind must be apt or apk (got ${KIND})" >&2
    exit 1
    ;;
esac

echo "verify-native-load: ${IMAGE} (${KIND}) against ${PUBLISH_DIR}"
docker run --rm \
  -v "${PUBLISH_DIR}:/publish:ro" \
  "${IMAGE}" \
  sh -c "${SETUP} && python3 - <<'PY'
import ctypes, glob, sys
hits = [p for p in glob.glob('/publish/libmsquic.so*') if 'lttng' not in p]
prefer = [p for p in hits if p.endswith('libmsquic.so.2')]
path = (prefer or hits)[0]
ctypes.CDLL(path, mode=ctypes.RTLD_GLOBAL)
ctypes.CDLL('libssl.so.3')
print('native load ok', path)
PY"
