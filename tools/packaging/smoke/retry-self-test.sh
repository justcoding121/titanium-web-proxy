#!/usr/bin/env bash
# Proves the retry classifier: infra is retried, a plain assertion is not,
# and a GUI assertion gets one retry that can pass as flaky.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
fail=0
tmp="$(mktemp -d)"
rm -f "${TMPDIR:-/tmp}/smoke-retry-infra-count" "${TMPDIR:-/tmp}/smoke-retry-gui-count"

run_case() {
  local name="$1" expect="$2"
  shift 2
  set +e
  "$@" >"$tmp/out" 2>&1
  local code=$?
  set -e
  if [[ "$code" -ne "$expect" ]]; then
    echo "FAIL $name exit=$code expected=$expect"
    cat "$tmp/out"
    fail=1
  else
    echo "PASS $name"
  fi
}

run_case infra-recovers 0 bash "$here/retry.sh" --leg assert -- bash -c '
  n="${TMPDIR:-/tmp}/smoke-retry-infra-count"
  c=0
  [[ -f "$n" ]] && c=$(cat "$n")
  c=$((c + 1))
  echo "$c" > "$n"
  if [[ "$c" -lt 2 ]]; then
    echo "Could not resolve host example.invalid" >&2
    exit 1
  fi
  exit 0
'

run_case assertion-no-retry 7 bash "$here/retry.sh" --leg assert -- bash -c '
  echo "ASSERT: version mismatch" >&2
  exit 7
'

run_case gui-retry-passes 0 bash "$here/retry.sh" --leg gui -- bash -c '
  n="${TMPDIR:-/tmp}/smoke-retry-gui-count"
  c=0
  [[ -f "$n" ]] && c=$(cat "$n")
  c=$((c + 1))
  echo "$c" > "$n"
  if [[ "$c" -lt 2 ]]; then
    echo "ASSERT: did not produce a decrypted GET" >&2
    exit 1
  fi
  exit 0
'

rm -f "${TMPDIR:-/tmp}/smoke-retry-infra-count" "${TMPDIR:-/tmp}/smoke-retry-gui-count"
rm -rf "$tmp"
exit "$fail"
