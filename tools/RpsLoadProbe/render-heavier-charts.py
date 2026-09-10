#!/usr/bin/env python3
"""Render heavier reverse-proxy RPS grouped bar charts (TWP / YARP / nginx / HAProxy / Envoy).

Windows + Linux only. Same sustain @ c=64 / median rule as render-practical-charts.py.
Arm names match paste-heavier-wiki.py. Windows omits HAProxy/Envoy bars (no official port).

Example:
  python3 tools/RpsLoadProbe/render-heavier-charts.py \\
    --bodies-root tools/RpsLoadProbe/results/gha-dl/34126816180 \\
    --post-root tools/RpsLoadProbe/results/gha-dl/34126819267 \\
    --lossy-root tools/RpsLoadProbe/results/gha-dl/34126822092 \\
    --tls-root tools/RpsLoadProbe/results/gha-dl/34126826292 \\
    --arch-root tools/RpsLoadProbe/results/gha-dl/34126829362 \\
    --out-dir wiki/images \\
    --title-suffix '@ 024bd68d'
"""

from __future__ import annotations

import argparse
import importlib.util
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

Arm6 = Tuple[str, str, str, Optional[str], Optional[str], Optional[str]]

OS_SPECS = (
    ("windows", "Windows", ("windows-latest",), False),
    ("linux", "Linux", ("ubuntu-latest",), True),
)


# Arm tuples: label, twp, nginx, haproxy, envoy, yarp.
BODY_ARMS: List[Arm6] = [
    (
        "64 KiB · H1 TLS",
        "twp-reverse-http1-tls-body64k",
        "nginx-reverse-http1-tls-body64k",
        "haproxy-reverse-http1-tls-body64k",
        "envoy-reverse-http1-tls-body64k",
        "yarp-reverse-http1-tls-body64k",
    ),
    (
        "64 KiB · H2 TLS",
        "twp-reverse-http2-cleartext-body64k",
        "nginx-reverse-http2-body64k",
        "haproxy-reverse-http2-body64k",
        "envoy-reverse-http2-body64k",
        "yarp-reverse-http2-body64k",
    ),
    (
        "64 KiB · H3",
        "twp-reverse-http3-cleartext-body64k",
        "nginx-reverse-http3-cleartext-body64k",
        "haproxy-reverse-http3-cleartext-body64k",
        "envoy-reverse-http3-cleartext-body64k",
        "yarp-reverse-http3-cleartext-body64k",
    ),
    (
        "256 KiB · H1 TLS",
        "twp-reverse-http1-tls-body256k",
        "nginx-reverse-http1-tls-body256k",
        "haproxy-reverse-http1-tls-body256k",
        "envoy-reverse-http1-tls-body256k",
        "yarp-reverse-http1-tls-body256k",
    ),
    (
        "256 KiB · H2 TLS",
        "twp-reverse-http2-cleartext-body256k",
        "nginx-reverse-http2-body256k",
        "haproxy-reverse-http2-body256k",
        "envoy-reverse-http2-body256k",
        "yarp-reverse-http2-body256k",
    ),
    (
        "256 KiB · H3",
        "twp-reverse-http3-cleartext-body256k",
        "nginx-reverse-http3-cleartext-body256k",
        "haproxy-reverse-http3-cleartext-body256k",
        "envoy-reverse-http3-cleartext-body256k",
        "yarp-reverse-http3-cleartext-body256k",
    ),
]

