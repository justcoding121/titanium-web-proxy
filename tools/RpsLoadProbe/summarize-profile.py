#!/usr/bin/env python3
"""Summarise profile-arm.sh captures into a markdown report.

    python3 summarize-profile.py ROOT [--cpus 4] [--markdown out.md]

ROOT holds one directory per arm (ROOT/<mode>/ as written by profile-arm.sh). The report has:
  1. CPU split per arm: percent of the proxy tree's CPU samples and CPU-us per request in each bucket
     (TWP managed / .NET libs / SslStream+OpenSSL / kernel / GC / JIT / thread pool / runtime locks / other),
     with native peers (nginx, HAProxy, Envoy) split the same way as the native floor.
  2. Allocation per request (dotnet-counters heap.total_allocated and gc-verbose AllocationTick types).
  3. Runtime counters (GC counts, lock contentions, thread-pool queue).
  4. Top self-time symbols and every Titanium.Web.Proxy-owned frame above 1.5% inclusive CPU (the Phase-2 gate).

CPU-us per request = proxy_cpu_avg_pct x cpus / 100 / rps from the arm's own probe CSV, so a bucket's
us/request is comparable across arms of the same wire. Absolute numbers from a shared VM are
diagnosis-only; compare ratios between arms of one capture session.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rps_csv  # noqa: E402

BUCKETS = ["TWP managed", ".NET libs (managed)", "SslStream + OpenSSL", "Kernel (syscalls, net)",
           "GC", "JIT / tiering", "Thread pool (spin, wait)", "Runtime locks (Monitor)",
           "Runtime other (alloc, casts, stubs)", "Native peer code", "libc / other native"]

SSL_DSO = re.compile(r"libssl|libcrypto|Cryptography\.Native|Net\.Security\.Native", re.I)
GC_SYM = re.compile(r"gc_heap|GCHeap|\bWKS::|\bSVR::|GarbageCollect|gc_thread|bgc_|mark_object|plan_phase|"
                    r"relocate_|compact_|sweep_|allocate_more_space|GCToEEInterface|SystemDomain::|"
                    r"gen0_|generation_|\bgc_", re.I)
POOL_SYM = re.compile(r"PortableThreadPool|ThreadPoolWorkQueue|LowLevelLifoSemaphore|LowLevelLock|WaitSubsystem|"
                      r"ThreadNative_SpinWait|YieldProcessor|System\.Threading\.SpinWait|Thread::.*Sleep", re.I)
LOCK_SYM = re.compile(r"Monitor_|EnterObjMonitor|AwareLock|SyncBlock|ObjHeader::|JIT_MonEnter|JIT_MonExit|"
                      r"Monitor::(Enter|Exit)", re.I)
NATIVE_PEER_DSO = re.compile(r"^(nginx|haproxy|envoy)", re.I)

HYPOTHESIS_FRAMES = (r"HeaderParser|HeaderBuilder|HttpHostHeader|Http1FramingValidator|ApplyTransparentForwardCleartextHost|"
                     r"TryRentFromPool|TryRentPooled|TcpConnectionFactory|H1TerminateLiteClient|ConnectionHeaderTokens|"
                     r"HeaderCollection|HttpHeader::|WriteResponseWithWireBodyAsync|RebindForTerminateLite|ResetWireState")

# one line of `perf report --sort comm,dso,sym`:  12.34%  comm  dso  [.] symbol
LINE = re.compile(r"^\s*(\d+\.\d+)%\s+(.*?)\s{2,}(\S.*?)\s{2,}\[(.)\]\s+(.*)$")
CHILD = re.compile(r"^\s*(\d+\.\d+)%\s+(\d+\.\d+)%\s+\[(.)\]\s+(.*)$")


def bucket(comm: str, dso: str, kind: str, sym: str) -> str:
    if kind == "k" or dso.startswith("[kernel"):
        return "Kernel (syscalls, net)"
    if "clrjit" in dso or "Tiered Com" in comm:
        return "JIT / tiering"
    if SSL_DSO.search(dso) or "[System.Net.Security]" in sym:
        return "SslStream + OpenSSL"
    if "[Titanium.Web.Proxy" in sym:
        return "TWP managed"
    if "Server GC" in comm or "BGC" in comm or "WKS GC" in comm or (dso.startswith("libcoreclr") and GC_SYM.search(sym)):
        return "GC"
    if LOCK_SYM.search(sym):
        return "Runtime locks (Monitor)"
    if POOL_SYM.search(sym):
        return "Thread pool (spin, wait)"
    if dso.startswith("[JIT]") or re.search(r"\[System[.\w]*\]", sym) or dso.endswith(".dll"):
        return ".NET libs (managed)"
    if dso.startswith("libcoreclr") or dso.startswith("libclr") or dso.startswith("libSystem.Native"):
        return "Runtime other (alloc, casts, stubs)"
    if NATIVE_PEER_DSO.search(dso):
        return "Native peer code"
    return "libc / other native"


def read_self(path: Path):
    rows = []
    for line in path.read_text(errors="replace").splitlines():
        if line.startswith("#") or not line.strip():
            continue
        m = LINE.match(line)
        if m:
            pct, comm, dso, kind, sym = m.groups()
            rows.append((float(pct), comm.strip(), dso.strip(), kind, sym.strip()))
    return rows


def read_children(path: Path):
    rows = []
    if not path.exists():
        return rows
    for line in path.read_text(errors="replace").splitlines():
        m = CHILD.match(line)
        if m:
            rows.append((float(m.group(1)), float(m.group(2)), m.group(4).strip()))
    return rows


def read_counters(path: Path):
    stats = defaultdict(list)
    if not path.exists():
        return {}
    with open(path, newline="", encoding="utf-8", errors="replace") as fh:
        for r in csv.DictReader(fh):
            try:
                stats[r["Counter Name"]].append(float(r["Mean/Increment"]))
            except (ValueError, KeyError):
                pass
    return stats


def counter(stats, prefix, tail_skip=2):
    for name, vals in stats.items():
        if name.startswith(prefix):
            vals = vals[1:-tail_skip] if len(vals) > 4 + tail_skip else vals
            return statistics.fmean(vals) if vals else None
    return None


def counter_sum(stats, prefix):
    total, found = 0.0, False
    for name, vals in stats.items():
        if name.startswith(prefix):
            vals = vals[1:-2] if len(vals) > 6 else vals
            total += statistics.fmean(vals) if vals else 0
            found = True
    return total if found else None


def arm_csv(dirpath: Path):
    rows = []
    for f in sorted((dirpath / "csv").glob("rps-ramp-*.csv")):
        rows += rps_csv.load_rows(f)
    return rps_csv.peak_row(rows) if rows else None


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root")
    ap.add_argument("--cpus", type=int, default=rps_csv.default_cpus())
    ap.add_argument("--gate-pct", type=float, default=1.5, help="inclusive-CPU gate for TWP-owned frames")
    ap.add_argument("--markdown", default=None)
    args = ap.parse_args(argv)

    root = Path(args.root)
    arms = sorted(d for d in root.iterdir() if d.is_dir() and (d / "perf-self.txt").exists())
    if not arms:
        print("no capture directories with perf-self.txt under", root, file=sys.stderr)
        return 1

    out = [f"# Linux profile summary (cpus={args.cpus})", "",
           "Percent = share of the proxy tree's perf samples (task-clock, 997 Hz, one 20 s window per arm). "
           "us/req = bucket share x CPU-us per request from the arm's own CSV row. Diagnosis-only absolutes.", ""]
    splits, costs, meta = {}, {}, {}
    for d in arms:
        rows = read_self(d / "perf-self.txt")
        total = sum(r[0] for r in rows) or 1.0
        split = defaultdict(float)
        for pct, comm, dso, kind, sym in rows:
            split[bucket(comm, dso, kind, sym)] += pct / total * 100.0
        splits[d.name] = split
        peak = arm_csv(d)
        meta[d.name] = peak
        cost = rps_csv.cpu_us_per_request(peak["cpu_pct"], peak["rps"], args.cpus) if peak else None
        costs[d.name] = cost

    out += ["## CPU split (percent of proxy-tree samples)", "",
            "| Arm | RPS | CPU-us/req | " + " | ".join(BUCKETS) + " |",
            "|---|---:|---:|" + "---:|" * len(BUCKETS)]
    for name, split in splits.items():
        peak = meta[name]
        cost = costs[name]
        out.append(f"| {name} | {peak['rps']:.0f} | {'-' if cost is None else f'{cost:.1f}'} | "
                   + " | ".join(f"{split.get(b, 0):.1f}" for b in BUCKETS) + " |" if peak else
                   f"| {name} | - | - | " + " | ".join(f"{split.get(b, 0):.1f}" for b in BUCKETS) + " |")
    out += ["", "## CPU-us per request by bucket", "",
            "| Arm | Total | " + " | ".join(BUCKETS) + " |", "|---|---:|" + "---:|" * len(BUCKETS)]
    for name, split in splits.items():
        cost = costs[name]
        if cost is None:
            continue
        out.append(f"| {name} | {cost:.1f} | " + " | ".join(f"{cost * split.get(b, 0) / 100:.1f}" for b in BUCKETS) + " |")

    out += ["", "## Allocation and runtime counters (managed arms)", "",
            "| Arm | Alloc B/req (counters) | Alloc B/req (AllocationTick) | gen0 GC/s | gen2 GC/s | lock contentions/s | pool queue len | GC pause ms/s |",
            "|---|---:|---:|---:|---:|---:|---:|---:|"]
    alloc_sections = []
    for d in arms:
        stats = read_counters(d / "counters.csv")
        peak = meta[d.name]
        rps = peak["rps"] if peak else None
        alloc_s = counter(stats, "dotnet.gc.heap.total_allocated")
        bpr = f"{alloc_s / rps:,.0f}" if alloc_s and rps else "-"
        tick_bpr = "-"
        tsv = d / "alloc-by-type.tsv"
        if tsv.exists():
            lines = tsv.read_text().splitlines()
            if lines and lines[0].startswith("TOTAL"):
                f = lines[0].split("\t")
                span = float(f[4].split("=")[1]) if len(f) > 4 and "span_s=" in f[4] else 0
                if span > 0 and rps:
                    tick_bpr = f"{int(f[1]) / span / rps:,.0f}"
                alloc_sections.append((d.name, lines[1:11], rps, span, int(f[1])))
        gen0 = counter(stats, "dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen0]")
        gen2 = counter(stats, "dotnet.gc.collections ({collection} / 1 sec)[gc.heap.generation=gen2]")
        lock = counter(stats, "dotnet.monitor.lock_contentions")
        queue = counter(stats, "dotnet.thread_pool.queue.length")
        pause = counter(stats, "dotnet.gc.pause.time")
        fmt = lambda v, p=1: "-" if v is None else f"{v:.{p}f}"  # noqa: E731
        out.append(f"| {d.name} | {bpr} | {tick_bpr} | {fmt(gen0)} | {fmt(gen2, 2)} | {fmt(lock, 0)} | {fmt(queue)} | "
                   f"{'-' if pause is None else f'{pause * 1000:.1f}'} |")
    for name, lines, rps, span, total in alloc_sections:
        out += ["", f"### Allocation by type: {name}", "", "| Type | bytes/req (est.) | share % |", "|---|---:|---:|"]
        for line in lines:
            t, b, _, p = line.split("\t")[:4]
            per = int(b) / span / rps if span > 0 and rps else 0
            out.append(f"| `{t[:110]}` | {per:,.0f} | {p} |")

    for d in arms:
        rows = read_self(d / "perf-self.txt")
        total = sum(r[0] for r in rows) or 1.0
        agg = defaultdict(float)
        for pct, comm, dso, kind, sym in rows:
            agg[(sym[:120], bucket(comm, dso, kind, sym))] += pct / total * 100.0
        out += ["", f"## Top self-time symbols: {d.name}", "", "| % | Bucket | Symbol |", "|---:|---|---|"]
        for (sym, b), pct in sorted(agg.items(), key=lambda kv: -kv[1])[:18]:
            out.append(f"| {pct:.2f} | {b} | `{sym}` |")
        kids = read_children(d / "perf-children.txt")
        twp = [(c, s, n) for c, s, n in kids if "[Titanium.Web.Proxy" in n]
        if twp:
            out += ["", f"### TWP-owned inclusive CPU: {d.name} (gate {args.gate_pct}% = Phase-2 candidate)", "",
                    "| Inclusive % | Self % | Gate | Frame |", "|---:|---:|---|---|"]
            for c, s, n in sorted(twp, key=lambda x: -x[0])[:25]:
                n = re.sub(r"\[Optimized\w*\]|\[Tier\d\]", "", n)
                out.append(f"| {c:.2f} | {s:.2f} | {'ABOVE' if c >= args.gate_pct else '-'} | `{n[:150]}` |")
        owned = [(c, s, n) for c, s, n in kids
                 if re.search(HYPOTHESIS_FRAMES, n) and "[Titanium.Web.Proxy" in n + " "]
        if owned:
            out += ["", f"### Phase-2 hypothesis frames (inclusive): {d.name}", "",
                    "| Inclusive % | Self % | Gate | Frame |", "|---:|---:|---|---|"]
            for c, s, n in sorted(owned, key=lambda x: -x[0])[:20]:
                n = re.sub(r"\[Optimized\w*\]|\[Tier\d\]", "", n)
                out.append(f"| {c:.2f} | {s:.2f} | {'ABOVE' if c >= args.gate_pct else '-'} | `{n[:150]}` |")
        mon = [(c, s, n) for c, s, n in kids if re.search(r"Monitor|ConcurrentBag|SpinWait", n)]
        if mon:
            out += ["", f"### Lock-related inclusive frames: {d.name}", "", "| Inclusive % | Self % | Frame |", "|---:|---:|---|"]
            for c, s, n in sorted(mon, key=lambda x: -x[0])[:8]:
                out.append(f"| {c:.2f} | {s:.2f} | `{n[:150]}` |")

    text = "\n".join(out) + "\n"
    print(text)
    if args.markdown:
        Path(args.markdown).write_text(text, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
