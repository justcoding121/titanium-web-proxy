#!/usr/bin/env python3
"""Render practical reverse-proxy RPS grouped bar charts (TWP / YARP / nginx / HAProxy / Envoy).

Two PNGs per OS:
  - Tiny: eight industry reverse wires (tiny keep-alive GET ~56 B) plus WebSocket / gRPC
    (10 clusters). Filename rps-practical-{os}.png.
  - Heavier (64 KB): typical reverse body/POST clusters. Filename
    rps-practical-heavier-{os}.png. Skipped when every cluster is empty (e.g. macOS wiki).

Example (CSV):
  python3 tools/RpsLoadProbe/render-practical-charts.py \\
    --results-root tools/RpsLoadProbe/results/gha-dl/<productRunId> \\
    --bodies-root tools/RpsLoadProbe/results/gha-dl/<bodiesRunId> \\
    --post-root tools/RpsLoadProbe/results/gha-dl/<postRunId> \\
    --arch-root tools/RpsLoadProbe/results/gha-dl/<archRunId> \\
    --grpc-root tools/RpsLoadProbe/results/gha-dl/<grpcRunId> \\
    --out-dir wiki/images \\
    --title-suffix '@ <sha>'

Wiki fallback (current Performance.md reverse + heavier + gRPC tables):
  python3 tools/RpsLoadProbe/render-practical-charts.py \\
    --from-wiki wiki/Performance.md --out-dir wiki/images --title-suffix '@ <sha>'
"""

from __future__ import annotations

import argparse
import csv
import re
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple, Union

# Practical industry reverse wires (short labels → CSV arm names).
# Order: typical TLS-in→H1-out, H2 same-protocol, H3 terminate, then H3→h2c.
# nginx H2/H3 origin stays None (stock nginx has no H2/H3 upstream).
PRACTICAL_ARMS: List[Tuple[str, str, str, Optional[str], Optional[str], Optional[str]]] = [
    # label, twp, yarp, nginx, haproxy, envoy (None = product-impossible)
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
    (
        "H2 TLS→h2c",
        "twp-reverse-http2-to-h2c",
        "yarp-reverse-http2-to-h2c",
        None,
        "haproxy-reverse-http2-to-h2c",
        "envoy-reverse-http2-to-h2c",
    ),
    (
        "H2 TLS→H2 TLS",
        "twp-reverse-http2",
        "yarp-reverse-http2-to-https",
        None,
        "haproxy-reverse-http2-to-https",
        "envoy-reverse-http2-to-https",
    ),
    (
        "H3→H1c",
        "twp-reverse-http3-cleartext",
        "yarp-reverse-http3-cleartext",
        "nginx-reverse-http3-cleartext",
        "haproxy-reverse-http3-cleartext",
        "envoy-reverse-http3-cleartext",
    ),
    (
        "H3→h2c",
        "twp-reverse-http3-to-h2c",
        "yarp-reverse-http3-to-h2c",
        None,
        "haproxy-reverse-http3-to-h2c",
        "envoy-reverse-http3-to-h2c",
    ),
]

COLORS = {
    "Titanium": "#0B6E4F",
    "YARP": "#C45C26",
    "nginx": "#2F5D8C",
    "HAProxy": "#8B4513",
    "Envoy": "#6B5B95",
}

PRODUCTS = ("Titanium", "YARP", "nginx", "HAProxy", "Envoy")

# Workload clusters on the tiny chart (protocol mix, not body size).
INDUSTRY_WORKLOADS: List[Tuple[str, Dict[str, Optional[str]]]] = [
    (
        "WebSocket",
        {
            "Titanium": "twp-reverse-http1-tls-duplex-ws",
            "YARP": "yarp-reverse-http1-tls-duplex-ws",
            "nginx": "nginx-reverse-http1-tls-duplex-ws",
            "HAProxy": "haproxy-reverse-http1-tls-duplex-ws",
            "Envoy": "envoy-reverse-http1-tls-duplex-ws",
        },
    ),
    (
        "gRPC unary",
        {
            "Titanium": "twp-reverse-http2-grpc-unary",
            "YARP": "yarp-reverse-http2-grpc-unary",
            "nginx": "nginx-reverse-http2-grpc-unary",
            "HAProxy": "haproxy-reverse-http2-grpc-unary",
            "Envoy": "envoy-reverse-http2-grpc-unary",
        },
    ),
]

