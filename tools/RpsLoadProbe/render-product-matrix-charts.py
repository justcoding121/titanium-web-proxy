#!/usr/bin/env python3
"""Render per-OS reverse product-matrix charts (TWP vs YARP vs nginx vs HAProxy vs Envoy).

25 Client×Origin wires as small-multiples by client protocol. nginx/HAProxy/Envoy *Not possible*
is omitted (not a zero bar). MITM Lite/Full are tables only — this script never
plots interception arms.

CSV mode (same sustain @ c=64 rule as paste-compare-product-wiki.ps1):
  python3 tools/RpsLoadProbe/render-product-matrix-charts.py \\
    --results-root tools/RpsLoadProbe/results/gha-dl/<runId> \\
    --out-dir wiki/images --title-suffix '@ <sha>'

Wiki fallback (current Performance.md reverse tables, no CSV required):
  python3 tools/RpsLoadProbe/render-product-matrix-charts.py --from-wiki wiki/Performance.md
"""

from __future__ import annotations

import argparse
import importlib.util
import re
import sys
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

_PRACTICAL = Path(__file__).resolve().parent / "render-practical-charts.py"
_spec = importlib.util.spec_from_file_location("rps_practical_charts", _PRACTICAL)
_mod = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(_mod)
arm_sustain_c64 = _mod.arm_sustain_c64
find_csv = _mod.find_csv
COLORS = _mod.COLORS
PRODUCTS = _mod.PRODUCTS

CLIENTS = (
    "HTTP/1 · plain",
    "HTTP/1 · TLS",
    "HTTP/2 · plain",
    "HTTP/2 · TLS",
    "HTTP/3 · QUIC",
)
ORIGINS = CLIENTS

Row = Tuple[str, str, Optional[float], Optional[float], Optional[float], Optional[float], Optional[float]]


# paste-compare-product-wiki.ps1 arm stems (client, origin) → CSV names.
def arm_name(prefix: str, client: str, origin: str) -> str:
    cmap = {
        "HTTP/1 · plain": "http1",
        "HTTP/1 · TLS": "http1-tls",
        "HTTP/2 · plain": "http2-cleartext",
        "HTTP/2 · TLS": "http2",
        "HTTP/3 · QUIC": "http3",
    }
    omap = {
        "HTTP/1 · plain": "http1",
        "HTTP/1 · TLS": "https-http1",
        "HTTP/2 · plain": "http2-cleartext",
        "HTTP/2 · TLS": "https-http2",
        "HTTP/3 · QUIC": "http3",
    }
    c, o = cmap[client], omap[origin]
    if prefix == "twp":
        special = {
            ("http1", "http1"): "twp-reverse-http1",
            ("http1-tls", "http1"): "twp-reverse-http1-tls",
            ("http1-tls", "https-http1"): "twp-reverse-http1-mitm",
            ("http2", "http1"): "twp-reverse-http2-cleartext",
            ("http2", "https-http1"): "twp-reverse-http2-to-https-http1",
            ("http2", "https-http2"): "twp-reverse-http2",
            ("http3", "http3"): "twp-reverse-http3",
        }
        return special.get((c, o), f"twp-reverse-{c}-to-{o}")
    if prefix == "yarp":
        special = {
            ("http1", "http1"): "yarp-reverse-http1",
            ("http1-tls", "http1"): "yarp-reverse-http1-tls",
            ("http1-tls", "https-http1"): "yarp-reverse-http1-tls-to-https",
            ("http2", "https-http2"): "yarp-reverse-http2-to-https",
        }
        return special.get((c, o), f"yarp-reverse-{c}-to-{o}")
    peer_special = {
        ("http1", "http1"): f"{prefix}-reverse-http1",
        ("http1-tls", "http1"): f"{prefix}-reverse-http1-tls",
        ("http1-tls", "https-http1"): f"{prefix}-reverse-http1-tls-to-https",
        ("http2", "http1"): f"{prefix}-reverse-http2",
        ("http2", "https-http1"): f"{prefix}-reverse-http2-to-https-http1",
        ("http3", "http1"): f"{prefix}-reverse-http3-cleartext",
        ("http3", "https-http1"): f"{prefix}-reverse-http3-to-https-http1",
    }
    if prefix in ("nginx", "haproxy", "envoy"):
        return peer_special.get((c, o), f"{prefix}-reverse-{c}-to-{o}")
    raise ValueError(f"unknown prefix {prefix}")


