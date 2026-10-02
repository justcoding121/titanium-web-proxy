#!/usr/bin/env python3
"""Same-machine paired A/B runner for RpsLoadProbe.

Runs the SAME probe harness (built from one source tree) against two product builds, alternating
baseline / candidate on one VM so runner variance (about 5% between VMs) cancels inside each pair.

    python3 rps-ab-run.py --baseline-probe A/RpsLoadProbe --candidate-probe B/RpsLoadProbe \
        --mode compare-ceiling --arm-contains twp-reverse-http1 --pairs 5 --out ab-out

Layout written under --out:
    manifest.json
    pair-01/<baseline|candidate>/{rps-ramp-*.csv, run.log}
Then: python3 rps-ab-analyze.py ab-out
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path


def build_order(pairs: int, order: str) -> list[tuple[int, list[str]]]:
    plan = []
    for p in range(1, pairs + 1):
        if order == "counterbalance" and p % 2 == 0:
            plan.append((p, ["candidate", "baseline"]))
        else:
            plan.append((p, ["baseline", "candidate"]))
    return plan


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--baseline-probe", required=True, help="RpsLoadProbe executable built with the baseline product")
    ap.add_argument("--candidate-probe", required=True, help="RpsLoadProbe executable built with the candidate product")
    ap.add_argument("--mode", required=True)
    ap.add_argument("--arm-contains", default="")
    ap.add_argument("--arm-shard", default="all")
    ap.add_argument("--concurrency", default="32")
    ap.add_argument("--warmup-sec", type=int, default=2)
    ap.add_argument("--duration-sec", type=int, default=8)
    ap.add_argument("--pairs", type=int, default=5)
    ap.add_argument("--cooldown-sec", type=int, default=15, help="idle gap between consecutive runs")
    ap.add_argument("--order", choices=["alternate", "counterbalance"], default="alternate",
                    help="alternate = A,B,A,B...; counterbalance = A,B,B,A,A,B... (cancels linear drift)")
    ap.add_argument("--probe-arg", action="append", default=[], help="extra argument forwarded to the probe (repeatable)")
    ap.add_argument("--out", required=True)
    args = ap.parse_args(argv)

    if args.pairs < 2:
        print("--pairs must be >= 2 (a CI needs at least two pairs; use >= 5 for a gate)", file=sys.stderr)
        return 2
    probes = {"baseline": Path(args.baseline_probe), "candidate": Path(args.candidate_probe)}
    for side, p in probes.items():
        if not p.exists():
            print(f"{side} probe not found: {p}", file=sys.stderr)
            return 2
    if probes["baseline"].resolve() == probes["candidate"].resolve():
        print("warning: baseline and candidate probe are the same file (A/A run)", file=sys.stderr)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest = {
        "mode": args.mode, "arm_contains": args.arm_contains, "arm_shard": args.arm_shard,
        "concurrency": args.concurrency, "warmup_sec": args.warmup_sec, "duration_sec": args.duration_sec,
        "pairs": args.pairs, "order": args.order, "cooldown_sec": args.cooldown_sec,
        "cpus": os.cpu_count(), "runs": [],
    }
    failures = 0
    first = True
    for pair, sides in build_order(args.pairs, args.order):
        for side in sides:
            if not first and args.cooldown_sec > 0:
                time.sleep(args.cooldown_sec)
            first = False
            run_dir = out / f"pair-{pair:02d}" / side
            run_dir.mkdir(parents=True, exist_ok=True)
            cmd = [str(probes[side]), "--ramp", "--mode", args.mode, "--concurrency", args.concurrency,
                   "--warmup-sec", str(args.warmup_sec), "--duration-sec", str(args.duration_sec),
                   "--repeats", "1", "--arm-shard", args.arm_shard, "--results-dir", str(run_dir)]
            if args.arm_contains:
                cmd += ["--arm-contains", args.arm_contains]
            cmd += args.probe_arg
            print(f"[pair {pair}/{args.pairs}] {side}: {' '.join(cmd)}", flush=True)
            with open(run_dir / "run.log", "w", encoding="utf-8") as log:
                rc = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT).returncode
            csvs = sorted(run_dir.glob("rps-ramp-*.csv"))
            if rc != 0 or not csvs:
                failures += 1
                print(f"  FAILED rc={rc} csv={len(csvs)} (see {run_dir / 'run.log'})", file=sys.stderr, flush=True)
            manifest["runs"].append({"pair": pair, "side": side, "rc": rc,
                                     "csv": [str(c.relative_to(out)) for c in csvs]})
            (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
