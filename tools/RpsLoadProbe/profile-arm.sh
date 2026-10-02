#!/usr/bin/env bash
# Linux CPU / allocation / perf capture for ONE RpsLoadProbe arm at a fixed concurrency (diagnosis only).
#
#   profile-arm.sh --probe PATH/RpsLoadProbe --mode reverse-http1-tls --out DIR \
#                  [--concurrency 32] [--capture-sec 20] [--warmup-sec 5] [--probe-arg X]...
#
# Timeline of the measure step (one steady load, nothing else changes between windows):
#   counters   whole measure step      dotnet-counters  System.Runtime (alloc-rate, GC counts, lock contention, ...)
#   window 1   perf record -g          all processes of the proxy tree (managed via DOTNET_PerfMapEnabled)
#   window 2   dotnet-trace            dotnet-sampled-thread-time (managed stacks)           [managed arms]
#   window 3   dotnet-trace            gc-verbose (GCAllocationTick -> bytes per type)       [managed arms]
# Native peers (nginx / HAProxy / Envoy) only get window 1: that is the native floor for the same wire.
#
# Needs: perf, dotnet-trace, dotnet-counters (dotnet tools), sudo for perf paranoid/kptr settings.
set -uo pipefail

probe=""; mode=""; out=""; conc=32; cap=20; warm=5; gap=2; probe_args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --probe) probe="$2"; shift 2 ;;
    --mode) mode="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --concurrency) conc="$2"; shift 2 ;;
    --capture-sec) cap="$2"; shift 2 ;;
    --warmup-sec) warm="$2"; shift 2 ;;
    --probe-arg) probe_args+=("$2"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -x "$probe" ] && [ -n "$mode" ] && [ -n "$out" ] || { sed -n 2,16p "$0"; exit 2; }
mkdir -p "$out"
out="$(cd "$out" && pwd)"

case "$mode" in
  nginx-*|haproxy-*|envoy-*) managed=0 ;;
  *) managed=1 ;;
esac

# Measure step must cover: 2 s settle + counters + 3 windows + gaps + slack.
if [ "$managed" = 1 ]; then windows=3; else windows=1; fi
measure=$(( 6 + windows * cap + (windows - 1) * gap + 6 ))

sudo sysctl -q kernel.perf_event_paranoid=-1 kernel.kptr_restrict=0 || true
# Profiling-only runtime settings (never set in a product or RPS measurement run):
#  PerfMapEnabled=1            writes /tmp/perf-<pid>.map so perf can name JIT-compiled frames
#  EnableWriteXorExecute=0     W^X double mapping hides JIT code in memfd:doublemapper, which perf cannot map
#  TieredPGO stays default so steady-state code matches a normal run
export DOTNET_PerfMapEnabled="${DOTNET_PerfMapEnabled:-1}"
export DOTNET_EnableWriteXorExecute="${DOTNET_EnableWriteXorExecute:-0}"

"$probe" --ramp --mode "$mode" --concurrency "$conc" --warmup-sec "$warm" --duration-sec "$measure" \
  --repeats 1 --results-dir "$out/csv" "${probe_args[@]}" > "$out/probe.log" 2>&1 &
probe_pid=$!

wait_for() { # pattern, timeout
  local n=0
  while ! grep -q -- "$1" "$out/probe.log" 2>/dev/null; do
    kill -0 "$probe_pid" 2>/dev/null || return 1
    sleep 0.5; n=$((n + 1))
    if [ "$n" -gt $(( $2 * 2 )) ]; then return 1; fi
  done
  return 0
}

wait_for "attach: split" 120 || { echo "arm did not start; see $out/probe.log" >&2; tail -20 "$out/probe.log" >&2; exit 1; }
proxy_pid="$(sed -n 's/.*proxy pid=\([0-9]*\).*/\1/p' "$out/probe.log" | head -1)"
[ -n "$proxy_pid" ] || { echo "no proxy pid in log" >&2; exit 1; }
wait_for "measure c=$conc" $(( warm + 60 )) || { echo "measure never started" >&2; exit 1; }
sleep 2