OS_SPECS: List[Tuple[str, str, Tuple[str, ...], str]] = [
    (
        "windows",
        "Windows (`windows-latest`)",
        ("windows-latest", "windows", "win"),
        "Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP",
    ),
    (
        "linux",
        "Linux (`ubuntu-latest`)",
        ("ubuntu-latest", "linux", "ubuntu"),
        "Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP",
    ),
    (
        "macos",
        "macOS (`macos-15-intel`)",
        ("macos-15-intel", "macos", "osx", "darwin"),
        "macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP",
    ),
]

HEADING_TO_OS = {
    "Windows — Titanium vs nginx vs YARP": "windows",
    "Linux — Titanium vs nginx vs YARP": "linux",
    "macOS — Titanium vs nginx vs YARP": "macos",
    "Windows — Titanium vs nginx vs HAProxy vs Envoy vs YARP": "windows",
    "Linux — Titanium vs nginx vs HAProxy vs Envoy vs YARP": "linux",
    "macOS — Titanium vs nginx vs HAProxy vs Envoy vs YARP": "macos",
}

CELL_RE = re.compile(r"\*{0,2}(\d[\d,]*)")


def parse_rps_cell(cell: str) -> Optional[float]:
    t = cell.strip()
    if "Not possible" in t or t in ("", "—", "-"):
        return None
    # Duplex/H3 nginx 0 sustain with a non-zero peak is still "not a peer bar".
    m = CELL_RE.search(t.replace(",", ""))
    if not m:
        return None
    v = float(m.group(1).replace(",", ""))
    if v == 0:
        return None
    return v


def parse_wiki_reverse_tables(md: str) -> Dict[str, List[Row]]:
    """os_key → list of (client, origin, twp, nginx, haproxy, envoy, yarp)."""
    out: Dict[str, list] = {"windows": [], "linux": [], "macos": []}
    current_os: Optional[str] = None
    in_reverse = False
    in_table = False
    for line in md.splitlines():
        if line.startswith("## "):
            title = line[3:].strip()
            current_os = HEADING_TO_OS.get(title)
            in_reverse = False
            in_table = False
            continue
        if current_os is None:
            continue
        if line.startswith("### Reverse"):
            in_reverse = True
            in_table = False
            continue
        if line.startswith("### ") and not line.startswith("### Reverse"):
            in_reverse = False
            in_table = False
            continue
        if not in_reverse:
            continue
        if line.startswith("| Client | Origin |"):
            in_table = True
            continue
        if in_table and line.startswith("|---"):
            continue
        if in_table and line.startswith("|"):
            cols = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cols) < 12:
                continue
            client, origin = cols[0], cols[1]
            twp = parse_rps_cell(cols[2])
            nginx = parse_rps_cell(cols[4])
            haproxy = parse_rps_cell(cols[6])
            envoy = parse_rps_cell(cols[8])
            yarp = parse_rps_cell(cols[10])
            out[current_os].append((client, origin, twp, nginx, haproxy, envoy, yarp))
        elif in_table and not line.startswith("|"):
            in_table = False
    return out