BODY_REMAINDER_ARMS: List[Arm6] = [
    (
        "64k · h2c→H1",
        "twp-reverse-h2c-to-h1-body64k",
        "nginx-reverse-h2c-to-h1-body64k",
        "haproxy-reverse-h2c-to-h1-body64k",
        "envoy-reverse-h2c-to-h1-body64k",
        "yarp-reverse-h2c-to-h1-body64k",
    ),
    (
        "64k · H2→h2c",
        "twp-reverse-http2-to-h2c-body64k",
        None,
        "haproxy-reverse-http2-to-h2c-body64k",
        "envoy-reverse-http2-to-h2c-body64k",
        "yarp-reverse-http2-to-h2c-body64k",
    ),
    (
        "64k · H2→H2",
        "twp-reverse-http2-to-https-body64k",
        None,
        "haproxy-reverse-http2-to-https-body64k",
        "envoy-reverse-http2-to-https-body64k",
        "yarp-reverse-http2-to-https-body64k",
    ),
    (
        "64k · H3→H2",
        "twp-reverse-http3-to-http2-body64k",
        None,
        "haproxy-reverse-http3-to-http2-body64k",
        "envoy-reverse-http3-to-http2-body64k",
        "yarp-reverse-http3-to-http2-body64k",
    ),
    (
        "64k · H3→H1 TLS",
        "twp-reverse-http3-to-https-http1-body64k",
        "nginx-reverse-http3-to-https-http1-body64k",
        "haproxy-reverse-http3-to-https-http1-body64k",
        "envoy-reverse-http3-to-https-http1-body64k",
        "yarp-reverse-http3-to-https-http1-body64k",
    ),
]

POST_ARMS: List[Arm6] = [
    (
        "H1 TLS",
        "twp-reverse-http1-tls-post64k",
        "nginx-reverse-http1-tls-post64k",
        "haproxy-reverse-http1-tls-post64k",
        "envoy-reverse-http1-tls-post64k",
        "yarp-reverse-http1-tls-post64k",
    ),
    (
        "H2 TLS",
        "twp-reverse-http2-cleartext-post64k",
        "nginx-reverse-http2-post64k",
        "haproxy-reverse-http2-post64k",
        "envoy-reverse-http2-post64k",
        "yarp-reverse-http2-post64k",
    ),
    (
        "H3",
        "twp-reverse-http3-cleartext-post64k",
        "nginx-reverse-http3-cleartext-post64k",
        "haproxy-reverse-http3-cleartext-post64k",
        "envoy-reverse-http3-cleartext-post64k",
        "yarp-reverse-http3-cleartext-post64k",
    ),
    (
        "h2c→H1",
        "twp-reverse-h2c-to-h1-post64k",
        "nginx-reverse-h2c-to-h1-post64k",
        "haproxy-reverse-h2c-to-h1-post64k",
        "envoy-reverse-h2c-to-h1-post64k",
        "yarp-reverse-h2c-to-h1-post64k",
    ),
    (
        "H2→H2",
        "twp-reverse-http2-to-https-post64k",
        None,
        "haproxy-reverse-http2-to-https-post64k",
        "envoy-reverse-http2-to-https-post64k",
        "yarp-reverse-http2-to-https-post64k",
    ),
]

LOSSY_ARMS: List[Arm6] = [
    (
        "H1 TLS",
        "twp-reverse-http1-tls-lossy",
        "nginx-reverse-http1-tls-lossy",
        "haproxy-reverse-http1-tls-lossy",
        "envoy-reverse-http1-tls-lossy",
        "yarp-reverse-http1-tls-lossy",
    ),
    (
        "H2 TLS",
        "twp-reverse-http2-cleartext-lossy",
        "nginx-reverse-http2-lossy",
        "haproxy-reverse-http2-lossy",
        "envoy-reverse-http2-lossy",
        "yarp-reverse-http2-lossy",
    ),
    (
        "H3",
        "twp-reverse-http3-cleartext-lossy",
        "nginx-reverse-http3-cleartext-lossy",
        "haproxy-reverse-http3-cleartext-lossy",
        "envoy-reverse-http3-cleartext-lossy",
        "yarp-reverse-http3-cleartext-lossy",
    ),
]

TLS_ARMS: List[Arm6] = [
    (
        "Keep-alive · tiny GET",
        "twp-reverse-http1-tls-ka-tiny",
        "nginx-reverse-http1-tls-ka-tiny",
        "haproxy-reverse-http1-tls-ka-tiny",
        "envoy-reverse-http1-tls-ka-tiny",
        "yarp-reverse-http1-tls-ka-tiny",
    ),
    (
        "New-connection · tiny GET",
        "twp-reverse-http1-tls-nc-tiny",
        "nginx-reverse-http1-tls-nc-tiny",
        "haproxy-reverse-http1-tls-nc-tiny",
        "envoy-reverse-http1-tls-nc-tiny",
        "yarp-reverse-http1-tls-nc-tiny",
    ),
    (
        "Keep-alive · 256 KiB GET",
        "twp-reverse-http1-tls-ka-256k",
        "nginx-reverse-http1-tls-ka-256k",
        "haproxy-reverse-http1-tls-ka-256k",
        "envoy-reverse-http1-tls-ka-256k",
        "yarp-reverse-http1-tls-ka-256k",
    ),
]

