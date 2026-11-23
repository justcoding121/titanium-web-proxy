#!/usr/bin/env bash
# Packages the Alpine runtime libraries the musl CLI needs before the first start.
# Usage: alpine-deps.sh   (runs apk; intended inside alpine:3.24)
set -euo pipefail
apk add --no-cache unzip libstdc++ libgcc icu-libs numactl lttng-ust libssl3 libcrypto3 ca-certificates curl
echo "alpine deps ok"
