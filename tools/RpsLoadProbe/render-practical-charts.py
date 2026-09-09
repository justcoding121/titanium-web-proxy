#!/usr/bin/env python3
"""Render practical reverse-proxy RPS grouped bar charts (TWP / YARP / nginx / HAProxy / Envoy).

Reads compare-product CSVs (same sustain @ c=64 rule as paste-compare-product-wiki.ps1)
and writes PNGs for README (Linux) and the website (Win / Linux / macOS).

Example:
  python3 tools/RpsLoadProbe/render-practical-charts.py \\
    --results-root tools/RpsLoadProbe/results/gha-dl/33480574506 \\
    --out-dir wiki/images \\
    --title-suffix '@ af6feb9c'
"""

from __future__ import annotations

import argparse
import csv
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

# Practical industry reverse wires (short labels → CSV arm names).
# nginx HTTPS-origin peers use proxy_ssl (http1-tls-to-https, http2/http3-to-https-http1).
# H2→H2 / H3→H2 stay nginx=None — stock nginx has no H2/H3 upstream.
PRACTICAL_ARMS: List[Tuple[str, str, str, Optional[str], Optional[str], Optional[str]]] = [
    # label, twp, yarp, nginx, haproxy, envoy (None = not possible)
    (
        "H1 TLS→H1c",
        "twp-reverse-http1-tls",
        "yarp-reverse-http1-tls",
        "nginx-reverse-http1-tls",
        "haproxy-reverse-http1-tls",
        "envoy-reverse-http1-tls",
    ),
    (
        "H1 TLS→H1 TLS",
        "twp-reverse-http1-mitm",
        "yarp-reverse-http1-tls-to-https",
        "nginx-reverse-http1-tls-to-https",
        "haproxy-reverse-http1-tls-to-https",
        "envoy-reverse-http1-tls-to-https",
    ),
    (
        "H2 TLS→H1c",
        "twp-reverse-http2-cleartext",
        "yarp-reverse-http2",
        "nginx-reverse-http2",
        "haproxy-reverse-http2",
        "envoy-reverse-http2",
    ),
    (
        "H2 TLS→H1 TLS",
        "twp-reverse-http2-to-https-http1",
        "yarp-reverse-http2-to-https-http1",
        "nginx-reverse-http2-to-https-http1",
        "haproxy-reverse-http2-to-https-http1",
        "envoy-reverse-http2-to-https-http1",
    ),
    ("H2 TLS→h2c", "twp-reverse-http2-to-h2c", "yarp-reverse-http2-to-h2c", None, None, None),
    ("H2 TLS→H2 TLS", "twp-reverse-http2", "yarp-reverse-http2-to-https", None, None, None),
    (
        "H3→H1c",
        "twp-reverse-http3-cleartext",
        "yarp-reverse-http3-cleartext",
        "nginx-reverse-http3-cleartext",
        "haproxy-reverse-http3-cleartext",
        "envoy-reverse-http3-cleartext",
    ),
    (
        "H3→H1 TLS",
        "twp-reverse-http3-to-https-http1",
        "yarp-reverse-http3-to-https-http1",
        "nginx-reverse-http3-to-https-http1",
        "haproxy-reverse-http3-to-https-http1",
        "envoy-reverse-http3-to-https-http1",
    ),
    ("H3→H2 TLS", "twp-reverse-http3-to-http2", "yarp-reverse-http3-to-http2", None, None, None),
]

COLORS = {
    "Titanium": "#0B6E4F",
    "YARP": "#C45C26",
    "nginx": "#2F5D8C",
    "HAProxy": "#8B4513",
    "Envoy": "#6B5B95",
}

PRODUCTS = ("Titanium", "YARP", "nginx", "HAProxy", "Envoy")

OS_SPECS = (
    ("linux", "Linux", ("ubuntu-latest",)),
    ("windows", "Windows", ("windows-latest",)),
    ("macos", "macOS", ("macos-15-intel", "macos-latest")),
)