# H2 duplex uses HAProxy/Envoy H2 TLS→H2 TLS (nginx has no H2 upstream).
ARCH_ARMS: List[Arm6] = [
    (
        "Slow · H1 TLS",
        "twp-reverse-http1-tls-slow256k",
        "nginx-reverse-http1-tls-slow256k",
        "haproxy-reverse-http1-tls-slow256k",
        "envoy-reverse-http1-tls-slow256k",
        "yarp-reverse-http1-tls-slow256k",
    ),
    (
        "Slow · H2 TLS",
        "twp-reverse-http2-cleartext-slow256k",
        "nginx-reverse-http2-slow256k",
        "haproxy-reverse-http2-slow256k",
        "envoy-reverse-http2-slow256k",
        "yarp-reverse-http2-slow256k",
    ),
    (
        "Slow · H3",
        "twp-reverse-http3-cleartext-slow256k",
        "nginx-reverse-http3-cleartext-slow256k",
        "haproxy-reverse-http3-cleartext-slow256k",
        "envoy-reverse-http3-cleartext-slow256k",
        "yarp-reverse-http3-cleartext-slow256k",
    ),
    (
        "Early · H1 TLS",
        "twp-reverse-http1-tls-early64k",
        "nginx-reverse-http1-tls-early64k",
        "haproxy-reverse-http1-tls-early64k",
        "envoy-reverse-http1-tls-early64k",
        "yarp-reverse-http1-tls-early64k",
    ),
    (
        "Early · H2 TLS",
        "twp-reverse-http2-cleartext-early64k",
        "nginx-reverse-http2-early64k",
        "haproxy-reverse-http2-early64k",
        "envoy-reverse-http2-early64k",
        "yarp-reverse-http2-early64k",
    ),
    (
        "Early · H3",
        "twp-reverse-http3-cleartext-early64k",
        "nginx-reverse-http3-cleartext-early64k",
        "haproxy-reverse-http3-cleartext-early64k",
        "envoy-reverse-http3-cleartext-early64k",
        "yarp-reverse-http3-cleartext-early64k",
    ),
    (
        "WebSocket · H1 TLS",
        "twp-reverse-http1-tls-duplex-ws",
        "nginx-reverse-http1-tls-duplex-ws",
        "haproxy-reverse-http1-tls-duplex-ws",
        "envoy-reverse-http1-tls-duplex-ws",
        "yarp-reverse-http1-tls-duplex-ws",
    ),
    (
        "Duplex H2 TLS↔H2",
        "twp-reverse-http2-duplex-h2",
        None,
        "haproxy-reverse-http2-duplex-h2",
        "envoy-reverse-http2-duplex-h2",
        "yarp-reverse-http2-to-https-duplex-h2",
    ),
]


def collect_series(
    csv_path: Path,
    arms: Sequence[Arm6],
    include_haproxy_envoy: bool,
) -> Dict[str, List[Optional[float]]]:
    out: Dict[str, List[Optional[float]]] = {p: [] for p in PRODUCTS}
    for _label, twp, nginx, haproxy, envoy, yarp in arms:
        out["Titanium"].append(arm_sustain_c64(csv_path, twp))
        out["YARP"].append(arm_sustain_c64(csv_path, yarp))
        for product, arm in (("nginx", nginx), ("HAProxy", haproxy), ("Envoy", envoy)):
            if arm is None or (not include_haproxy_envoy and product in ("HAProxy", "Envoy")):
                out[product].append(None)
            else:
                out[product].append(arm_sustain_c64(csv_path, arm))
    return out


