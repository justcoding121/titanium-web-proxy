# Paste helpers for Phase 4 real-world gRPC/WS wiki tables (wiki only — no chart PNGs).
#
# Usage (after downloading GHA artifacts into tools/RpsLoadProbe/results/gha-dl/<runId>/):
#   py -3 tools/RpsLoadProbe/paste-grpc-ws-wiki.py --grpc-root <id> --ws-h1tls-root <id> --ws-h2-root <id>
#
# Prints markdown table bodies for:
#   Unary gRPC (H2 TLS → h2c)     arms *-grpc-h2c
#   WebSocket (H1 TLS → H1 TLS)   arms *-duplex-ws-h1tls
#   WebSocket (H2 TLS RFC 8441)   arms *-duplex-ws-h2
#
# Does not edit wiki/Performance.md — copy cells into the placeholder tables.

from __future__ import annotations

import argparse
import csv
import statistics
from pathlib import Path
from typing import Optional

OS_FOLDERS = {
    "windows": ("windows", "win"),
    "linux": ("linux", "ubuntu"),
    "macos": ("macos", "osx", "darwin"),
}

GRPC_H2C = {
    "Titanium": "twp-grpc-h2c",
    "YARP": "yarp-grpc-h2c",
    "nginx": None,  # Not possible
    "HAProxy": "haproxy-grpc-h2c",
    "Envoy": "envoy-grpc-h2c",
}

WS_H1TLS = {
    "Titanium": "twp-reverse-http1-tls-duplex-ws-h1tls",
    "YARP": "yarp-reverse-http1-tls-duplex-ws-h1tls",
    "nginx": "nginx-reverse-http1-tls-duplex-ws-h1tls",
    "HAProxy": "haproxy-reverse-http1-tls-duplex-ws-h1tls",
    "Envoy": "envoy-reverse-http1-tls-duplex-ws-h1tls",
}

WS_H2 = {
    "Titanium": "twp-reverse-http2-duplex-ws-h2",
    "YARP": "yarp-reverse-http2-duplex-ws-h2",
    "nginx": None,  # Not possible
    "HAProxy": "haproxy-reverse-http2-duplex-ws-h2",
    "Envoy": "envoy-reverse-http2-duplex-ws-h2",
}


def find_csvs(root: Path) -> dict[str, list[Path]]:
    by_os: dict[str, list[Path]] = {k: [] for k in OS_FOLDERS}
    if not root.exists():
        return by_os
    for path in root.rglob("*.csv"):
        low = str(path).lower().replace("\\", "/")
        for os_name, keys in OS_FOLDERS.items():
            if any(k in low for k in keys):
                by_os[os_name].append(path)
                break
    return by_os


def load_arm_medians(csvs: list[Path]) -> dict[str, dict[str, float]]:
    """arm -> {PeakRps, SustainRps, RssMiB, CpuPct} medians across repeats/files."""
    buckets: dict[str, dict[str, list[float]]] = {}
    for path in csvs:
        with path.open(newline="", encoding="utf-8") as f:
            reader = csv.DictReader(f)
            for row in reader:
                arm = (row.get("arm") or row.get("Arm") or "").strip()
                if not arm:
                    continue
                b = buckets.setdefault(arm, {"Peak": [], "Sustain": [], "Rss": [], "Cpu": []})
                for key, dest in (
                    ("peak_rps", "Peak"),
                    ("PeakRps", "Peak"),
                    ("sustain_rps", "Sustain"),
                    ("SustainRps", "Sustain"),
                    ("proxy_rss_peak_bytes", "Rss"),
                    ("ProxyRssPeakBytes", "Rss"),
                    ("proxy_cpu_avg_pct", "Cpu"),
                    ("ProxyCpuAvgPct", "Cpu"),
                ):
                    if key in row and row[key] not in (None, ""):
                        try:
                            val = float(row[key])
                        except ValueError:
                            continue
                        if dest == "Rss":
                            val = val / (1024 * 1024)
                        b[dest].append(val)
    out: dict[str, dict[str, float]] = {}
    for arm, series in buckets.items():
        out[arm] = {
            k: statistics.median(v) if v else float("nan")
            for k, v in series.items()
        }
    return out


