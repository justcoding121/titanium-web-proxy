#!/usr/bin/env python3
"""Fill the Editions table from one compare-editions run (Win + Linux in one folder)."""
from __future__ import annotations

import argparse
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


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", required=True, type=int)
    parser.add_argument("--head-sha", required=True)
    args = parser.parse_args()
    ph = load_paste()
    win = ph.load_os(args.run, "windows-latest")
    lin = ph.load_os(args.run, "ubuntu-latest")
    if "twp-cli-reverse-http1" not in win or "twp-cli-reverse-http1" not in lin:
        raise SystemExit("editions CSVs missing twp-cli-reverse-http1")

    lines = [
        "| Arm | Win | Linux | Win÷ | Lin÷ | Gate |",
        "|---|---:|---:|---:|---:|---|",
    ]
    for label, num, den in ROWS:
        lines.append(
            f"| {label} | {ph.fmt_cell(win.get(num))} | {ph.fmt_cell(lin.get(num))} | "
            f"{ratio(win.get(num), win.get(den))} | {ratio(lin.get(num), lin.get(den))} | ≥ **0.50×** |"
        )
    table = "\n".join(lines)

    def lib(data, arm) -> str:
        m = data.get(arm)
        if not m:
            return "—"
        return f"**{round(m['Sustain']):,}**"

    footer = (
        f"`validate-edition-gates.ps1` floors are **0.50×** (runner noise on Plus/CLI feature arms). "
        f"Library baselines @ c=64 (same job): Win H1 {lib(win, 'twp-reverse-http1')} / TLS {lib(win, 'twp-reverse-http1-tls')}; "
        f"Linux H1 {lib(lin, 'twp-reverse-http1')} / TLS {lib(lin, 'twp-reverse-http1-tls')}. "
        f"Laptop smoke ratios stay on [Performance Local Lab — Editions](Performance-Local-Lab#editions-cli--plus-stress). "
        f"The macOS job in this run lost the hosted runner before the ramp finished, so this table stays Windows and Linux."
    )
    header = (
        f"Median of **3** repeats @ `{args.head_sha}`. Source: Actions [{args.run}]"
        f"(https://github.com/justcoding121/titanium-web-proxy/actions/runs/{args.run}). "
        f"Warmup 2s / measure 8s; concurrency 8–64; sustain = median peak RPS among SLO-pass steps @ **c=64**. "
        f"**RPS cells** show sustain; `<sub>` holds peak (when higher) plus `(MiB / CPU%)`. "
        f"The Gate column is that script's floor."
    )

    text = WIKI.read_text(encoding="utf-8")
    start = text.find("## Editions (CLI / Plus / Intercept)")
    end = text.find("\n## Heavier reverse workloads", start)
    if start < 0 or end < 0:
        raise SystemExit("editions section not found")
    block = text[start:end]
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
