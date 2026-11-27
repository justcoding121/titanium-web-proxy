#!/bin/bash
set -euo pipefail
if [ -x /setup.sh ]; then
  /setup.sh
fi
if [ "${ROLE:-server}" = "client" ]; then
  echo "This endpoint is a server." >&2
  exit 127
fi
case "${TESTCASE:-handshake}" in
  handshake|multiconnect) ;;
  *)
    echo "unsupported test case: ${TESTCASE}" >&2
    exit 127
    ;;
esac
exec /endpoint/QuicInteropHost --www /www --certs /certs --port 443