def cell(stats: Optional[dict[str, float]], impossible: Optional[str] = None) -> str:
    if impossible:
        return f"*{impossible}*"
    if stats is None or not stats or all(v != v for v in stats.values()):  # noqa: PLR0124
        return "*Not measured*"
    sustain = stats.get("Sustain", float("nan"))
    peak = stats.get("Peak", float("nan"))
    rss = stats.get("Rss", float("nan"))
    cpu = stats.get("Cpu", float("nan"))
    if sustain != sustain:  # NaN
        return "*Not measured*"
    rps = int(round(sustain)) if sustain == sustain else 0
    rss_s = f"{rss:.0f} MiB" if rss == rss else "?"
    cpu_s = f"{cpu:.1f}% CPU" if cpu == cpu else "?"
    return f"**{rps:,}**<br><sub>({rss_s} / {cpu_s})</sub>"


def render_table(title: str, arms: dict[str, Optional[str]], by_os: dict[str, dict[str, dict[str, float]]],
                 win_no_haproxy_envoy: bool = True, nginx_impossible: Optional[str] = None) -> str:
    lines = [
        f"### {title}",
        "",
        "| OS | Titanium | YARP | nginx | HAProxy | Envoy |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for os_label, os_key in (("Windows", "windows"), ("Linux", "linux"), ("macOS", "macos")):
        data = by_os.get(os_key, {})
        cells = []
        for product in ("Titanium", "YARP", "nginx", "HAProxy", "Envoy"):
            arm = arms.get(product)
            if arm is None:
                reason = nginx_impossible or "Not possible"
                cells.append(cell(None, reason))
            elif win_no_haproxy_envoy and os_key == "windows" and product in ("HAProxy", "Envoy"):
                cells.append(cell(None, "Not possible"))
            else:
                cells.append(cell(data.get(arm)))
        lines.append("| " + " | ".join([os_label, *cells]) + " |")
    return "\n".join(lines)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--grpc-root", action="append", default=[], help="gha-dl run id or path for compare-grpc")
    ap.add_argument("--ws-h1tls-root", action="append", default=[], help="compare-ws-h1tls roots")
    ap.add_argument("--ws-h2-root", action="append", default=[], help="compare-ws-h2 roots")
    ap.add_argument("--gha-dl", type=Path, default=Path("tools/RpsLoadProbe/results/gha-dl"))
    args = ap.parse_args()

    def roots(ids: list[str]) -> list[Path]:
        out = []
        for item in ids:
            p = Path(item)
            if not p.exists():
                p = args.gha_dl / item
            out.append(p)
        return out

    def load(ids: list[str]) -> dict[str, dict[str, dict[str, float]]]:
        merged: dict[str, dict[str, dict[str, float]]] = {k: {} for k in OS_FOLDERS}
        for root in roots(ids):
            for os_name, csvs in find_csvs(root).items():
                med = load_arm_medians(csvs)
                merged[os_name].update(med)
        return merged

    blocks = []
    if args.grpc_root:
        blocks.append(render_table("Unary gRPC (H2 TLS → h2c)", GRPC_H2C, load(args.grpc_root),
                                   nginx_impossible="Not possible (no H2 upstream)"))
    if args.ws_h1tls_root:
        blocks.append(render_table("WebSocket (H1 TLS → H1 TLS)", WS_H1TLS, load(args.ws_h1tls_root)))
    if args.ws_h2_root:
        blocks.append(render_table("WebSocket (H2 TLS RFC 8441 → H1 plain)", WS_H2, load(args.ws_h2_root),
                                   nginx_impossible="Not possible (no RFC 8441 extended CONNECT)"))
    if not blocks:
        ap.error("Provide at least one of --grpc-root / --ws-h1tls-root / --ws-h2-root")
    print("\n\n".join(blocks))


if __name__ == "__main__":
    main()
