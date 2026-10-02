#!/usr/bin/env python3
"""Paired A/B analysis for rps-ab-run.py output.

Per arm and per metric, each pair gives (candidate / baseline - 1) in percent; the report shows the mean
paired delta, its 95% Student-t confidence interval, and the per-pair spread. A delta whose CI includes
zero is reported as noise. Metrics: RPS, CPU-us/request, proxy RSS, p99, error count.

Gate (rule for keeping a product change): target-arm RPS up >= --min-gain or CPU/request down >= --min-gain
with a CI excluding zero, RSS and p99 not worse (CI not entirely above zero), zero errors.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import rps_csv  # noqa: E402

# metric -> (extractor, higher_is_better)
METRICS = ["rps", "cpu_us_per_req", "rss_mb", "p99_ms"]
HIGHER_IS_BETTER = {"rps": True, "cpu_us_per_req": False, "rss_mb": False, "p99_ms": False}


def collect(out: Path, concurrency: int | None, cpus: int):
    manifest = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    data: dict[str, dict[int, dict[str, dict]]] = defaultdict(lambda: defaultdict(dict))
    errors: dict[str, dict[str, int]] = defaultdict(lambda: {"baseline": 0, "candidate": 0})
    for run in manifest["runs"]:
        if run["rc"] != 0:
            continue
        rows = []
        for rel in run["csv"]:
            rows += rps_csv.load_rows(out / rel)
        by_arm = defaultdict(list)
        for r in rows:
            by_arm[r["arm"]].append(r)
        for arm, arm_rows in by_arm.items():
            best = rps_csv.peak_row(arm_rows, concurrency)
            if best is None:
                continue
            errors[arm][run["side"]] += sum(r["errors"] for r in arm_rows
                                            if concurrency is None or r["concurrency"] == concurrency)
            data[arm][run["pair"]][run["side"]] = {
                "rps": best["rps"],
                "cpu_us_per_req": rps_csv.cpu_us_per_request(best["cpu_pct"], best["rps"], cpus),
                "rss_mb": None if best["rss_bytes"] is None else best["rss_bytes"] / (1024 * 1024),
                "p99_ms": best["p99_ms"],
            }
    return manifest, data, errors


def pair_deltas(pairs: dict[int, dict], metric: str) -> list[float]:
    out = []
    for sides in pairs.values():
        a, b = sides.get("baseline"), sides.get("candidate")
        if not a or not b:
            continue
        va, vb = a.get(metric), b.get(metric)
        if va is None or vb is None or va == 0:
            continue
        out.append((vb / va - 1.0) * 100.0)
    return out


def classify(metric: str, ci_low: float, ci_high: float, mean: float) -> str:
    if ci_low != ci_low:  # NaN
        return "n<2"
    if ci_low <= 0.0 <= ci_high:
        return "noise"
    improved = (mean > 0) == HIGHER_IS_BETTER[metric]
    return "better" if improved else "WORSE"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("out", help="directory written by rps-ab-run.py")
    ap.add_argument("--cpus", type=int, default=None, help="logical CPUs of the measured machine (default: manifest)")
    ap.add_argument("--concurrency", type=int, default=None, help="restrict to this concurrency")
    ap.add_argument("--min-gain", type=float, default=2.0, help="gate: minimum mean improvement, percent")
    ap.add_argument("--markdown", default=None)
    args = ap.parse_args(argv)

    out = Path(args.out)
    manifest = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    cpus = args.cpus or manifest.get("cpus") or rps_csv.default_cpus()
    conc = args.concurrency
    if conc is None and "," not in str(manifest.get("concurrency", "")):
        conc = int(manifest["concurrency"])
    manifest, data, errors = collect(out, conc, cpus)

    lines = [
        f"## Paired A/B (mode `{manifest['mode']}`, arm filter `{manifest['arm_contains'] or '*'}`, "
        f"c={manifest['concurrency']}, {manifest['pairs']} pairs, order={manifest['order']}, "
        f"{manifest['warmup_sec']}s warmup + {manifest['duration_sec']}s measure, cpus={cpus})",
        "",
        "Delta = candidate vs baseline, percent, paired within one VM. CI = 95% Student-t on the mean of the paired deltas.",
        "",
        "| Arm | Metric | Baseline (median) | Candidate (median) | Mean delta | 95% CI | Pairs | Verdict |",
        "|---|---|---:|---:|---:|---|---:|---|",
    ]
    gate_lines = []
    for arm in sorted(data):
        pairs = data[arm]
        verdicts = {}
        for metric in METRICS:
            deltas = pair_deltas(pairs, metric)
            if not deltas:
                continue
            m, lo, hi = rps_csv.mean_ci95(deltas)
            verdict = classify(metric, lo, hi, m)
            verdicts[metric] = (verdict, m, lo, hi)
            base_vals = sorted(s["baseline"][metric] for s in pairs.values()
                               if "baseline" in s and s["baseline"].get(metric) is not None)
            cand_vals = sorted(s["candidate"][metric] for s in pairs.values()
                               if "candidate" in s and s["candidate"].get(metric) is not None)
            med = lambda v: v[len(v) // 2] if v else float("nan")  # noqa: E731
            ci = "n/a" if lo != lo else f"[{lo:+.2f}%, {hi:+.2f}%]"
            lines.append(f"| {arm} | {metric} | {med(base_vals):.1f} | {med(cand_vals):.1f} | {m:+.2f}% | {ci} | "
                         f"{len(deltas)} | {verdict} |")
        err = errors[arm]
        problems = []
        gain = False
        for metric in ("rps", "cpu_us_per_req"):
            v = verdicts.get(metric)
            if v and v[0] == "better" and abs(v[1]) >= args.min_gain:
                gain = True
        if not gain:
            problems.append(f"no >= {args.min_gain:.0f}% CI-significant gain in RPS or CPU/request")
        for metric in ("rss_mb", "p99_ms"):
            v = verdicts.get(metric)
            if v and v[0] == "WORSE":
                problems.append(f"{metric} worse")
        v = verdicts.get("rps")
        if v and v[0] == "WORSE" and v[1] <= -3.0:
            problems.append("RPS dropped more than 3%")
        if err["baseline"] or err["candidate"]:
            problems.append(f"errors baseline={err['baseline']} candidate={err['candidate']}")
        gate_lines.append(f"| {arm} | {'PASS' if not problems else 'FAIL'} | {'; '.join(problems) or '-'} |")

    lines += ["", "### Keep gate (per arm)", "", "| Arm | Gate | Reasons |", "|---|---|---|"] + gate_lines
    text = "\n".join(lines) + "\n"
    print(text)
    if args.markdown:
        Path(args.markdown).write_text(text, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