def median(vals: Sequence[float]) -> Optional[float]:
    if not vals:
        return None
    s = sorted(vals)
    return s[(len(s) - 1) // 2]


def arm_sustain_c64(csv_path: Path, arm: str) -> Optional[float]:
    """Sustain RPS @ c=64 (0 if SLO miss). Median across repeats in one CSV."""
    rows = [r for r in csv.DictReader(csv_path.open(newline="")) if r.get("arm") == arm]
    if not rows:
        return None
    # Group into ramp chunks of 4 concurrency steps when present; else last c=64 per pass.
    steps = 4
    sustains: List[float] = []
    i = 0
    while i + steps <= len(rows):
        chunk = rows[i : i + steps]
        c64_ok = [r for r in chunk if r.get("concurrency") == "64" and r.get("meets_slo") == "1"]
        if c64_ok:
            sustains.append(float(c64_ok[-1]["rps"]))
        else:
            c64_any = [r for r in chunk if r.get("concurrency") == "64"]
            if c64_any:
                sustains.append(0.0)
        i += steps
    if not sustains:
        # Fallback: last c=64 row
        c64 = [r for r in rows if r.get("concurrency") == "64"]
        if not c64:
            return None
        r = c64[-1]
        return float(r["rps"]) if r.get("meets_slo") == "1" else 0.0
    return median(sustains)


def find_csv(results_root: Path, os_keys: Iterable[str]) -> Optional[Path]:
    for key in os_keys:
        for pattern in (
            f"rps-csv-{key}/*.csv",
            f"{key}/*.csv",
            f"**/*{key}*.csv",
        ):
            hits = sorted(results_root.glob(pattern))
            if hits:
                return hits[0]
    return None


def collect_series(csv_path: Path) -> Dict[str, List[Optional[float]]]:
    """Return product → list of RPS aligned with PRACTICAL_ARMS (None = missing)."""
    out: Dict[str, List[Optional[float]]] = {p: [] for p in PRODUCTS}
    for _label, twp, yarp, nginx, haproxy, envoy in PRACTICAL_ARMS:
        out["Titanium"].append(arm_sustain_c64(csv_path, twp))
        out["YARP"].append(arm_sustain_c64(csv_path, yarp))
        for product, arm in (("nginx", nginx), ("HAProxy", haproxy), ("Envoy", envoy)):
            if arm is None:
                out[product].append(None)
            else:
                out[product].append(arm_sustain_c64(csv_path, arm))
    return out


def render_chart(
    series: Dict[str, List[Optional[float]]],
    os_title: str,
    out_path: Path,
    title_suffix: str,
) -> None:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np

    labels = [a[0] for a in PRACTICAL_ARMS]
    x = np.arange(len(labels), dtype=float)
    width = 0.14
    offsets = tuple((i - 2) * width for i in range(5))

    fig, ax = plt.subplots(figsize=(14.5, 5.4), dpi=140)
    ymax = 1.0
    for product, offset in zip(PRODUCTS, offsets):
        vals = series[product]
        heights = [0.0 if v is None else float(v) for v in vals]
        present = [v is not None for v in vals]
        # Hide missing bars (n/a cells) by setting height 0 and no edge; hatch optional.
        bars = ax.bar(
            x + offset,
            [h if p else 0.0 for h, p in zip(heights, present)],
            width,
            label=product,
            color=COLORS[product],
            edgecolor="white",
            linewidth=0.4,
            zorder=3,
        )
        for bar, p, h in zip(bars, present, heights):
            if not p:
                bar.set_visible(False)
            elif h > 0:
                ymax = max(ymax, h)

    ax.set_ylabel("Sustain RPS @ concurrency 64")
    title = f"Reverse proxy RPS — {os_title}"
    if title_suffix:
        title = f"{title} {title_suffix}"
    ax.set_title(title)
    ax.set_xticks(x)
    ax.set_xticklabels(labels, rotation=18, ha="right")
    ax.set_ylim(0, ymax * 1.12)
    ax.yaxis.set_major_formatter(plt.FuncFormatter(lambda v, _: f"{int(v):,}"))
    ax.grid(axis="y", linestyle=":", alpha=0.45, zorder=0)
    ax.legend(loc="upper right", framealpha=0.92, ncols=5, fontsize=8)
    ax.set_axisbelow(True)
    fig.text(
        0.01,
        0.01,
        "Tiny keep-alive GET · GHA 4-core · nginx/HAProxy/Envoy HTTPS-origin peers · "
        "missing bars = Not possible · SLO-miss sustain plotted as 0",
        fontsize=8,
        color="#444444",
    )
    fig.tight_layout(rect=(0, 0.04, 1, 1))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_path, bbox_inches="tight")
    plt.close(fig)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--results-root", type=Path, help="gha-dl/<runId> folder with rps-csv-* dirs")
    ap.add_argument("--csv-linux", type=Path)
    ap.add_argument("--csv-windows", type=Path)
    ap.add_argument("--csv-macos", type=Path)
    ap.add_argument("--out-dir", type=Path, default=Path("wiki/images"))
    ap.add_argument("--title-suffix", default="", help="e.g. '@ af6feb9c'")
    ap.add_argument(
        "--os",
        action="append",
        choices=("linux", "windows", "macos"),
        help="Subset of OS charts (default: all with a CSV)",
    )
    args = ap.parse_args()

    csv_by_os: Dict[str, Path] = {}
    if args.csv_linux:
        csv_by_os["linux"] = args.csv_linux
    if args.csv_windows:
        csv_by_os["windows"] = args.csv_windows
    if args.csv_macos:
        csv_by_os["macos"] = args.csv_macos
    if args.results_root:
        for key, _title, folder_keys in OS_SPECS:
            if key in csv_by_os:
                continue
            found = find_csv(args.results_root, folder_keys)
            if found:
                csv_by_os[key] = found

    if not csv_by_os:
        ap.error("Provide --results-root and/or --csv-linux/--csv-windows/--csv-macos")

    wanted = set(args.os) if args.os else set(csv_by_os)
    written = []
    for key, title, _ in OS_SPECS:
        if key not in wanted or key not in csv_by_os:
            continue
        path = csv_by_os[key]
        series = collect_series(path)
        out = args.out_dir / f"rps-practical-{key}.png"
        render_chart(series, title, out, args.title_suffix.strip())
        written.append(out)
        # Console summary
        print(f"{title} ← {path}")
        for i, (label, *_rest) in enumerate(PRACTICAL_ARMS):
            parts = []
            for product in PRODUCTS:
                v = series[product][i]
                parts.append("n/a" if v is None else f"{v:.0f}")
            print(
                f"  {label}: TWP={parts[0]} YARP={parts[1]} nginx={parts[2]} "
                f"HAProxy={parts[3]} Envoy={parts[4]}"
            )
        print(f"  wrote {out}")

    if not written:
        print("No charts written (no matching CSVs).", file=__import__("sys").stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