# Heavier practical chart: 64 KB GET/POST on typical reverse wires (+ 256 KB H1).
# label, twp, yarp, nginx, haproxy, envoy (None = product-impossible).
PRACTICAL_HEAVIER_ARMS: List[Tuple[str, str, str, Optional[str], Optional[str], Optional[str]]] = [
    (
        "GET 64 KB · H1 TLS→H1c",
        "twp-reverse-http1-tls-body64k",
        "yarp-reverse-http1-tls-body64k",
        "nginx-reverse-http1-tls-body64k",
        "haproxy-reverse-http1-tls-body64k",
        "envoy-reverse-http1-tls-body64k",
    ),
    (
        "GET 64 KB · H2 TLS→H1c",
        "twp-reverse-http2-cleartext-body64k",
        "yarp-reverse-http2-body64k",
        "nginx-reverse-http2-body64k",
        "haproxy-reverse-http2-body64k",
        "envoy-reverse-http2-body64k",
    ),
    (
        "GET 64 KB · H3→H1c",
        "twp-reverse-http3-cleartext-body64k",
        "yarp-reverse-http3-cleartext-body64k",
        "nginx-reverse-http3-cleartext-body64k",
        "haproxy-reverse-http3-cleartext-body64k",
        "envoy-reverse-http3-cleartext-body64k",
    ),
    (
        "GET 64 KB · H2 TLS→H2 TLS",
        "twp-reverse-http2-to-https-body64k",
        "yarp-reverse-http2-to-https-body64k",
        None,
        "haproxy-reverse-http2-to-https-body64k",
        "envoy-reverse-http2-to-https-body64k",
    ),
    (
        "POST 64 KB · H1 TLS→H1c",
        "twp-reverse-http1-tls-post64k",
        "yarp-reverse-http1-tls-post64k",
        "nginx-reverse-http1-tls-post64k",
        "haproxy-reverse-http1-tls-post64k",
        "envoy-reverse-http1-tls-post64k",
    ),
    (
        "GET 256 KB · H1 TLS→H1c",
        "twp-reverse-http1-tls-body256k",
        "yarp-reverse-http1-tls-body256k",
        "nginx-reverse-http1-tls-body256k",
        "haproxy-reverse-http1-tls-body256k",
        "envoy-reverse-http1-tls-body256k",
    ),
]

WIRE_COUNT = len(PRACTICAL_ARMS)
HEAVIER_COUNT = len(PRACTICAL_HEAVIER_ARMS)

OS_SPECS = (
    ("linux", "Linux", ("ubuntu-latest",)),
    ("windows", "Windows", ("windows-latest",)),
    ("macos", "macOS", ("macos-15-intel", "macos-latest")),
)

TINY_FOOTER = (
    "Wires: tiny keep-alive GET (~56 B) · Workloads: WebSocket / gRPC (RPC/s) · "
    "GitHub Actions 4-core / 16 GiB (macOS 14 GiB)"
)

HEAVIER_FOOTER = (
    "64 KB GET/POST on typical reverse wires (H1/H2/H3 terminate, H2 origin, "
    "256 KB H1) · GitHub Actions 4-core / 16 GiB (macOS 14 GiB)"
)

# Back-compat alias for importers (render-heavier-charts / product-matrix).
MERGED_FOOTER = TINY_FOOTER

# Practical wire label → (client, origin) cells in wiki Performance.md reverse tables.
WIKI_WIRE_CELLS: Dict[str, Tuple[str, str]] = {
    "H1 TLS→H1c": ("HTTP/1 · TLS", "HTTP/1 · plain"),
    "H1 TLS→H1 TLS": ("HTTP/1 · TLS", "HTTP/1 · TLS"),
    "H2 TLS→H1c": ("HTTP/2 · TLS", "HTTP/1 · plain"),
    "H2 TLS→H1 TLS": ("HTTP/2 · TLS", "HTTP/1 · TLS"),
    "H2 TLS→h2c": ("HTTP/2 · TLS", "HTTP/2 · plain"),
    "H2 TLS→H2 TLS": ("HTTP/2 · TLS", "HTTP/2 · TLS"),
    "H3→H1c": ("HTTP/3 · QUIC", "HTTP/1 · plain"),
    "H3→h2c": ("HTTP/3 · QUIC", "HTTP/2 · plain"),
}

