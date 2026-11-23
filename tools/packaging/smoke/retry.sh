#!/usr/bin/env bash
# Retry a smoke command. Infra failures retry twice. Assertion failures do not
# retry, except GUI and kind legs, which get one retry. A pass after a retry is
# recorded as flaky and does not fail the leg.
# Usage: retry.sh --leg assert|gui|kind -- command [args...]
set -uo pipefail

leg=assert
if [[ "${1:-}" == "--self-test" ]]; then
  exec bash "$(dirname "$0")/retry-self-test.sh"
fi
if [[ "${1:-}" == "--leg" ]]; then
  leg="${2:?leg}"
  shift 2
fi
if [[ "${1:-}" == "--" ]]; then
  shift
fi
if [[ "$#" -lt 1 ]]; then
  echo "usage: retry.sh --leg assert|gui|kind -- command" >&2
  exit 2
fi

here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=classify.sh
source "$here/classify.sh"

max_tries=1
case "$leg" in
  gui|kind) max_tries=2 ;;
  assert) max_tries=1 ;;
  *) echo "unknown leg $leg" >&2; exit 2 ;;
esac

attempt=1
log="$(mktemp)"
while true; do
  set +e
  "$@" >"$log" 2>&1
  code=$?
  set -e
  cat "$log"
  if [[ "$code" -eq 0 ]]; then
    if [[ "$attempt" -gt 1 ]]; then
      echo "flaky: $leg passed on attempt $attempt"
      if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
        {
          echo "### flaky"
          echo "Leg \`$leg\` passed on attempt $attempt. The first attempt failed."
        } >> "$GITHUB_STEP_SUMMARY"
      fi
      if [[ -n "${GH_TOKEN:-}" && -n "${GITHUB_REPOSITORY:-}" ]]; then
        gh issue create --repo "$GITHUB_REPOSITORY" \
          --title "flaky: $leg smoke passed on retry" \
          --body "A $leg product-smoke leg failed once and passed on attempt $attempt in ${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID:-0}. The gate stayed green. Infra retries are not product escapes." \
          >/dev/null 2>&1 || true
      fi
    fi
    rm -f "$log"
    exit 0
  fi
  class="$(smoke_classify "$code" "$log")"
  echo "smoke-classify attempt=$attempt class=$class exit=$code leg=$leg"
  if [[ "$class" == "infra" && "$attempt" -lt 3 ]]; then
    attempt=$((attempt + 1))
    sleep $((attempt * 5))
    continue
  fi
  if [[ "$class" == "assertion" && "$leg" != "assert" && "$attempt" -lt "$max_tries" ]]; then
    attempt=$((attempt + 1))
    sleep 5
    continue
  fi
  rm -f "$log"
  exit "$code"
done
