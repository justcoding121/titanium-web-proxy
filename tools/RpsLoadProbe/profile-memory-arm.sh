#!/usr/bin/env bash
# Mid-measure gcdump + Heap dump for one RpsLoadProbe arm (Linux / Docker).
# Usage (inside twp-mem-linux container, repo at /src, results at /out):
#   bash /src/tools/RpsLoadProbe/profile-memory-arm.sh reverse-http3-cleartext [out-subdir]
set -euo pipefail

MODE="${1:?mode required (e.g. reverse-http3-cleartext)}"
OUT_ROOT="${2:-/out/mem-audit-linux}"
CONCURRENCY="${CONCURRENCY:-64}"
WARMUP_SEC="${WARMUP_SEC:-3}"
DURATION_SEC="${DURATION_SEC:-20}"
DUMP_AFTER_SEC="${DUMP_AFTER_SEC:-4}"
SKIP_BUILD="${SKIP_BUILD:-0}"

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROBE_DIR="$REPO_ROOT/tools/RpsLoadProbe"
OUT_DIR="$OUT_ROOT/$MODE"
mkdir -p "$OUT_DIR"
LOG="$OUT_DIR/run.log"
ERR="$OUT_DIR/run.err.log"
META="$OUT_DIR/profile-meta.log"
: >"$LOG"
: >"$ERR"
: >"$META"

cd "$REPO_ROOT"
if [[ "$SKIP_BUILD" != "1" ]]; then
  echo "Building Release..."
  dotnet build -c Release "$PROBE_DIR/RpsLoadProbe.csproj" --nologo -v q
fi

DLL="$PROBE_DIR/bin/Release/net10.0/RpsLoadProbe.dll"
if [[ ! -f "$DLL" ]]; then
  echo "missing $DLL" >&2
  exit 1
fi

echo "Profiling $MODE → $OUT_DIR"
dotnet "$DLL" --ramp --mode "$MODE" \
  --concurrency "$CONCURRENCY" \
  --warmup-sec "$WARMUP_SEC" \
  --duration-sec "$DURATION_SEC" \
  --repeats 1 \
  --results-dir "$OUT_DIR" \
  >"$LOG" 2>"$ERR" &
PROBE_PID=$!

proxy_pid=""
dumped=0
pos=0
exit_code=0

deadline=$((SECONDS + WARMUP_SEC + DURATION_SEC + 120))
while kill -0 "$PROBE_PID" 2>/dev/null; do
  sleep 0.2
  if [[ -f "$LOG" ]]; then
    size=$(wc -c <"$LOG" | tr -d ' ')
    if (( size > pos )); then
      tail -c +"$((pos + 1))" "$LOG" | while IFS= read -r line || [[ -n "$line" ]]; do
        [[ -n "$line" ]] && echo "$line"
      done
      pos=$size
    fi
  fi

  if [[ -z "$proxy_pid" ]]; then
    if grep -qE 'proxy pid=[0-9]+' "$LOG" 2>/dev/null; then
      proxy_pid=$(grep -oE 'proxy pid=[0-9]+' "$LOG" | tail -1 | grep -oE '[0-9]+')
      echo "CAPTURED_PROXY_PID=$proxy_pid"
      echo "CAPTURED_PROXY_PID=$proxy_pid" >>"$META"
    fi
  fi

  if [[ "$dumped" -eq 0 && -n "$proxy_pid" ]] && grep -q 'measure c=' "$LOG" 2>/dev/null; then
    dumped=1
    echo "Waiting ${DUMP_AFTER_SEC}s then dumping pid=$proxy_pid..."
    sleep "$DUMP_AFTER_SEC"
    if kill -0 "$proxy_pid" 2>/dev/null; then
      gcdump="$OUT_DIR/proxy-${proxy_pid}.gcdump"
      echo "GCDUMP $gcdump"
      dotnet-gcdump collect -p "$proxy_pid" -o "$gcdump"
      echo "GCDUMP_DONE $gcdump" >>"$META"
      dump="$OUT_DIR/proxy-${proxy_pid}.dump"
      echo "HEAP DUMP $dump"
      dotnet-dump collect -p "$proxy_pid" -o "$dump" --type Heap
      echo "DUMP_DONE $dump" >>"$META"
    else
      echo "WARN: proxy exited before dump" >&2
    fi
  fi

  if (( SECONDS >= deadline )); then
    echo "WARN: deadline exceeded; killing probe" >&2
    kill "$PROBE_PID" 2>/dev/null || true
    break
  fi
done

set +e
wait "$PROBE_PID"
exit_code=$?
set -e
echo "EXIT_CODE=$exit_code"
if [[ -f "$LOG" ]]; then
  size=$(wc -c <"$LOG" | tr -d ' ')
  if (( size > pos )); then
    tail -c +"$((pos + 1))" "$LOG"
  fi
fi

shopt -s nullglob
gcs=("$OUT_DIR"/*.gcdump)
if (( ${#gcs[@]} > 0 )); then
  report="$OUT_DIR/gcdump-top.txt"
  echo "Report → $report"
  dotnet-gcdump report "${gcs[0]}" 2>&1 | head -n 120 | tee "$report"
else
  echo "WARN: no gcdump" >&2
fi

csvs=("$OUT_DIR"/rps-ramp-*.csv)
if [[ "$exit_code" -ne 0 && ${#csvs[@]} -eq 0 ]]; then
  echo "probe failed with exit $exit_code" >&2
  [[ -f "$ERR" ]] && tail -n 40 "$ERR" >&2
  exit "$exit_code"
fi
echo "Done: $OUT_DIR"
