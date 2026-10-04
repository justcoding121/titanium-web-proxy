#!/usr/bin/env python3
"""CPU cost per request from existing RpsLoadProbe CSV columns (no schema change).

    cpu_us_per_req = proxy_cpu_avg_pct * ProcessorCount / 100 / rps * 1e6

proxy_cpu_avg_pct is the /proc CPU of the whole proxy process tree (TWP child, or nginx/HAProxy/Envoy
master+workers), normalised so that 100 means every logical CPU was busy. Pass --cpus when the CSV came
from a machine other than this one (GitHub ubuntu-latest = 4).

Examples:
  python3 rps-cpu-per-request.py results/rps-ramp-*.csv --cpus 4
  python3 rps-cpu-per-request.py a.csv b.csv --concurrency 32 --ref nginx-reverse-http1 --markdown out.md
"""
from __future__ import annotations

import argparse
import statistics
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rps_csv  # noqa: E402


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("csv", nargs="+", help="rps-ramp-*.csv files (several = repeats; medians are reported)")
    ap.add_argument("--cpus", type=int, default=rps_csv.default_cpus(), help="logical CPUs of the measured machine")
    ap.add_argument("--concurrency", type=int, default=None,
                    help="only rows at this concurrency (default: each file's peak-RPS row per arm)")
    ap.add_argument("--ref", default=None, help="reference arm name; adds a ratio column vs this arm")
    ap.add_argument("--contains", default=None, help="only arms whose name contains this text")
    ap.add_argument("--markdown", default=None, help="write the table to this file as well as stdout")
    args = ap.parse_args(argv)

    per_arm: dict[str, list[dict]] = defaultdict(list)
    for path in args.csv:
        by_arm: dict[str, list[dict]] = defaultdict(list)
        for row in rps_csv.load_rows(path):
            by_arm[row["arm"]].append(row)
        for arm, rows in by_arm.items():
            if args.contains and args.contains not in arm:
                continue
            best = rps_csv.peak_row(rows, args.concurrency)
            if best is not None:
                per_arm[arm].append(best)

    if not per_arm:
        print("no rows matched", file=sys.stderr)
        return 1

    table = []
    for arm, rows in per_arm.items():
        costs = [c for c in (rps_csv.cpu_us_per_request(r["cpu_pct"], r["rps"], args.cpus) for r in rows)
                 if c is not None]
        table.append({
            "arm": arm,
            "n": len(rows),
            "c": sorted({r["concurrency"] for r in rows}),
            "rps": statistics.median(r["rps"] for r in rows),
            "cpu_pct": statistics.median(r["cpu_pct"] for r in rows if r["cpu_pct"] is not None)
            if any(r["cpu_pct"] is not None for r in rows) else None,
            "cost": statistics.median(costs) if costs else None,
        })
    ref = next((t for t in table if t["arm"] == args.ref), None) if args.ref else None

    lines = [f"CPU per request (cpus={args.cpus}; cost = proxy_cpu_avg_pct x cpus / 100 / rps)", "",
             "| Arm | n | c | RPS | proxy CPU % | CPU-us / req | CPU-% per 1k RPS |"
             + (" vs ref |" if ref else ""),
             "|---|---:|---|---:|---:|---:|---:|" + ("---:|" if ref else "")]
    for t in sorted(table, key=lambda x: x["arm"]):
        cost = "-" if t["cost"] is None else f"{t['cost']:.1f}"
        pct = "-" if t["cpu_pct"] is None else f"{t['cpu_pct']:.1f}"
        per_k = "-" if t["cpu_pct"] is None else f"{t['cpu_pct'] / (t['rps'] / 1000.0):.2f}"
        line = (f"| {t['arm']} | {t['n']} | {','.join(map(str, t['c']))} | {t['rps']:.0f} | {pct} | {cost} | {per_k} |")
        if ref:
            ratio = "-" if t["cost"] is None or ref["cost"] in (None, 0) else f"{t['cost'] / ref['cost']:.2f}x"
            line += f" {ratio} |"
        lines.append(line)
    out = "\n".join(lines) + "\n"
    print(out)
    if args.markdown:
        Path(args.markdown).write_text(out, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
