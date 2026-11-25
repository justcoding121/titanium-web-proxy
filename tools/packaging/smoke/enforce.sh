#!/usr/bin/env bash
# Soft-to-hard lifecycle enforcement wrapper.
# Usage: enforce.sh <name> -- <cmd...>
# If LIFECYCLE_ENFORCE is not "true", failures become ::warning + step summary row + exit 0.
# If LIFECYCLE_ENFORCE=true, failures exit non-zero.
#
# Always persists the wrapped command's stdout/stderr under
# $RUNNER_TEMP/product-smoke/logs/<name>.log (and copies known helper logs) so the
# product-smoke artifact can show the failing assertion when job logs are unavailable.
set -euo pipefail

smoke_log_dir() {
  local root="${RUNNER_TEMP:-/tmp}/product-smoke"
  mkdir -p "$root/logs"
  printf '%s\n' "$root"
}

write_summary() {
  local name="$1" code="$2" detail="$3"
  local row="| \`$name\` | lifecycle | exit $code | $detail |"
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    if ! grep -q '### lifecycle enforcement (advisory)' "$GITHUB_STEP_SUMMARY" 2>/dev/null; then
      {
        echo "### lifecycle enforcement (advisory)"
        echo ""
        echo "| step | class | exit | detail |"
        echo "| --- | --- | --- | --- |"
      } >> "$GITHUB_STEP_SUMMARY"
    fi
    echo "$row" >> "$GITHUB_STEP_SUMMARY"
  fi
}

copy_helper_logs() {
  local dest="$1"
  mkdir -p "$dest"
  for f in /tmp/update-feed.log /tmp/update-proxy.log /tmp/twp-origin-smoke.log; do
    if [[ -f "$f" ]]; then
      cp -f "$f" "$dest/$(basename "$f")" 2>/dev/null || true
    fi
  done
}

run_enforce() {
  local name="${1:?name}"
  shift
  if [[ "${1:-}" != "--" ]]; then
    echo "usage: enforce.sh <name> -- <cmd...>" >&2
    exit 2
  fi
  shift
  if [[ "$#" -lt 1 ]]; then
    echo "usage: enforce.sh <name> -- <cmd...>" >&2
    exit 2
  fi

  local root log helpers
  root="$(smoke_log_dir)"
  log="$root/logs/${name}.log"
  helpers="$root/logs/${name}-helpers"
  mkdir -p "$(dirname "$log")"

  set +e
  "$@" >"$log" 2>&1
  local code=$?
  set -e
  cat "$log"
  copy_helper_logs "$helpers"

  if [[ "$code" -eq 0 ]]; then
    return 0
  fi

  local detail
  detail="$(tr '\n' ' ' <"$log" | head -c 400)"
  if [[ "${LIFECYCLE_ENFORCE:-}" == "true" ]]; then
    echo "lifecycle enforce hard-fail: $name exit=$code" >&2
    echo "lifecycle enforce log: $log" >&2
    return "$code"
  fi

  echo "::warning title=Lifecycle advisory ($name)::exit $code — $detail"
  write_summary "$name" "$code" "$detail"
  return 0
}

if [[ "${1:-}" == "--self-test" ]]; then
  unset LIFECYCLE_ENFORCE || true
  if ! run_enforce self-test-soft -- false; then
    echo "self-test: soft mode should swallow false exit" >&2
    exit 1
  fi
  export LIFECYCLE_ENFORCE=true
  if run_enforce self-test-hard -- false; then
    echo "self-test: hard mode should fail on false exit" >&2
    exit 1
  fi
  if ! run_enforce self-test-hard-ok -- true; then
    echo "self-test: hard mode should pass true exit" >&2
    exit 1
  fi
  root="$(smoke_log_dir)"
  test -f "$root/logs/self-test-hard.log" || {
    echo "self-test: expected persisted hard-fail log" >&2
    exit 1
  }
  echo "enforce self-test ok"
  exit 0
fi

run_enforce "$@"
