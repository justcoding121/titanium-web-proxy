#!/usr/bin/env bash
# Classify a smoke log as infra (runner, network, kind node pull) or assertion
# (the product did the wrong thing). Unknown failures are assertions so a new
# product bug is not retried away.
smoke_classify() {
  local code="$1"
  local log="$2"
  local text
  text="$(tr '[:upper:]' '[:lower:]' < "$log" || true)"
  if grep -q 'assert:' <<< "$text"; then
    echo assertion
    return 0
  fi
  local infra_re='hdiutil: .*busy|resource temporarily unavailable|could not resolve host|temporary failure in name resolution|connection reset|connection timed out|tls handshake timeout|failed to download|failed to pull|kind:.*node image|econnreset|dial tcp|curl: \(56\)|http 429|apt-get update.*failed|no space left on device|brew: .*download|context deadline exceeded'
  if grep -Eq "$infra_re" <<< "$text"; then
    echo infra
    return 0
  fi
  echo assertion
  return 0
}
