#!/usr/bin/env bash
# Soft-to-hard lifecycle enforcement wrapper.
# Usage: enforce.sh <name> -- <cmd...>
# If LIFECYCLE_ENFORCE is not "true", failures become ::warning + step summary row + exit 0.
# If LIFECYCLE_ENFORCE=true, failures exit non-zero.
set -euo pipefail

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

  local log
  log="$(mktemp)"
  set +e
  "$@" >"$log" 2>&1
  local code=$?
  set -e
  cat "$log"
  if [[ "$code" -eq 0 ]]; then
    rm -f "$log"
    return 0
  fi

  local detail
  detail="$(tr '\n' ' ' <"$log" | head -c 400)"
  if [[ "${LIFECYCLE_ENFORCE:-}" == "true" ]]; then
    echo "lifecycle enforce hard-fail: $name exit=$code" >&2
    rm -f "$log"
    return "$code"
  fi

  echo "::warning title=Lifecycle advisory ($name)::exit $code — $detail"
  write_summary "$name" "$code" "$detail"
  rm -f "$log"
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
  echo "enforce self-test ok"
  exit 0
fi

run_enforce "$@"