tree_pids() { # root pid -> root + all descendants
  local p="$1" kids k
  echo "$p"
  kids="$(pgrep -P "$p" 2>/dev/null || true)"
  for k in $kids; do tree_pids "$k"; done
}
pids="$(tree_pids "$proxy_pid" | sort -un | paste -sd, -)"
echo "$pids" > "$out/proxy-tree-pids.txt"
echo "mode=$mode conc=$conc measure=${measure}s capture=${cap}s proxy_tree=$pids" | tee "$out/capture.txt"

if [ "$managed" = 1 ]; then
  counters_sec=$(( 4 + windows * cap + (windows - 1) * gap ))
  dotnet-counters collect -p "$proxy_pid" --refresh-interval 1 --format csv --counters System.Runtime \
    --duration "00:$(printf '%02d' $((counters_sec / 60))):$(printf '%02d' $((counters_sec % 60)))" \
    -o "$out/counters" > "$out/counters.log" 2>&1 &
  sleep 3
fi

echo "[window 1] perf record -g ${cap}s" | tee -a "$out/capture.txt"
perf record -F 997 -g --call-graph fp -p "$pids" -o "$out/perf.data" -- sleep "$cap" > "$out/perf-record.log" 2>&1 || \
  echo "perf record failed (see perf-record.log)" | tee -a "$out/capture.txt"
sleep "$gap"

if [ "$managed" = 1 ]; then
  echo "[window 2] dotnet-trace dotnet-sampled-thread-time ${cap}s" | tee -a "$out/capture.txt"
  dotnet-trace collect -p "$proxy_pid" --profile dotnet-sampled-thread-time \
    --duration "00:00:$(printf '%02d' "$cap")" -o "$out/sampled.nettrace" > "$out/dotnet-trace-sampled.log" 2>&1 || \
    echo "dotnet-trace sampled failed" | tee -a "$out/capture.txt"
  sleep "$gap"
  echo "[window 3] dotnet-trace gc-verbose ${cap}s" | tee -a "$out/capture.txt"
  dotnet-trace collect -p "$proxy_pid" --profile gc-verbose \
    --duration "00:00:$(printf '%02d' "$cap")" -o "$out/gcverbose.nettrace" > "$out/dotnet-trace-gc.log" 2>&1 || \
    echo "dotnet-trace gc-verbose failed" | tee -a "$out/capture.txt"
fi

wait "$probe_pid"
echo "probe exit=$?" >> "$out/capture.txt"
wait

# Post-process while the perf map files in /tmp still exist.
if [ -f "$out/perf.data" ]; then
  perf report -i "$out/perf.data" --stdio --no-children -g none --sort comm,dso,sym --percent-limit 0.02 \
    > "$out/perf-self.txt" 2> "$out/perf-report.err" || true
  perf report -i "$out/perf.data" --stdio --children -g none --sort sym --percent-limit 0.5 \
    > "$out/perf-children.txt" 2>> "$out/perf-report.err" || true
  perf report -i "$out/perf.data" --stdio --no-children -g none --sort comm --percent-limit 0.1 \
    > "$out/perf-comm.txt" 2>> "$out/perf-report.err" || true
fi
if [ -f "$out/sampled.nettrace" ]; then
  dotnet-trace convert "$out/sampled.nettrace" --format Speedscope -o "$out/sampled" > "$out/convert.log" 2>&1 || true
fi
if [ -f "$out/gcverbose.nettrace" ] && [ -n "${RPS_ALLOC_TICKS:-}" ]; then
  "$RPS_ALLOC_TICKS" "$out/gcverbose.nettrace" --pid "$proxy_pid" --top 60 > "$out/alloc-by-type.tsv" 2> "$out/alloc.err" || true
fi
rm -f "$out/perf.data.old"
gzip -f "$out/perf.data" 2>/dev/null || true
echo "done: $out"
