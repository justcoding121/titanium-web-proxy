# Paste helpers for Phase 4 real-world gRPC/WS wiki tables (wiki only — no chart PNGs).
#
# Usage (after downloading GHA artifacts into tools/RpsLoadProbe/results/gha-dl/<runId>/):
#   py -3 tools/RpsLoadProbe/paste-grpc-ws-wiki.py --grpc-root <id> --ws-h1tls-root <id> --ws-h2-root <id>
#
# Prints markdown table bodies for:
#   Unary gRPC (H2 TLS)           arms *-grpc-http2
#   Unary gRPC (H2 TLS → h2c)     arms *-grpc-h2c
#   WebSocket (H1 TLS → H1 TLS)   arms *-duplex-ws-h1tls
#   WebSocket (H2 TLS RFC 8441)   arms *-duplex-ws-h2
#
# With --apply, replaces matching ## sections' tables in wiki/Performance.md.

from __future__ import annotations

import argparse
import csv
import statistics
from collections import defaultdict
from pathlib import Path
from typing import Optional

OS_FOLDERS = {
    "windows": ("windows", "win"),
    "linux": ("linux", "ubuntu"),
    # Apple Silicon only — never match retired `macos-15-intel` paths.
    "macos": ("macos-15",),
}

GRPC_H2 = {
    "Titanium": "twp-grpc-http2",
    "YARP": "yarp-grpc-http2",
    "nginx": "nginx-grpc-http2",
    "HAProxy": "haproxy-grpc-http2",
    "Envoy": "envoy-grpc-http2",
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
        if "macos-15-intel" in low:
            continue
        for os_name, keys in OS_FOLDERS.items():
            if os_name == "macos":
                # Exact Apple Silicon label only (avoid matching macos-15-intel).
                if "macos-15" in low and "macos-15-intel" not in low:
                    by_os[os_name].append(path)
                    break
                continue
            if any(k in low for k in keys):
                by_os[os_name].append(path)
                break
    return by_os


STEPS = 4  # concurrency ladder 8,16,32,64 per repeat


def load_arm_medians(csvs: list[Path]) -> dict[str, dict[str, float]]:
    """arm -> {Peak, Sustain, Rss, Cpu} using ramp CSV rows (c=64 SLO for sustain)."""
    by_arm_rows: dict[str, list[dict[str, str]]] = defaultdict(list)
    for path in csvs:
        with path.open(newline="", encoding="utf-8") as f:
            for row in csv.DictReader(f):
                arm = (row.get("arm") or "").strip()
                if arm:
                    by_arm_rows[arm].append(row)

    out: dict[str, dict[str, float]] = {}
    for arm, rows in by_arm_rows.items():
        sustains: list[float] = []
        peaks: list[float] = []
        rss: list[float] = []
        cpu: list[float] = []
        # Prefer explicit repeat column when present; else chunk by concurrency ladder.
        if any((r.get("repeat") or "").strip() for r in rows):
            by_rep: dict[str, list[dict[str, str]]] = defaultdict(list)
            for r in rows:
                by_rep[r.get("repeat", "0")].append(r)
            chunks = list(by_rep.values())
        else:
            chunks = [rows[i : i + STEPS] for i in range(0, len(rows), STEPS) if i + STEPS <= len(rows)]
            if not chunks and rows:
                chunks = [rows]
        for chunk in chunks:
            best = max(chunk, key=lambda r: float(r["rps"]))
            peaks.append(float(best["rps"]))
            rss.append(float(best.get("proxy_rss_peak_bytes") or 0) / (1024 * 1024))
            cpu.append(float(best.get("proxy_cpu_avg_pct") or 0))
            ok = [r for r in chunk if r.get("concurrency") == "64" and r.get("meets_slo") == "1"]
            sustains.append(float(ok[-1]["rps"]) if ok else 0.0)
        out[arm] = {
            "Sustain": statistics.median(sustains) if sustains else float("nan"),
            "Peak": statistics.median(peaks) if peaks else float("nan"),
            "Rss": statistics.median(rss) if rss else float("nan"),
            "Cpu": statistics.median(cpu) if cpu else float("nan"),
        }
    return out


GOLD = "\U0001F947"
SILVER = "\U0001F948"
BRONZE = "\U0001F949"
MEDALS = (GOLD, SILVER, BRONZE)


def cell(
    stats: Optional[dict[str, float]],
    impossible: Optional[str] = None,
    *,
    medal: Optional[str] = None,
) -> str:
    if impossible:
        return f"*{impossible}*"
    if stats is None or not stats or all(v != v for v in stats.values()):  # noqa: PLR0124
        return "*Not measured*"
    sustain = stats.get("Sustain", float("nan"))
    rss = stats.get("Rss", float("nan"))
    cpu = stats.get("Cpu", float("nan"))
    if sustain != sustain:  # NaN
        return "*Not measured*"
    rps = int(round(sustain)) if sustain == sustain else 0
    rss_s = f"{rss:.0f} MiB" if rss == rss else "?"
    cpu_s = f"{cpu:.1f}% CPU" if cpu == cpu else "?"
    prefix = f"{medal} " if medal else ""
    return f"{prefix}**{rps:,}**<br><sub>({rss_s} / {cpu_s})</sub>"


def pick_row_medals(
    products: list[str],
    arms: dict[str, Optional[str]],
    data: dict[str, dict[str, float]],
    *,
    os_key: str,
    win_no_haproxy_envoy: bool,
) -> dict[str, str]:
    """Top-3 sustain > 0 among OS-possible peers; ties break on lower RSS then CPU."""
    ranked: list[tuple[tuple[float, float, float], str]] = []
    for product in products:
        arm = arms.get(product)
        if arm is None:
            continue
        if win_no_haproxy_envoy and os_key == "windows" and product in ("HAProxy", "Envoy"):
            continue
        stats = data.get(arm)
        if not stats:
            continue
        sustain = stats.get("Sustain", float("nan"))
        if sustain != sustain or sustain <= 0:
            continue
        rss = stats.get("Rss", float("inf"))
        cpu = stats.get("Cpu", float("inf"))
        if rss != rss:
            rss = float("inf")
        if cpu != cpu:
            cpu = float("inf")
        ranked.append(((-sustain, rss, cpu), product))
    ranked.sort(key=lambda item: item[0])
    return {name: MEDALS[i] for i, (_, name) in enumerate(ranked[:3])}


def render_table(
    title: str,
    arms: dict[str, Optional[str]],
    by_os: dict[str, dict[str, dict[str, float]]],
    win_no_haproxy_envoy: bool = True,
    nginx_impossible: Optional[str] = None,
    *,
    include_heading: bool = True,
) -> str:
    # Drop nginx when it is impossible on every OS (h2c / RFC 8441).
    products = ["Titanium", "YARP", "nginx", "HAProxy", "Envoy"]
    note_lines: list[str] = []
    if nginx_impossible:
        products = [p for p in products if p != "nginx"]
        note_lines.append(
            f"*Not possible:* **nginx** column omitted "
            f"({nginx_impossible.removeprefix('Not possible').strip(' ()') or 'not supported on this path'})."
        )
    header = "| OS | " + " | ".join(products) + " |"
    rule = "|---|" + "|".join(["---:"] * len(products)) + "|"
    lines: list[str] = []
    if include_heading:
        lines.extend([f"### {title}", ""])
    lines.extend(note_lines)
    if note_lines:
        lines.append("")
    lines.extend([header, rule])
    for os_label, os_key in (("Windows", "windows"), ("Linux", "linux"), ("macOS", "macos")):
        data = by_os.get(os_key, {})
        medals = pick_row_medals(
            products, arms, data, os_key=os_key, win_no_haproxy_envoy=win_no_haproxy_envoy
        )
        cells = []
        for product in products:
            arm = arms.get(product)
            if arm is None:
                cells.append(cell(None, nginx_impossible or "Not possible"))
            elif win_no_haproxy_envoy and os_key == "windows" and product in ("HAProxy", "Envoy"):
                cells.append(cell(None, "Not possible"))
            else:
                cells.append(cell(data.get(arm), medal=medals.get(product)))
        lines.append("| " + " | ".join([os_label, *cells]) + " |")
    return "\n".join(lines)


def apply_to_wiki(
    wiki_path: Path,
    sections: list[tuple[str, str]],
    head_sha: str,
    primary_run_id: str,
) -> None:
    import re

    text = wiki_path.read_text(encoding="utf-8")
    for heading, table_md in sections:
        pat = re.compile(
            rf"(^## {re.escape(heading)}\n)(.*?)(?=^## |\Z)",
            re.MULTILINE | re.DOTALL,
        )
        m = pat.search(text)
        if not m:
            raise SystemExit(f"Section not found in wiki: ## {heading}")
        head, body = m.group(1), m.group(2)
        body = re.sub(
            r"Median of \*\*3\*\* repeats @ `[^`]+` — \[[0-9]+\]\(https://github\.com/justcoding121/titanium-web-proxy/actions/runs/[0-9]+\)",
            f"Median of **3** repeats @ `{head_sha}` — [{primary_run_id}](https://github.com/justcoding121/titanium-web-proxy/actions/runs/{primary_run_id})",
            body,
            count=1,
        )
        prose_end = re.search(r"(?:\*Not possible:.*\n\n)?\| OS \|", body)
        if not prose_end:
            raise SystemExit(f"No OS table under ## {heading}")
        prose = body[: prose_end.start()]
        new_body = prose.rstrip() + "\n\n" + table_md.strip() + "\n\n"
        text = text[: m.start()] + head + new_body + text[m.end() :]
    wiki_path.write_text(text, encoding="utf-8", newline="\n")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--grpc-root", action="append", default=[], help="gha-dl run id or path for compare-grpc")
    ap.add_argument("--ws-h1tls-root", action="append", default=[], help="compare-ws-h1tls roots")
    ap.add_argument("--ws-h2-root", action="append", default=[], help="compare-ws-h2 roots")
    ap.add_argument("--gha-dl", type=Path, default=Path("tools/RpsLoadProbe/results/gha-dl"))
    ap.add_argument("--apply", action="store_true", help="Write tables into wiki/Performance.md")
    ap.add_argument("--wiki", type=Path, default=Path("wiki/Performance.md"))
    ap.add_argument("--head-sha", default="e781b009")
    ap.add_argument("--primary-run-id", default="", help="Actions run id for intro Source links")
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

    sections: list[tuple[str, str]] = []
    print_blocks: list[str] = []
    if args.grpc_root:
        grpc_data = load(args.grpc_root)
        h2 = render_table("Unary gRPC (H2 TLS)", GRPC_H2, grpc_data, include_heading=False)
        h2c = render_table(
            "Unary gRPC (H2 TLS → h2c)",
            GRPC_H2C,
            grpc_data,
            nginx_impossible="Not possible (no H2 upstream)",
            include_heading=False,
        )
        sections.append(("Unary gRPC (H2 TLS)", h2))
        sections.append(("Unary gRPC (H2 TLS → h2c)", h2c))
        print_blocks.extend([h2, h2c])
    if args.ws_h1tls_root:
        t = render_table(
            "WebSocket (H1 TLS → H1 TLS)",
            WS_H1TLS,
            load(args.ws_h1tls_root),
            include_heading=False,
        )
        sections.append(("WebSocket (H1 TLS → H1 TLS)", t))
        print_blocks.append(t)
    if args.ws_h2_root:
        t = render_table(
            "WebSocket (H2 TLS 8441 → H1)",
            WS_H2,
            load(args.ws_h2_root),
            nginx_impossible="Not possible (no RFC 8441 extended CONNECT)",
            include_heading=False,
        )
        # Wiki heading uses "8441 → H1" (not "RFC 8441 → H1 plain").
        sections.append(("WebSocket (H2 TLS 8441 → H1)", t))
        print_blocks.append(t)
    if not sections:
        ap.error("Provide at least one of --grpc-root / --ws-h1tls-root / --ws-h2-root")
    print("\n\n".join(print_blocks))
    if args.apply:
        primary = args.primary_run_id or (args.grpc_root[0] if args.grpc_root else args.ws_h1tls_root[0])
        apply_to_wiki(args.wiki, sections, args.head_sha[:8], str(primary))
        print(f"Applied {len(sections)} section(s) to {args.wiki}", flush=True)


if __name__ == "__main__":
    main()