def render_os(
    rows: Sequence[Row],
    os_title: str,
    out_path: Path,
    title_suffix: str,
) -> None:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np

    fig, axes = plt.subplots(5, 1, figsize=(12.5, 16.5), dpi=130, sharey=False)
    fig.suptitle(
        f"Reverse RPS — {os_title}" + (f" {title_suffix}" if title_suffix else ""),
        fontsize=13,
        y=0.995,
    )
    width = 0.14
    offsets = tuple((i - 2) * width for i in range(5))

    by_client: Dict[str, List[Tuple[str, Optional[float], Optional[float], Optional[float], Optional[float], Optional[float]]]] = {
        c: [] for c in CLIENTS
    }
    for client, origin, twp, nginx, haproxy, envoy, yarp in rows:
        if client in by_client:
            by_client[client].append((origin, twp, nginx, haproxy, envoy, yarp))

    for ax, client in zip(axes, CLIENTS):
        group = by_client.get(client) or []
        ordered = []
        lookup = {o: (t, n, h, e, y) for o, t, n, h, e, y in group}
        for origin in ORIGINS:
            if origin in lookup:
                ordered.append((origin, *lookup[origin]))
        labels = [o.replace("HTTP/", "H").replace(" · ", " ") for o, *_ in ordered]
        x = np.arange(len(labels), dtype=float)
        series = {
            "Titanium": [r[1] for r in ordered],
            "nginx": [r[2] for r in ordered],
            "HAProxy": [r[3] for r in ordered],
            "Envoy": [r[4] for r in ordered],
            "YARP": [r[5] for r in ordered],
        }
        ymax = 1.0
        for product, offset in zip(PRODUCTS, offsets):
            vals = series[product]
            heights = [0.0 if v is None else float(v) for v in vals]
            present = [v is not None for v in vals]
            bars = ax.bar(
                x + offset,
                [h if p else 0.0 for h, p in zip(heights, present)],
                width,
                label=product if ax is axes[0] else None,
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
        ax.set_title(f"Client {client}", loc="left", fontsize=10)
        ax.set_xticks(x)
        ax.set_xticklabels(labels, fontsize=8)
        ax.set_ylabel("RPS @ c=64")
        ax.set_ylim(0, ymax * 1.14)
        ax.yaxis.set_major_formatter(plt.FuncFormatter(lambda v, _: f"{int(v):,}"))
        ax.grid(axis="y", linestyle=":", alpha=0.45, zorder=0)
        ax.set_axisbelow(True)

    axes[0].legend(loc="upper right", framealpha=0.92, ncols=5, fontsize=8)
    fig.text(
        0.01,
        0.004,
        "Reverse only · TWP vs YARP vs nginx vs HAProxy vs Envoy · Not possible omitted · MITM Lite/Full stay tables-only",
        fontsize=8,
        color="#444444",
    )
    fig.tight_layout(rect=(0, 0.018, 1, 0.978))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_path, bbox_inches="tight")
    plt.close(fig)


def rows_from_csv(csv_path: Path) -> List[Row]:
    rows = []
    for client in CLIENTS:
        for origin in ORIGINS:
            twp = arm_sustain_c64(csv_path, arm_name("twp", client, origin))
            yarp = arm_sustain_c64(csv_path, arm_name("yarp", client, origin))
            nginx = arm_sustain_c64(csv_path, arm_name("nginx", client, origin))
            haproxy = arm_sustain_c64(csv_path, arm_name("haproxy", client, origin))
            envoy = arm_sustain_c64(csv_path, arm_name("envoy", client, origin))
            rows.append((client, origin, twp, nginx, haproxy, envoy, yarp))
    return rows


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--results-root", type=Path)
    ap.add_argument("--from-wiki", type=Path)
    ap.add_argument("--out-dir", type=Path, default=Path("wiki/images"))
    ap.add_argument("--title-suffix", default="")
    args = ap.parse_args()

    wiki_rows = None
    if args.from_wiki:
        wiki_rows = parse_wiki_reverse_tables(args.from_wiki.read_text(encoding="utf-8"))

    written = []
    for key, title, folder_keys, _heading in OS_SPECS:
        rows = None
        if args.results_root:
            csv_path = find_csv(args.results_root, folder_keys)
            if csv_path:
                rows = rows_from_csv(csv_path)
                print(f"{title} ← {csv_path}")
        if rows is None and wiki_rows is not None:
            rows = wiki_rows.get(key) or []
            if rows:
                print(f"{title} ← wiki table ({len(rows)} wires)")
        if not rows:
            print(f"{title}: no data", file=sys.stderr)
            continue
        out = args.out_dir / f"rps-product-reverse-{key}.png"
        render_os(rows, title, out, args.title_suffix.strip())
        written.append(out)
        print(f"  wrote {out}")

    if not written:
        print("No charts written.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