# Heavier chart wiki cells: (body_or_None_for_post, client, origin) → cluster index.
WIKI_HEAVIER_CELLS: Dict[Tuple[Optional[str], str, str], int] = {
    ("64 KiB", "HTTP/1 · TLS", "HTTP/1 · plain"): 0,
    ("64 KiB", "HTTP/2 · TLS", "HTTP/1 · plain"): 1,
    ("64 KiB", "HTTP/3 · QUIC", "HTTP/1 · plain"): 2,
    ("64 KiB", "HTTP/2 · TLS", "HTTP/2 · TLS"): 3,
    (None, "HTTP/1 · TLS", "HTTP/1 · plain"): 4,  # POST table (no Body col)
    ("256 KiB", "HTTP/1 · TLS", "HTTP/1 · plain"): 5,
}

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
    if "Not possible" in t or "Not measured" in t or t in ("", "—", "-"):
        return None
    m = CELL_RE.search(t.replace(",", ""))
    if not m:
        return None
    v = float(m.group(1).replace(",", ""))
    if v == 0:
        return None
    return v


def _product_row_from_cols(cols: Sequence[str], *, offset: int = 0) -> Dict[str, Optional[float]]:
    """Parse TWP/nginx/HAProxy/Envoy/YARP sustain columns (offset skips Scenario)."""
    # cols: [Client, Origin, TWP, TWP peak, nginx, nginx peak, HAProxy, ..., YARP, YARP peak]
    i = offset
    return {
        "Titanium": parse_rps_cell(cols[i + 2]),
        "nginx": parse_rps_cell(cols[i + 4]),
        "HAProxy": parse_rps_cell(cols[i + 6]),
        "Envoy": parse_rps_cell(cols[i + 8]),
        "YARP": parse_rps_cell(cols[i + 10]),
    }


def parse_wiki_practical(md: str) -> Dict[str, Dict[str, List[Optional[float]]]]:
    """Build per-OS product series aligned with PRACTICAL_ARMS + INDUSTRY_WORKLOADS."""
    empty = {p: [None] * (WIRE_COUNT + len(INDUSTRY_WORKLOADS)) for p in PRODUCTS}
    out: Dict[str, Dict[str, List[Optional[float]]]] = {
        "windows": {p: list(empty[p]) for p in PRODUCTS},
        "linux": {p: list(empty[p]) for p in PRODUCTS},
        "macos": {p: list(empty[p]) for p in PRODUCTS},
    }

    wire_index = {label: i for i, (label, *_rest) in enumerate(PRACTICAL_ARMS)}
    ws_idx = WIRE_COUNT + 0
    grpc_idx = WIRE_COUNT + 1

    # --- reverse 5×5 → practical wires ---
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
            vals = _product_row_from_cols(cols)
            for label, cell in WIKI_WIRE_CELLS.items():
                if (client, origin) != cell:
                    continue
                idx = wire_index[label]
                for product in PRODUCTS:
                    out[current_os][product][idx] = vals[product]
        elif in_table and not line.startswith("|"):
            in_table = False

    # --- WebSocket from architecture-sensitive ---
    arch_os: Optional[str] = None
    in_arch_table = False
    for line in md.splitlines():
        if line.startswith("#### Windows"):
            arch_os = "windows"
            in_arch_table = False
            continue
        if line.startswith("#### Linux"):
            arch_os = "linux"
            in_arch_table = False
            continue
        if line.startswith("#### ") and arch_os is not None:
            arch_os = None
            in_arch_table = False
            continue
        if arch_os is None:
            continue
        if line.startswith("| Scenario | Client | Origin |"):
            in_arch_table = True
            continue
        if in_arch_table and line.startswith("|---"):
            continue
        if in_arch_table and line.startswith("|"):
            cols = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cols) < 13:
                continue
            if "WebSocket" in cols[0]:
                vals = _product_row_from_cols(cols, offset=1)
                for product in PRODUCTS:
                    out[arch_os][product][ws_idx] = vals[product]
        elif in_arch_table and not line.startswith("|"):
            in_arch_table = False

    # --- Unary gRPC ---
    in_grpc = False
    in_grpc_table = False
    grpc_os_map = {"Windows": "windows", "Linux": "linux", "macOS": "macos"}
    for line in md.splitlines():
        if line.startswith("## Unary gRPC"):
            in_grpc = True
            in_grpc_table = False
            continue
        if in_grpc and line.startswith("## ") and not line.startswith("## Unary gRPC"):
            break
        if not in_grpc:
            continue
        if line.startswith("| OS |"):
            in_grpc_table = True
            continue
        if in_grpc_table and line.startswith("|---"):
            continue
        if in_grpc_table and line.startswith("|"):
            cols = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cols) < 6:
                continue
            key = grpc_os_map.get(cols[0])
            if not key:
                continue
            out[key]["Titanium"][grpc_idx] = parse_rps_cell(cols[1])
            out[key]["YARP"][grpc_idx] = parse_rps_cell(cols[2])
            out[key]["nginx"][grpc_idx] = parse_rps_cell(cols[3])
            out[key]["HAProxy"][grpc_idx] = parse_rps_cell(cols[4])
            out[key]["Envoy"][grpc_idx] = parse_rps_cell(cols[5])
        elif in_grpc_table and not line.startswith("|"):
            in_grpc_table = False

    return out


