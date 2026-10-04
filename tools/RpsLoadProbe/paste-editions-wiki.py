#!/usr/bin/env python3
"""Fill the Editions table from one compare-editions run (Win + Linux in one folder)."""
from __future__ import annotations

import argparse
import csv
import importlib.util
import re
from pathlib import Path

ROOT = Path("tools/RpsLoadProbe")
WIKI = Path("wiki/Performance.md")

# Display floor matches validate-edition-gates.ps1 (all edition ratios >= 0.50).
ROWS = [
    ("`twp-cli-reverse-http1` vs library", "twp-cli-reverse-http1", "twp-reverse-http1"),
    ("`twp-cli-reverse-http1-tls` vs library TLS", "twp-cli-reverse-http1-tls", "twp-reverse-http1-tls"),
    ("`twp-cli-reverse-http1-route` vs CLI", "twp-cli-reverse-http1-route", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-base-http1` vs CLI", "twp-cli-plus-base-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-cache-http1` (cold) vs CLI", "twp-cli-plus-cache-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-intercept-http1` vs CLI", "twp-cli-intercept-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-waf-http1` vs CLI", "twp-cli-plus-waf-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-cidr-http1` vs CLI", "twp-cli-plus-cidr-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-jwt-http1` vs CLI", "twp-cli-plus-jwt-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-ratelimit-http1` vs CLI", "twp-cli-plus-ratelimit-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-resilience-http1` vs CLI", "twp-cli-plus-resilience-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-discovery-file-http1` vs CLI", "twp-cli-plus-discovery-file-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-metrics-scrape-http1` vs CLI", "twp-cli-plus-metrics-scrape-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-cache-hit-http1` vs cache cold", "twp-cli-plus-cache-hit-http1", "twp-cli-plus-cache-http1"),
    ("`twp-cli-plus-cors-http1` vs CLI", "twp-cli-plus-cors-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-circuit-http1` vs CLI", "twp-cli-plus-circuit-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-plus-retry-http1` vs CLI", "twp-cli-plus-retry-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-static-http1` vs CLI", "twp-cli-static-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-logging-http1` vs CLI", "twp-cli-logging-http1", "twp-cli-reverse-http1"),
    ("`twp-cli-lb-leasttime-http1` vs route", "twp-cli-lb-leasttime-http1", "twp-cli-reverse-http1-route"),
    ("`twp-cli-dialect-twp-http1` vs CLI", "twp-cli-dialect-twp-http1", "twp-cli-reverse-http1"),
]


def load_paste():
    path = ROOT / "paste-heavier-wiki.py"
    spec = importlib.util.spec_from_file_location("paste_heavier_wiki", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def ratio(num, den) -> str:
    if not num or not den or not den.get("Sustain"):
        return "—"
    return f"**{num['Sustain'] / den['Sustain']:.2f}×**"


def pair_from_csv(ph, run: int, os_folder: str, numerator: str, denominator: str):
    """Metrics for both arms from one CSV. Mixing shard files would divide across VMs."""
    run_dir = ROOT / str(run)
    for path in ph._csv_files_for_os(run_dir, os_folder):
        arms = {r["arm"] for r in csv.DictReader(path.open(newline=""))}
        if numerator in arms and denominator in arms:
            return ph.arm_metrics(path, numerator), ph.arm_metrics(path, denominator)
    return None, None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", required=True, type=int)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--prior-sha", default="", help="SHA of rows this run does not re-measure")
    args = parser.parse_args()
    ph = load_paste()

    text = WIKI.read_text(encoding="utf-8")
    start = text.find("## Editions (CLI / Plus / Intercept)")
    end = text.find("\n## Heavier reverse workloads", start)
    if start < 0 or end < 0:
        raise SystemExit("editions section not found")
    block = text[start:end]
    table_at = block.find("| Arm |")
    if table_at < 0:
        raise SystemExit("editions table not found")
    existing_rows = {}
    for line in block[table_at:].splitlines():
        if line.startswith("| `"):
            existing_rows[line.split("|")[1].strip()] = line

    lines = [
        "| Arm | Win | Linux | Win÷ | Lin÷ | Gate |",
        "|---|---:|---:|---:|---:|---|",
    ]
    updated = 0
    for label, num, den in ROWS:
        win_num, win_den = pair_from_csv(ph, args.run, "windows-latest", num, den)
        lin_num, lin_den = pair_from_csv(ph, args.run, "ubuntu-latest", num, den)
        if win_num and lin_num and win_den and lin_den:
            lines.append(
                f"| {label} | {ph.fmt_cell(win_num)} | {ph.fmt_cell(lin_num)} | "
                f"{ratio(win_num, win_den)} | {ratio(lin_num, lin_den)} | ≥ **0.50×** |"
            )
            updated += 1
        elif label in existing_rows:
            lines.append(existing_rows[label])
        else:
            lines.append(
                f"| {label} | *Not measured* | *Not measured* | — | — | ≥ **0.50×** |"
            )
    if updated == 0:
        raise SystemExit("no edition pair found in a single CSV")
    table = "\n".join(lines)

    prior = f" Other edition rows stay @ `{args.prior_sha}`." if args.prior_sha else ""
    footer = (
        f"`validate-edition-gates.ps1` floors are **0.50×**. "
        f"Each ÷ column uses the two arms from the same job. "
        f"Circuit breaker and idempotent retry stay on the session path, so a ratio near intercept is expected. "
        f"Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress)."
    )
    header = (
        f"Median of **3** repeats @ `{args.head_sha}`. Source: Actions [{args.run}]"
        f"(https://github.com/justcoding121/titanium-web-proxy/actions/runs/{args.run}).{prior} "
        f"Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. "
        f"**RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. "
        f"The Gate column is that script's floor."
    )

    block2, n = re.subn(
        r"Median of \*\*3\*\* repeats @ `[^`]+`\. Source: Actions \[[0-9]+\]\([^)]+\)\.[^\n]*",
        header,
        block,
        count=1,
    )
    if n != 1:
        raise SystemExit("editions header not replaced")
    block2 = ph.replace_table_at(block2, block2.find("| Arm |"), table)
    block2, n = re.subn(
        r"`validate-edition-gates\.ps1`[^\n]*",
        footer,
        block2,
        count=1,
    )
    if n != 1:
        raise SystemExit("editions footer not replaced")
    WIKI.write_text(text[:start] + block2 + text[end:], encoding="utf-8")
    print(f"editions pasted from {args.run}")


if __name__ == "__main__":
    main()