def render_chart(
    series: Dict[str, List[Optional[float]]],
    labels: Sequence[str],
    chart_title: str,
    out_path: Path,
    footer: str,
) -> None:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np

    x = np.arange(len(labels), dtype=float)
    width = 0.14
    offsets = tuple((i - 2) * width for i in range(5))

    fig, ax = plt.subplots(figsize=(14.5, 5.4), dpi=140)
    ymax = 1.0
    for product, offset in zip(PRODUCTS, offsets):
        vals = series[product]
        heights = [0.0 if v is None else float(v) for v in vals]
        present = [v is not None for v in vals]
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
    ax.set_title(chart_title)
    ax.set_xticks(x)
    ax.set_xticklabels(labels, rotation=18, ha="right")
    ax.set_ylim(0, ymax * 1.12)
    ax.yaxis.set_major_formatter(plt.FuncFormatter(lambda v, _: f"{int(v):,}"))
    ax.grid(axis="y", linestyle=":", alpha=0.45, zorder=0)
    ax.legend(loc="upper right", framealpha=0.92, ncols=5, fontsize=8)
    ax.set_axisbelow(True)
    fig.text(0.01, 0.01, footer, fontsize=8, color="#444444")
    fig.tight_layout(rect=(0, 0.04, 1, 1))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_path, bbox_inches="tight")
    plt.close(fig)


CHART_SPECS = (
    ("bodies", "bodies", BODY_ARMS, "Heavier GET bodies"),
    ("bodies", "bodies-remainder", BODY_REMAINDER_ARMS, "Heavier GET remainder wires"),
    ("post", "post", POST_ARMS, "POST 64 KiB"),
    ("lossy", "lossy", LOSSY_ARMS, "Lossy / high-RTT"),
    ("tls", "tls-cost", TLS_ARMS, "TLS termination cost"),
    ("arch", "arch", ARCH_ARMS, "Architecture-sensitive"),
)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--bodies-root", type=Path, help="gha-dl/<runId> for compare-bodies")
    ap.add_argument("--post-root", type=Path, help="gha-dl/<runId> for compare-post")
    ap.add_argument("--lossy-root", type=Path, help="gha-dl/<runId> for compare-lossy")
    ap.add_argument("--tls-root", type=Path, help="gha-dl/<runId> for compare-tls-cost")
    ap.add_argument("--arch-root", type=Path, help="gha-dl/<runId> for compare-arch")
    ap.add_argument("--out-dir", type=Path, default=Path("wiki/images"))
    ap.add_argument("--title-suffix", default="", help="e.g. '@ 024bd68d'")
    ap.add_argument(
        "--os",
        action="append",
        choices=("linux", "windows"),
        help="Subset of OS charts (default: all with CSVs)",
    )
    args = ap.parse_args()

    roots = {
        "bodies": args.bodies_root,
        "post": args.post_root,
        "lossy": args.lossy_root,
        "tls": args.tls_root,
        "arch": args.arch_root,
    }
    if not any(roots.values()):
        ap.error("Provide at least one of --bodies-root/--post-root/--lossy-root/--tls-root/--arch-root")

    wanted_os = set(args.os) if args.os else {"linux", "windows"}
    suffix = args.title_suffix.strip()
    written: List[Path] = []

    for mode_key, file_stem, arms, title_base in CHART_SPECS:
        root = roots[mode_key]
        if root is None:
            continue
        labels = [a[0] for a in arms]
        for os_key, os_title, folder_keys, include_he in OS_SPECS:
            if os_key not in wanted_os:
                continue
            csv_path = find_csv(root, folder_keys)
            if csv_path is None:
                print(f"{title_base} {os_title}: no CSV under {root}", file=__import__("sys").stderr)
                continue
            series = collect_series(csv_path, arms, include_he)
            out = args.out_dir / f"rps-heavier-{file_stem}-{os_key}.png"
            chart_title = f"{title_base} — {os_title}"
            if suffix:
                chart_title = f"{chart_title} {suffix}"
            footer = (
                "Heavier reverse workloads · GHA 4-core · missing bars = Not possible · "
                "SLO-miss sustain plotted as 0"
            )
            if not include_he:
                footer += " · Windows: no HAProxy/Envoy official port"
            render_chart(series, labels, chart_title, out, footer)
            written.append(out)
            print(f"{chart_title} ← {csv_path}")
            print(f"  wrote {out}")

    if not written:
        print("No charts written.", file=__import__("sys").stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