def parse_wiki_heavier(md: str) -> Dict[str, Dict[str, List[Optional[float]]]]:
    """Build per-OS product series aligned with PRACTICAL_HEAVIER_ARMS from wiki tables."""
    empty = {p: [None] * HEAVIER_COUNT for p in PRODUCTS}
    out: Dict[str, Dict[str, List[Optional[float]]]] = {
        "windows": {p: list(empty[p]) for p in PRODUCTS},
        "linux": {p: list(empty[p]) for p in PRODUCTS},
        "macos": {p: list(empty[p]) for p in PRODUCTS},
    }

    # --- heavier reverse GET ---
    body_os: Optional[str] = None
    in_body_table = False
    for line in md.splitlines():
        if line.startswith("### Windows — heavier reverse GET"):
            body_os = "windows"
            in_body_table = False
            continue
        if line.startswith("### Linux — heavier reverse GET"):
            body_os = "linux"
            in_body_table = False
            continue
        if line.startswith("### ") and body_os is not None and "heavier reverse GET" not in line:
            body_os = None
            in_body_table = False
            continue
        if body_os is None:
            continue
        if line.startswith("| Body | Client | Origin |") or line.startswith("| Size | Client | Origin |"):
            in_body_table = True
            continue
        if in_body_table and line.startswith("|---"):
            continue
        if in_body_table and line.startswith("|"):
            cols = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cols) < 13:
                continue
            key = (cols[0], cols[1], cols[2])
            idx = WIKI_HEAVIER_CELLS.get(key)
            if idx is None:
                continue
            vals = _product_row_from_cols(cols, offset=1)
            for product in PRODUCTS:
                out[body_os][product][idx] = vals[product]
        elif in_body_table and not line.startswith("|"):
            in_body_table = False

    # --- POST 64 KiB (H1 TLS→H1c only for practical heavier chart) ---
    post_os: Optional[str] = None
    in_post_table = False
    for line in md.splitlines():
        if line.startswith("### Windows — POST"):
            post_os = "windows"
            in_post_table = False
            continue
        if line.startswith("### Linux — POST"):
            post_os = "linux"
            in_post_table = False
            continue
        if line.startswith("### ") and post_os is not None and "POST" not in line:
            post_os = None
            in_post_table = False
            continue
        if post_os is None:
            continue
        if line.startswith("| Client | Origin |"):
            in_post_table = True
            continue
        if in_post_table and line.startswith("|---"):
            continue
        if in_post_table and line.startswith("|"):
            cols = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cols) < 12:
                continue
            key = (None, cols[0], cols[1])
            idx = WIKI_HEAVIER_CELLS.get(key)
            if idx is None:
                continue
            vals = _product_row_from_cols(cols)
            for product in PRODUCTS:
                out[post_os][product][idx] = vals[product]
        elif in_post_table and not line.startswith("|"):
            in_post_table = False

    return out


def series_has_any(series: Dict[str, List[Optional[float]]]) -> bool:
    """True if any product has a positive sustain value."""
    for product in PRODUCTS:
        for v in series.get(product) or []:
            if v is not None and float(v) > 0:
                return True
    return False


def median(vals: Sequence[float]) -> Optional[float]:
    if not vals:
        return None
    s = sorted(vals)
    return s[(len(s) - 1) // 2]


def plot_packed_product_bars(
    ax,
    series: Dict[str, List[Optional[float]]],
    x,
    *,
    products: Sequence[str] = PRODUCTS,
    colors: Optional[Dict[str, str]] = None,
    bar_width: float = 0.14,
    legend_once: bool = True,
    add_legend_labels: bool = True,
) -> float:
    """Draw per-cluster packed bars; omit None and <=0 so no empty slots remain.

    Surviving peers keep a constant bar width and are packed edge-to-edge, then
    centered on the cluster. Missing 0 / n/a slots collapse; remaining bars do
    not grow to fill the five-product group.
    """
    palette = colors or COLORS
    labeled: set = set()
    ymax = 1.0
    for i, xi in enumerate(x):
        present: List[Tuple[str, float]] = []
        for product in products:
            vals = series.get(product)
            if not vals or i >= len(vals):
                continue
            v = vals[i]
            if v is None:
                continue
            h = float(v)
            if h <= 0:
                continue
            present.append((product, h))
        n = len(present)
        if n == 0:
            continue
        start = float(xi) - (n - 1) * bar_width / 2.0
        for j, (product, h) in enumerate(present):
            label = None
            if add_legend_labels:
                if legend_once:
                    if product not in labeled:
                        label = product
                        labeled.add(product)
                else:
                    label = product
            ax.bar(
                start + j * bar_width,
                h,
                bar_width,
                label=label,
                color=palette[product],
                edgecolor="white",
                linewidth=0.4,
                zorder=3,
            )
            ymax = max(ymax, h)
    return ymax


def arm_sustain_c64(csv_path: Path, arm: str) -> Optional[float]:
    """Sustain RPS @ c=64. Median of SLO-passing repeats; 0 if all c=64 steps miss SLO."""
    rows = [r for r in csv.DictReader(csv_path.open(newline="")) if r.get("arm") == arm]
    if not rows:
        return None
    # Group into ramp chunks of 4 concurrency steps when present; else last c=64 per pass.
    steps = 4
    sustains: List[float] = []
    saw_c64 = False
    i = 0
    while i + steps <= len(rows):
        chunk = rows[i : i + steps]
        c64_ok = [r for r in chunk if r.get("concurrency") == "64" and r.get("meets_slo") == "1"]
        if c64_ok:
            sustains.append(float(c64_ok[-1]["rps"]))
            saw_c64 = True
        else:
            c64_any = [r for r in chunk if r.get("concurrency") == "64"]
            if c64_any:
                saw_c64 = True
        i += steps
    if sustains:
        return median(sustains)
    # Misaligned repeats (e.g. a pass missing c=64) — still use any SLO-pass c=64 rows.
    ok_any = [
        float(r["rps"])
        for r in rows
        if r.get("concurrency") == "64" and r.get("meets_slo") == "1"
    ]
    if ok_any:
        return median(ok_any)
    if not saw_c64:
        return None
    return 0.0


def parse_path_list(values: Optional[Sequence[Union[Path, str]]]) -> List[Path]:
    """Expand repeated flags and comma-separated paths."""
    out: List[Path] = []
    for v in values or []:
        for part in str(v).split(","):
            part = part.strip()
            if part:
                out.append(Path(part))
    return out


def find_csv(results_root: Path, os_keys: Iterable[str]) -> Optional[Path]:
    for key in os_keys:
        for pattern in (
            f"rps-csv-{key}/*.csv",
            f"rps-csv-{key}-*/*.csv",
            f"{key}/*.csv",
            f"**/*{key}*/**/*.csv",
            f"**/*{key}*.csv",
        ):
            hits = sorted(results_root.glob(pattern))
            if hits:
                return hits[0]
    return None


def find_csvs(results_roots: Sequence[Path], os_keys: Iterable[str]) -> List[Path]:
    seen: set = set()
    out: List[Path] = []
    for root in results_roots:
        found = find_csv(root, os_keys)
        if found and found not in seen:
            seen.add(found)
            out.append(found)
    return out


def gha_dl_sibling_roots(results_root: Path) -> List[Path]:
    """Other gha-dl/<runId> folders beside a primary run (workload CSV defaults)."""
    run_dir = results_root
    if not run_dir.is_dir():
        run_dir = results_root.parent
    gha_dl = run_dir.parent
    if gha_dl.name != "gha-dl":
        return [run_dir]
    return sorted(p for p in gha_dl.iterdir() if p.is_dir())


def arm_sustain_union(csv_paths: Sequence[Path], arm: Optional[str]) -> Optional[float]:
    """Best sustain across CSVs (overlay peer-fix runs over older 0-RPS product rows)."""
    if arm is None:
        return None
    best: Optional[float] = None
    for path in csv_paths:
        val = arm_sustain_c64(path, arm)
        if val is None:
            continue
        if best is None or val > best:
            best = val
    return best


def grpc_arm_union(csv_paths: Sequence[Path], prefix: str, fallback: Optional[str]) -> Optional[str]:
    """Resolve a *-grpc-* arm; prefer <prefix>-reverse-http2-grpc-unary when present."""
    preferred = fallback or f"{prefix}-reverse-http2-grpc-unary"
    for path in csv_paths:
        arms = {r.get("arm") for r in csv.DictReader(path.open(newline="")) if r.get("arm")}
        if preferred in arms:
            return preferred
        matches = sorted(a for a in arms if a.startswith(f"{prefix}-") and "-grpc-" in a)
        if matches:
            return matches[0]
    return fallback


def collect_series(csv_paths: Sequence[Path]) -> Dict[str, List[Optional[float]]]:
    """Return product → list of RPS aligned with PRACTICAL_ARMS (None = missing)."""
    out: Dict[str, List[Optional[float]]] = {p: [] for p in PRODUCTS}
    for _label, twp, yarp, nginx, haproxy, envoy in PRACTICAL_ARMS:
        out["Titanium"].append(arm_sustain_union(csv_paths, twp))
        out["YARP"].append(arm_sustain_union(csv_paths, yarp))
        for product, arm in (("nginx", nginx), ("HAProxy", haproxy), ("Envoy", envoy)):
            if arm is None:
                out[product].append(None)
            else:
                out[product].append(arm_sustain_union(csv_paths, arm))
    return out


def collect_industry_series(
    arch_csvs: Sequence[Path],
    grpc_csvs: Sequence[Path],
) -> Dict[str, List[Optional[float]]]:
    """WebSocket / gRPC @ c=64 from mode-specific CSV unions."""
    sources = {
        "WebSocket": arch_csvs,
        "gRPC unary": grpc_csvs,
    }
    prefix_map = {
        "Titanium": "twp",
        "YARP": "yarp",
        "nginx": "nginx",
        "HAProxy": "haproxy",
        "Envoy": "envoy",
    }
    out: Dict[str, List[Optional[float]]] = {p: [] for p in PRODUCTS}
    for label, arms in INDUSTRY_WORKLOADS:
        csv_paths = sources[label]
        for product in PRODUCTS:
            arm = arms.get(product)
            if label == "gRPC unary":
                arm = grpc_arm_union(csv_paths, prefix_map[product], arm)
            out[product].append(arm_sustain_union(csv_paths, arm))
    return out


def collect_heavier_series(
    bodies_csvs: Sequence[Path],
    post_csvs: Sequence[Path],
) -> Dict[str, List[Optional[float]]]:
    """Return product → list of RPS aligned with PRACTICAL_HEAVIER_ARMS."""
    out: Dict[str, List[Optional[float]]] = {p: [] for p in PRODUCTS}
    for label, twp, yarp, nginx, haproxy, envoy in PRACTICAL_HEAVIER_ARMS:
        csv_paths = post_csvs if "POST" in label else bodies_csvs
        out["Titanium"].append(arm_sustain_union(csv_paths, twp))
        out["YARP"].append(arm_sustain_union(csv_paths, yarp))
        for product, arm in (("nginx", nginx), ("HAProxy", haproxy), ("Envoy", envoy)):
            if arm is None:
                out[product].append(None)
            else:
                out[product].append(arm_sustain_union(csv_paths, arm))
    return out


def merge_series(
    wire: Dict[str, List[Optional[float]]],
    workload: Dict[str, List[Optional[float]]],
) -> Dict[str, List[Optional[float]]]:
    return {p: list(wire[p]) + list(workload[p]) for p in PRODUCTS}


def render_chart(
    series: Dict[str, List[Optional[float]]],
    os_title: str,
    out_path: Path,
    title_suffix: str,
    labels: Optional[Sequence[str]] = None,
    footer: Optional[str] = None,
    workload_start: Optional[int] = None,
    title_template: str = "Reverse proxy RPS — {os}",
) -> None:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np

    labels = list(labels) if labels is not None else [a[0] for a in PRACTICAL_ARMS]
    x = np.arange(len(labels), dtype=float)

    fig_w = 16.0 if len(labels) >= 10 else 14.5
    fig, ax = plt.subplots(figsize=(fig_w, 5.8), dpi=140)
    ymax = plot_packed_product_bars(ax, series, x)

    if workload_start is not None and 0 < workload_start < len(labels):
        ax.axvline(
            workload_start - 0.5,
            color="#888888",
            linestyle="--",
            linewidth=1.0,
            zorder=2,
            alpha=0.7,
        )

    ax.set_ylabel("Sustain RPS @ concurrency 64", fontsize=13)
    title = title_template.format(os=os_title)
    if title_suffix:
        title = f"{title} {title_suffix}"
    ax.set_title(title, fontsize=15)
    ax.set_xticks(x)
    ax.set_xticklabels(labels, rotation=18, ha="right", fontsize=12)
    ax.tick_params(axis="y", labelsize=12)
    ax.set_ylim(0, ymax * 1.12)
    ax.yaxis.set_major_formatter(plt.FuncFormatter(lambda v, _: f"{int(v):,}"))
    ax.grid(axis="y", linestyle=":", alpha=0.45, zorder=0)
    ax.legend(
        loc="upper right",
        framealpha=0.92,
        ncols=5,
        fontsize=13,
        handlelength=1.8,
        handleheight=1.4,
        borderpad=0.6,
        labelspacing=0.45,
        columnspacing=1.4,
        handletextpad=0.5,
    )
    ax.set_axisbelow(True)
    fig.text(
        0.01,
        0.01,
        footer or TINY_FOOTER,
        fontsize=12,
        color="#444444",
    )
    fig.tight_layout(rect=(0, 0.06, 1, 1))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_path, bbox_inches="tight")
    plt.close(fig)


def _print_series(labels: Sequence[str], series: Dict[str, List[Optional[float]]]) -> None:
    for i, label in enumerate(labels):
        parts = []
        for product in PRODUCTS:
            v = series[product][i] if i < len(series[product]) else None
            parts.append("n/a" if v is None else f"{v:.0f}")
        print(
            f"  {label}: TWP={parts[0]} YARP={parts[1]} nginx={parts[2]} "
            f"HAProxy={parts[3]} Envoy={parts[4]}"
        )


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--results-root",
        action="append",
        default=[],
        help="gha-dl/<runId> folder(s) for compare-product; comma-separated or repeat flag",
    )
    ap.add_argument(
        "--from-wiki",
        type=Path,
        help="Render from wiki/Performance.md reverse + heavier + gRPC tables (no CSV)",
    )
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
    ap.add_argument(
        "--bodies-root",
        action="append",
        default=[],
        help="compare-bodies run root(s) for practical heavier GET clusters",
    )
    ap.add_argument(
        "--post-root",
        action="append",
        default=[],
        help="compare-post run root(s) for practical heavier POST cluster",
    )
    ap.add_argument(
        "--arch-root",
        action="append",
        default=[],
        help="compare-arch run root(s) for WebSocket cluster",
    )
    ap.add_argument(
        "--grpc-root",
        action="append",
        default=[],
        help="compare-grpc run root(s) for gRPC unary cluster",
    )
    args = ap.parse_args()

    suffix = args.title_suffix.strip()
    written: List[Path] = []
    tiny_labels = [a[0] for a in PRACTICAL_ARMS] + [w[0] for w in INDUSTRY_WORKLOADS]
    heavier_labels = [a[0] for a in PRACTICAL_HEAVIER_ARMS]
    heavier_title = "Reverse proxy RPS — {os} 64 KB"

    if args.from_wiki:
        md = args.from_wiki.read_text(encoding="utf-8")
        by_os = parse_wiki_practical(md)
        by_os_heavier = parse_wiki_heavier(md)
        wanted = set(args.os) if args.os else {"linux", "windows", "macos"}
        for key, title, _folder_keys in OS_SPECS:
            if key not in wanted:
                continue
            series = by_os[key]
            out = args.out_dir / f"rps-practical-{key}.png"
            render_chart(
                series,
                title,
                out,
                suffix,
                labels=tiny_labels,
                footer=TINY_FOOTER,
                workload_start=WIRE_COUNT,
            )
            written.append(out)
            print(f"{title} (tiny) <- wiki {args.from_wiki}")
            _print_series(tiny_labels, series)
            print(f"  wrote {out}")

            heavier = by_os_heavier[key]
            if not series_has_any(heavier):
                print(f"{title} (64 KB) <- wiki: skip (no data)")
                continue
            out_h = args.out_dir / f"rps-practical-heavier-{key}.png"
            render_chart(
                heavier,
                title,
                out_h,
                suffix,
                labels=heavier_labels,
                footer=HEAVIER_FOOTER,
                title_template=heavier_title,
            )
            written.append(out_h)
            print(f"{title} (64 KB) <- wiki {args.from_wiki}")
            _print_series(heavier_labels, heavier)
            print(f"  wrote {out_h}")
        if not written:
            print("No charts written.", file=__import__("sys").stderr)
            return 2
        return 0

    results_roots = parse_path_list(args.results_root)
    csv_lists_by_os: Dict[str, List[Path]] = {}
    if args.csv_linux:
        csv_lists_by_os["linux"] = [args.csv_linux]
    if args.csv_windows:
        csv_lists_by_os["windows"] = [args.csv_windows]
    if args.csv_macos:
        csv_lists_by_os["macos"] = [args.csv_macos]
    if results_roots:
        for key, _title, folder_keys in OS_SPECS:
            if key in csv_lists_by_os:
                continue
            found = find_csvs(results_roots, folder_keys)
            if found:
                csv_lists_by_os[key] = found

    if not csv_lists_by_os:
        ap.error(
            "Provide --from-wiki, --results-root, and/or --csv-linux/--csv-windows/--csv-macos"
        )

    sibling_roots = gha_dl_sibling_roots(results_roots[0]) if results_roots else []
    bodies_roots = parse_path_list(args.bodies_root) or results_roots or sibling_roots
    post_roots = parse_path_list(args.post_root) or results_roots or sibling_roots
    arch_roots = parse_path_list(args.arch_root) or results_roots or sibling_roots
    grpc_roots = parse_path_list(args.grpc_root) or results_roots or sibling_roots

    wanted = set(args.os) if args.os else set(csv_lists_by_os)

    for key, title, folder_keys in OS_SPECS:
        if key not in wanted or key not in csv_lists_by_os:
            continue
        paths = csv_lists_by_os[key]
        bodies_csvs = find_csvs(bodies_roots, folder_keys)
        post_csvs = find_csvs(post_roots, folder_keys)
        arch_csvs = find_csvs(arch_roots, folder_keys)
        grpc_csvs = find_csvs(grpc_roots, folder_keys)
        wire = collect_series(paths)
        workload = collect_industry_series(arch_csvs, grpc_csvs)
        series = merge_series(wire, workload)
        out = args.out_dir / f"rps-practical-{key}.png"
        render_chart(
            series,
            title,
            out,
            suffix,
            labels=tiny_labels,
            footer=TINY_FOOTER,
            workload_start=WIRE_COUNT,
        )
        written.append(out)
        src = ", ".join(str(p) for p in paths)
        print(f"{title} (tiny) <- product: {src}")
        print(f"  workloads <- arch={len(arch_csvs)} grpc={len(grpc_csvs)} CSV(s)")
        _print_series(tiny_labels, series)
        print(f"  wrote {out}")

        heavier = collect_heavier_series(bodies_csvs, post_csvs)
        if not series_has_any(heavier):
            print(f"{title} (64 KB) <- skip (no bodies/post data)")
            continue
        out_h = args.out_dir / f"rps-practical-heavier-{key}.png"
        render_chart(
            heavier,
            title,
            out_h,
            suffix,
            labels=heavier_labels,
            footer=HEAVIER_FOOTER,
            title_template=heavier_title,
        )
        written.append(out_h)
        print(
            f"{title} (64 KB) <- bodies={len(bodies_csvs)} post={len(post_csvs)} CSV(s)"
        )
        _print_series(heavier_labels, heavier)
        print(f"  wrote {out_h}")

    if not written:
        print("No charts written (no matching CSVs).", file=__import__("sys").stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
