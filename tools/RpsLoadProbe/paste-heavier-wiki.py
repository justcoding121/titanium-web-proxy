#!/usr/bin/env python3
"""Paste heavier + saturation tables into wiki/Performance.md from gha-dl CSVs."""
from __future__ import annotations

import argparse
import csv
import re
from pathlib import Path
from typing import Dict, List, Optional, Tuple, Union

RunIds = Union[int, List[int]]

ROOT = Path("tools/RpsLoadProbe/results/gha-dl")
WIKI = Path("wiki/Performance.md")
HEAD = "e781b009"
RUNS = {
    # Wiki-grade batch @ e781b009 (2026-09-26); prior @ 8bfa7852 (2026-09-16).
    "saturation": [34441539402, 34441541578],
    "bodies": [34557778171, 34557780393, 34441570199, 34441572485, 34441574457, 34441576323],
    "post": [34557782264, 34441591377, 34441593359],
    "lossy": [34557784262, 34441595456, 34441597540],
    "tls": [34441599658, 34441602032],
    "arch": [34557786074, 34557788179, 34557790004, 34441578556, 34441580725, 34441583238, 34441585221, 34441587413, 34441589415],
}
MEDAL = "\U0001F947"
STEPS = 4


def median(vals: List[float]) -> Optional[float]:
    if not vals:
        return None
    s = sorted(vals)
    return s[(len(s) - 1) // 2]


def arm_metrics(csv_path: Path, arm: str) -> Optional[dict]:
    """Median sustain/peak/RSS/CPU for one arm.

    Prefer c=64 SLO-pass (wiki default). If c=64 never meets SLO, fall back to the
    highest concurrency that does — same rule as validate-lossy-arch-gates — so
    published TWP cells are not forced to 0 when a lower step still sustains.
    """
    rows = [r for r in csv.DictReader(csv_path.open(newline="")) if r.get("arm") == arm]
    if not rows:
        return None
    sustains: List[float] = []
    peaks: List[float] = []
    rss: List[float] = []
    cpu: List[float] = []
    i = 0
    while i + STEPS <= len(rows):
        chunk = rows[i : i + STEPS]
        c64_ok = [r for r in chunk if r.get("concurrency") == "64" and r.get("meets_slo") == "1"]
        if c64_ok:
            r = c64_ok[-1]
            sustains.append(float(r["rps"]))
            peaks.append(float(r["rps"]))
            rss.append(float(r.get("proxy_rss_peak_bytes") or 0))
            cpu.append(float(r.get("proxy_cpu_avg_pct") or 0))
        else:
            slo_any = [r for r in chunk if r.get("meets_slo") == "1"]
            c64_any = [r for r in chunk if r.get("concurrency") == "64"]
            peak_row = c64_any[-1] if c64_any else (chunk[-1] if chunk else None)
            if slo_any:
                best_c = max(int(r["concurrency"]) for r in slo_any)
                r = [x for x in slo_any if int(x["concurrency"]) == best_c][-1]
                sustains.append(float(r["rps"]))
                peaks.append(float(peak_row["rps"]) if peak_row else float(r["rps"]))
                rss.append(float(r.get("proxy_rss_peak_bytes") or 0))
                cpu.append(float(r.get("proxy_cpu_avg_pct") or 0))
            elif peak_row is not None:
                sustains.append(0.0)
                peaks.append(float(peak_row["rps"]))
                rss.append(float(peak_row.get("proxy_rss_peak_bytes") or 0))
                cpu.append(float(peak_row.get("proxy_cpu_avg_pct") or 0))
        i += STEPS
    if not peaks:
        slo = [r for r in rows if r.get("meets_slo") == "1"]
        if slo:
            best_c = max(int(r["concurrency"]) for r in slo)
            r = [x for x in slo if int(x["concurrency"]) == best_c][-1]
            return {
                "Sustain": float(r["rps"]),
                "Peak": float(r["rps"]),
                "Rss": float(r.get("proxy_rss_peak_bytes") or 0),
                "Cpu": float(r.get("proxy_cpu_avg_pct") or 0),
            }
        c64 = [r for r in rows if r.get("concurrency") == "64"]
        if not c64:
            return None
        r = c64[-1]
        return {
            "Sustain": 0.0,
            "Peak": float(r["rps"]),
            "Rss": float(r.get("proxy_rss_peak_bytes") or 0),
            "Cpu": float(r.get("proxy_cpu_avg_pct") or 0),
        }
    return {
        "Sustain": median(sustains),
        "Peak": median(peaks),
        "Rss": median(rss),
        "Cpu": median(cpu),
    }


def _run_id_list(run_ids: RunIds) -> List[int]:
    return [run_ids] if isinstance(run_ids, int) else list(run_ids)


def _csv_files_for_os(run_dir: Path, os_folder: str) -> List[Path]:
    """Exact rps-csv-<os> or shard-suffixed rps-csv-<os>-shard-* folders."""
    exact = run_dir / f"rps-csv-{os_folder}"
    files = sorted(exact.glob("*.csv")) if exact.is_dir() else []
    if files:
        return files
    for d in sorted(run_dir.glob(f"rps-csv-{os_folder}-*")):
        # `rps-csv-macos-15-*` must not pick up retired `rps-csv-macos-15-intel-*`.
        if os_folder == "macos-15" and "macos-15-intel" in d.name:
            continue
        files.extend(sorted(d.glob("*.csv")))
    return files


def load_os(run_ids: RunIds, os_folder: str) -> Dict[str, dict]:
    """Union arm metrics across shard run ids; first non-empty wins per arm."""
    merged: Dict[str, dict] = {}
    for rid in _run_id_list(run_ids):
        for path in _csv_files_for_os(ROOT / str(rid), os_folder):
            arms = {r["arm"] for r in csv.DictReader(path.open(newline=""))}
            for a in arms:
                if a in merged:
                    continue
                m = arm_metrics(path, a)
                if m is not None:
                    merged[a] = m
    return merged


def primary_run_id(run_ids: RunIds) -> int:
    return _run_id_list(run_ids)[0]


def run_id_with_os(run_ids: RunIds, os_folder: str) -> int:
    """First run id in the list that has CSVs for this OS (matches load_os first-wins)."""
    ids = _run_id_list(run_ids)
    for rid in ids:
        if _csv_files_for_os(ROOT / str(rid), os_folder):
            return rid
    return ids[0]


def has_os(run_ids: RunIds, os_folder: str) -> bool:
    """True when a downloaded rps-csv folder exists for this OS. Absent folders must not wipe wiki rows."""
    return any(_csv_files_for_os(ROOT / str(rid), os_folder) for rid in _run_id_list(run_ids))


def parse_run_ids(text: str) -> RunIds:
    parts = [p.strip() for p in text.split(",") if p.strip()]
    ids = [int(p) for p in parts]
    return ids[0] if len(ids) == 1 else ids


def fmt_cell(m: Optional[dict], medal: bool = False, impossible: Optional[str] = None) -> str:
    if impossible:
        return f"*{impossible}*"
    if not m:
        return "*Not measured*"
    sustain = round(m["Sustain"])
    peak = round(m["Peak"])
    mb = round(m["Rss"] / (1024 * 1024))
    cpu = round(m["Cpu"], 1)
    prefix = f"{MEDAL} " if medal else ""
    # Handshake arms defer CPU sampling (post-measure snapshot) — omit idle 0% CPU.
    foot = f"{mb} MiB" if cpu < 0.05 else f"{mb} MiB / {cpu}% CPU"
    if peak > sustain:
        sub = f"peak {peak:,} · {foot}"
    else:
        sub = foot
    return f"{prefix}**{sustain:,}**<br><sub>({sub})</sub>"


def pick_medal(cands: List[Tuple[str, Optional[dict]]]) -> Optional[str]:
    valid = [(k, m) for k, m in cands if m and (m["Sustain"] or 0) > 0]
    if not valid:
        return None
    return min(valid, key=lambda km: (-km[1]["Sustain"], km[1]["Rss"], km[1]["Cpu"]))[0]


WIN_NO_HAPROXY_ENVOY_NOTE = (
    "*Not possible:* **HAProxy** and **Envoy** columns are omitted (no official Windows port).\n"
)


def peer_cols(include_haproxy_envoy: bool) -> str:
    mid = "HAProxy | Envoy | " if include_haproxy_envoy else ""
    return f"TWP | nginx | {mid}YARP"


def peer_rule(include_haproxy_envoy: bool) -> str:
    n = 5 if include_haproxy_envoy else 3
    return "|".join(["---:"] * n)


def _peer_impossible(arm: Optional[str], nginx_a: Optional[str], win_no_quic: bool) -> Optional[str]:
    if arm is not None:
        if win_no_quic and nginx_a and "http3" in nginx_a:
            return "Not possible (no QUIC)"
        return None
    return "Not possible"


def peer_row(
    prefix: List[str],
    twp_a: Optional[str],
    nginx_a: Optional[str],
    yarp_a: Optional[str],
    data: dict,
    win_no_quic: bool = False,
    win_no_haproxy_envoy: bool = False,
    haproxy_a: Optional[str] = None,
    envoy_a: Optional[str] = None,
) -> str:
    twp = data.get(twp_a) if twp_a else None
    nginx = data.get(nginx_a) if nginx_a else None
    if haproxy_a is None:
        haproxy_a = nginx_a.replace("nginx-", "haproxy-", 1) if nginx_a else None
    if envoy_a is None:
        envoy_a = nginx_a.replace("nginx-", "envoy-", 1) if nginx_a else None
    haproxy = data.get(haproxy_a) if haproxy_a else None
    envoy = data.get(envoy_a) if envoy_a else None
    yarp = data.get(yarp_a) if yarp_a else None
    nginx_imp = _peer_impossible(nginx_a, nginx_a, win_no_quic)
    haproxy_imp = _peer_impossible(haproxy_a, nginx_a, win_no_quic)
    envoy_imp = _peer_impossible(envoy_a, nginx_a, win_no_quic)
    if win_no_quic and nginx_a and "http3" in nginx_a:
        nginx = None
        nginx_imp = "Not possible (no QUIC)"
    if win_no_haproxy_envoy:
        # Omit HAProxy/Envoy columns entirely on Windows (no official ports).
        haproxy = None
        envoy = None
        medal_peers = [("twp", twp), ("nginx", nginx), ("yarp", yarp)]
    else:
        medal_peers = [("twp", twp), ("nginx", nginx), ("haproxy", haproxy), ("envoy", envoy), ("yarp", yarp)]
    medal = pick_medal(medal_peers)
    cells = prefix + [
        fmt_cell(twp, medal=(medal == "twp")),
        fmt_cell(nginx, medal=(medal == "nginx"), impossible=nginx_imp),
    ]
    if not win_no_haproxy_envoy:
        cells += [
            fmt_cell(haproxy, medal=(medal == "haproxy"), impossible=haproxy_imp),
            fmt_cell(envoy, medal=(medal == "envoy"), impossible=envoy_imp),
        ]
    cells += [
        fmt_cell(yarp, medal=(medal == "yarp")),
    ]
    return "| " + " | ".join(cells) + " |"


def run_url(rid: int) -> str:
    return f"https://github.com/justcoding121/titanium-web-proxy/actions/runs/{rid}"


def replace_table_at(text: str, start: int, new_table: str) -> str:
    j = text.find("|", start)
    if j < 0:
        raise SystemExit(f"no table at {start}")
    lines = text[j:].splitlines(keepends=True)
    n = 0
    for line in lines:
        if line.startswith("|"):
            n += 1
        else:
            break
    end = j + sum(len(lines[i]) for i in range(n))
    return text[:j] + new_table + "\n" + text[end:]


def main() -> None:
    global HEAD
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--head-sha", default=HEAD, help="Short commit SHA stamped into wiki intros")
    for mode in RUNS:
        ap.add_argument(f"--{mode}", help="Run id or comma-separated shard ids")
    args = ap.parse_args()
    HEAD = (args.head_sha or HEAD)[:8]

    runs: Dict[str, RunIds] = dict(RUNS)
    for mode in RUNS:
        override = getattr(args, mode, None)
        if override:
            runs[mode] = parse_run_ids(override)

    win = {k: load_os(rid, "windows-latest") for k, rid in runs.items()}
    lin = {k: load_os(rid, "ubuntu-latest") for k, rid in runs.items()}
    # macOS wiki/charts use Apple Silicon `macos-15` only — never `macos-15-intel`.
    mac_folder = "macos-15"
    mac = {k: load_os(rid, mac_folder) for k, rid in runs.items()}
    text = WIKI.read_text(encoding="utf-8")
    rid_b = primary_run_id(runs["bodies"])
    rid_p = primary_run_id(runs["post"])
    rid_l = primary_run_id(runs["lossy"])
    rid_a = primary_run_id(runs["arch"])
    rid_t = run_id_with_os(runs["tls"], "windows-latest")
    rid_t_lin = run_id_with_os(runs["tls"], "ubuntu-latest")
    rid_t_mac = run_id_with_os(runs["tls"], mac_folder)
    rid_b_mac = run_id_with_os(runs["bodies"], mac_folder)
    rid_p_mac = run_id_with_os(runs["post"], mac_folder)
    rid_l_mac = run_id_with_os(runs["lossy"], mac_folder)
    rid_a_mac = run_id_with_os(runs["arch"], mac_folder)
    rid_s = primary_run_id(runs["saturation"])
    rid_s_mac = run_id_with_os(runs["saturation"], mac_folder)

    body_spec = [
        ("64 KiB", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-body64k", "nginx-reverse-http1-tls-body64k", "yarp-reverse-http1-tls-body64k"),
        ("64 KiB", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-body64k", "nginx-reverse-http2-body64k", "yarp-reverse-http2-body64k"),
        ("64 KiB", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-body64k", "nginx-reverse-http3-cleartext-body64k", "yarp-reverse-http3-cleartext-body64k"),
        ("256 KiB", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-body256k", "nginx-reverse-http1-tls-body256k", "yarp-reverse-http1-tls-body256k"),
        ("256 KiB", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-body256k", "nginx-reverse-http2-body256k", "yarp-reverse-http2-body256k"),
        ("256 KiB", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-body256k", "nginx-reverse-http3-cleartext-body256k", "yarp-reverse-http3-cleartext-body256k"),
        ("64 KiB", "HTTP/2 · plain", "HTTP/1 · plain", "twp-reverse-h2c-to-h1-body64k", "nginx-reverse-h2c-to-h1-body64k", "yarp-reverse-h2c-to-h1-body64k"),
        ("64 KiB", "HTTP/2 · TLS", "HTTP/2 · plain", "twp-reverse-http2-to-h2c-body64k", None, "yarp-reverse-http2-to-h2c-body64k"),
        ("64 KiB", "HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-to-https-body64k", None, "yarp-reverse-http2-to-https-body64k"),
        ("64 KiB", "HTTP/3 · QUIC", "HTTP/2 · TLS", "twp-reverse-http3-to-http2-body64k", None, "yarp-reverse-http3-to-http2-body64k"),
        ("64 KiB", "HTTP/3 · QUIC", "HTTP/1 · TLS", "twp-reverse-http3-to-https-http1-body64k", "nginx-reverse-http3-to-https-http1-body64k", "yarp-reverse-http3-to-https-http1-body64k"),
        ("256 KiB", "HTTP/2 · plain", "HTTP/1 · plain", "twp-reverse-h2c-to-h1-body256k", "nginx-reverse-h2c-to-h1-body256k", "yarp-reverse-h2c-to-h1-body256k"),
        ("256 KiB", "HTTP/2 · TLS", "HTTP/2 · plain", "twp-reverse-http2-to-h2c-body256k", None, "yarp-reverse-http2-to-h2c-body256k"),
        ("256 KiB", "HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-to-https-body256k", None, "yarp-reverse-http2-to-https-body256k"),
        ("256 KiB", "HTTP/3 · QUIC", "HTTP/2 · TLS", "twp-reverse-http3-to-http2-body256k", None, "yarp-reverse-http3-to-http2-body256k"),
        ("256 KiB", "HTTP/3 · QUIC", "HTTP/1 · TLS", "twp-reverse-http3-to-https-http1-body256k", "nginx-reverse-http3-to-https-http1-body256k", "yarp-reverse-http3-to-https-http1-body256k"),
    ]

    def bodies_table(data: dict, is_win: bool) -> str:
        include_he = not is_win
        rows = [
            f"| Body | Client | Origin | {peer_cols(include_he)} |",
            f"|---|---|---|{peer_rule(include_he)}|",
        ]
        for body, c, o, t, n, y in body_spec:
            extra = {}
            if n is None:
                stem = t.replace("twp-reverse-", "", 1)
                extra = {"haproxy_a": f"haproxy-reverse-{stem}", "envoy_a": f"envoy-reverse-{stem}"}
            rows.append(
                peer_row([body, c, o], t, n, y, data, win_no_quic=is_win, win_no_haproxy_envoy=is_win, **extra)
            )
        return "\n".join(rows)

    post_spec = [
        ("HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-post64k", "nginx-reverse-http1-tls-post64k", "yarp-reverse-http1-tls-post64k"),
        ("HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-post64k", "nginx-reverse-http2-post64k", "yarp-reverse-http2-post64k"),
        ("HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-post64k", "nginx-reverse-http3-cleartext-post64k", "yarp-reverse-http3-cleartext-post64k"),
        ("HTTP/2 · plain", "HTTP/1 · plain", "twp-reverse-h2c-to-h1-post64k", "nginx-reverse-h2c-to-h1-post64k", "yarp-reverse-h2c-to-h1-post64k"),
        ("HTTP/2 · TLS", "HTTP/2 · plain", "twp-reverse-http2-to-h2c-post64k", None, "yarp-reverse-http2-to-h2c-post64k"),
        ("HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-to-https-post64k", None, "yarp-reverse-http2-to-https-post64k"),
        ("HTTP/3 · QUIC", "HTTP/2 · TLS", "twp-reverse-http3-to-http2-post64k", None, "yarp-reverse-http3-to-http2-post64k"),
        ("HTTP/3 · QUIC", "HTTP/1 · TLS", "twp-reverse-http3-to-https-http1-post64k", "nginx-reverse-http3-to-https-http1-post64k", "yarp-reverse-http3-to-https-http1-post64k"),
    ]

    def post_table(data: dict, is_win: bool) -> str:
        include_he = not is_win
        rows = [
            f"| Client | Origin | {peer_cols(include_he)} |",
            f"|---|---|{peer_rule(include_he)}|",
        ]
        for c, o, t, n, y in post_spec:
            extra = {}
            if n is None:
                stem = t.replace("twp-reverse-", "", 1)
                extra = {"haproxy_a": f"haproxy-reverse-{stem}", "envoy_a": f"envoy-reverse-{stem}"}
            rows.append(
                peer_row([c, o], t, n, y, data, win_no_quic=is_win, win_no_haproxy_envoy=is_win, **extra)
            )
        return "\n".join(rows)

    lossy_spec = [
        ("HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-lossy", "nginx-reverse-http1-tls-lossy", "yarp-reverse-http1-tls-lossy"),
        ("HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-lossy", "nginx-reverse-http2-lossy", "yarp-reverse-http2-lossy"),
        ("HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-lossy", "nginx-reverse-http3-cleartext-lossy", "yarp-reverse-http3-cleartext-lossy"),
        ("HTTP/2 · plain", "HTTP/1 · plain", "twp-reverse-h2c-to-h1-lossy", "nginx-reverse-h2c-to-h1-lossy", "yarp-reverse-h2c-to-h1-lossy"),
        ("HTTP/2 · TLS", "HTTP/2 · plain", "twp-reverse-http2-to-h2c-lossy", None, "yarp-reverse-http2-to-h2c-lossy"),
        ("HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-to-https-lossy", None, "yarp-reverse-http2-to-https-lossy"),
        ("HTTP/3 · QUIC", "HTTP/2 · TLS", "twp-reverse-http3-to-http2-lossy", None, "yarp-reverse-http3-to-http2-lossy"),
        ("HTTP/3 · QUIC", "HTTP/1 · TLS", "twp-reverse-http3-to-https-http1-lossy", "nginx-reverse-http3-to-https-http1-lossy", "yarp-reverse-http3-to-https-http1-lossy"),
    ]

    def lossy_table(data: dict, is_win: bool) -> str:
        include_he = not is_win
        rows = [
            f"| Client | Origin | {peer_cols(include_he)} |",
            f"|---|---|{peer_rule(include_he)}|",
        ]
        for c, o, t, n, y in lossy_spec:
            extra = {}
            if n is None:
                stem = t.replace("twp-reverse-", "", 1)
                extra = {"haproxy_a": f"haproxy-reverse-{stem}", "envoy_a": f"envoy-reverse-{stem}"}
            rows.append(
                peer_row([c, o], t, n, y, data, win_no_quic=is_win, win_no_haproxy_envoy=is_win, **extra)
            )
        return "\n".join(rows)

    arch_spec = [
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-slow256k", "nginx-reverse-http1-tls-slow256k", "yarp-reverse-http1-tls-slow256k"),
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-slow256k", "nginx-reverse-http2-slow256k", "yarp-reverse-http2-slow256k"),
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-slow256k", "nginx-reverse-http3-cleartext-slow256k", "yarp-reverse-http3-cleartext-slow256k"),
        ("Early response (origin writes after first request chunk)", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-early64k", "nginx-reverse-http1-tls-early64k", "yarp-reverse-http1-tls-early64k"),
        ("Early response (origin writes after first request chunk)", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-early64k", "nginx-reverse-http2-early64k", "yarp-reverse-http2-early64k"),
        ("Early response (origin writes after first request chunk)", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-early64k", "nginx-reverse-http3-cleartext-early64k", "yarp-reverse-http3-cleartext-early64k"),
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/2 · plain", "HTTP/1 · plain", "twp-reverse-h2c-to-h1-slow256k", "nginx-reverse-h2c-to-h1-slow256k", "yarp-reverse-h2c-to-h1-slow256k"),
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-to-https-slow256k", None, "yarp-reverse-http2-to-https-slow256k"),
        ("Early response (origin writes after first request chunk)", "HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-to-https-early64k", None, "yarp-reverse-http2-to-https-early64k"),
        ("Duplex (both directions live)", "HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-duplex-h2", None, "yarp-reverse-http2-to-https-duplex-h2"),
        ("Duplex (WebSocket / H1 Upgrade)", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-duplex-ws", "nginx-reverse-http1-tls-duplex-ws", "yarp-reverse-http1-tls-duplex-ws"),
    ]

    def arch_table(data: dict, is_win: bool) -> str:
        include_he = not is_win
        rows = [
            f"| Scenario | Client | Origin | {peer_cols(include_he)} |",
            f"|---|---|---|{peer_rule(include_he)}|",
        ]
        for sc, c, o, t, n, y in arch_spec:
            extra = {}
            if n is None:
                stem = t.replace("twp-reverse-", "", 1)
                extra = {
                    "haproxy_a": f"haproxy-reverse-{stem}",
                    "envoy_a": f"envoy-reverse-{stem}",
                }
            rows.append(
                peer_row(
                    [sc, c, o],
                    t,
                    n,
                    y,
                    data,
                    win_no_quic=is_win and bool(n and "http3" in n),
                    win_no_haproxy_envoy=is_win,
                    **extra,
                )
            )
        return "\n".join(rows)

    tls_spec = [
        ("Keep-alive · tiny GET", "twp-reverse-http1-tls-ka-tiny", "nginx-reverse-http1-tls-ka-tiny", "yarp-reverse-http1-tls-ka-tiny"),
        ("New-connection · tiny GET", "twp-reverse-http1-tls-nc-tiny", "nginx-reverse-http1-tls-nc-tiny", "yarp-reverse-http1-tls-nc-tiny"),
        ("Keep-alive · 256 KiB GET", "twp-reverse-http1-tls-ka-256k", "nginx-reverse-http1-tls-ka-256k", "yarp-reverse-http1-tls-ka-256k"),
    ]

    def tls_table(data: dict, is_win: bool) -> str:
        include_he = not is_win
        rows = [
            f"| Workload | {peer_cols(include_he)} |",
            f"|---|{peer_rule(include_he)}|",
        ]
        for label, t, n, y in tls_spec:
            rows.append(peer_row([label], t, n, y, data, win_no_haproxy_envoy=is_win))
        return "\n".join(rows)

    def sat_block_a(data: dict) -> str:
        origin = data.get("origin-direct")
        op = origin["Peak"] if origin else None
        peer_keys = ["nginx-reverse-http1", "yarp-reverse-http1", "twp-reverse-http1"]
        medal = pick_medal([(k, data.get(k)) for k in peer_keys])
        arms = [
            ("origin-direct", "dotnet-httpclient", False),
            ("origin-direct-bombardier", "bombardier", False),
            ("bare-reverse-http1", "dotnet-httpclient", False),
            ("nginx-reverse-http1", "dotnet-httpclient", True),
            ("yarp-reverse-http1", "dotnet-httpclient", True),
            ("twp-reverse-http1", "dotnet-httpclient", True),
        ]
        rows = [
            "| Arm | Generator | RPS | % of origin-HttpClient |",
            "|---|---|---:|---:|",
        ]
        for arm, gen, is_peer in arms:
            m = data.get(arm)
            med = is_peer and medal == arm
            pct = "—"
            if m and op and op > 0:
                pct = f"**{m['Peak'] / op * 100:.1f}%**"
            rows.append(
                f"| {arm} | {gen} | {fmt_cell(m, medal=med)} | {pct} |"
            )
        return "\n".join(rows)

    def sat_block_bc(data: dict, nginx_a: str, yarp_a: str, twp_a: str, win_no_nginx: bool = False) -> str:
        nginx = None if win_no_nginx else data.get(nginx_a)
        yarp = data.get(yarp_a)
        twp = data.get(twp_a)
        medal = pick_medal([("nginx", nginx), ("yarp", yarp), ("twp", twp)])

        def rdiv(num: Optional[dict], den: Optional[dict]) -> str:
            if not num or not den or not den["Peak"]:
                return "—"
            return f"**{num['Peak'] / den['Peak']:.2f}×**"

        rows = [
            "| Arm | Generator | RPS | ÷YARP | ÷nginx |",
            "|---|---|---:|---:|---:|",
        ]
        for arm, m, imp, key in [
            (nginx_a, nginx, "Not possible (no QUIC)" if win_no_nginx else None, "nginx"),
            (yarp_a, yarp, None, "yarp"),
            (twp_a, twp, None, "twp"),
        ]:
            if imp:
                rows.append(f"| {arm} | dotnet-httpclient | *{imp}* | — | — |")
                continue
            rows.append(
                f"| {arm} | dotnet-httpclient | {fmt_cell(m, medal=(medal == key))} | "
                f"{rdiv(m, yarp)} | {rdiv(m, nginx)} |"
            )
        return "\n".join(rows)

    # Saturation intro
    sat_intro = (
        f"Calibration for the shared 4 vCPU loopback shape: how close client + origin are to saturated before ranking reverse peers. "
        f"Tiny keep-alive GET. Median of **3** repeats @ `{HEAD}` — [{rid_s}]({run_url(rid_s)}). "
        f"Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Block A **% of origin-HttpClient** uses median **peak** RPS. "
        f"Blocks B/C use peer÷YARP / ÷nginx on median peak (not % of H1 origin). **RPS cells** embed median RSS / CPU for the **proxy child** plus its **full descendant tree** "
        f"(serve-proxy → nginx master → workers); origin-direct samples the **origin** child. Product matrices below use matched `dotnet-httpclient` only (not bombardier).\n"
    )
    # Saturation is Linux-only unless a Windows or macOS folder was actually downloaded.
    sat_win = has_os(runs["saturation"], "windows-latest")
    sat_lin = has_os(runs["saturation"], "ubuntu-latest")
    sat_mac = has_os(runs["saturation"], mac_folder)
    if sat_win and sat_lin:
        text = re.sub(
            r"Calibration for the shared 4 vCPU loopback shape:.*?(?=\n\n#### Block A)",
            sat_intro.rstrip() + "\n",
            text,
            count=1,
            flags=re.S,
        )

    # Block A
    a = text.find("#### Block A — H1 plain")
    b = text.find("#### Block B — H2 TLS→H1")
    block = text[a:b]
    if sat_win:
        w = block.find("**Windows**")
        block = replace_table_at(block, block.find("| Arm | Generator | RPS", w), sat_block_a(win["saturation"]))
    if sat_lin:
        l = block.find("**Linux**")
        block = replace_table_at(block, block.find("| Arm | Generator | RPS", l), sat_block_a(lin["saturation"]))
    if sat_mac:
        mac_a = sat_block_a(mac["saturation"])
        if "**macOS**" not in block:
            mac_section = f"\n\n**macOS** (`{mac_folder}`, Apple Silicon)\n\n" + mac_a + "\n"
            prose = block.find("\nReverse peers are about")
            if prose < 0:
                prose = block.find("\nOn this macOS run")
            block = (block[:prose] + mac_section + block[prose:]) if prose > 0 else block.rstrip() + mac_section
        else:
            mpos = block.find("**macOS**")
            block = replace_table_at(block, block.find("| Arm |", mpos), mac_a)
    def peak_pct(data: dict, arm: str, origin: str) -> str:
        num = data.get(arm)
        den = data.get(origin)
        if not num or not den or not den.get("Peak"):
            return "—"
        return f"{num['Peak'] / den['Peak'] * 100:.1f}%"
    if sat_win and sat_lin:
        block = re.sub(
            r"Reverse peers on Block A @ .*?Bare and origin-direct are controls \(not medal peers\)\.",
            (
                f"Reverse peers on Block A @ `{HEAD}`: Windows TWP is **{peak_pct(win['saturation'], 'twp-reverse-http1', 'origin-direct')}** "
                f"of origin-direct and Linux TWP is **{peak_pct(lin['saturation'], 'twp-reverse-http1', 'origin-direct')}**. "
                f"Prefer the **%** column over absolute RPS across runs. Bare and origin-direct are controls (not medal peers)."
            ),
            block,
            count=1,
            flags=re.S,
        )
    if sat_mac:
        mac_origin = mac["saturation"].get("origin-direct")
        if mac_origin:
            block = re.sub(
                r"On this macOS run `origin-direct` was \*\*[0-9.]+%\*\* CPU",
                f"On this macOS run `origin-direct` was **{mac_origin['Cpu']:.1f}%** CPU",
                block,
                count=1,
            )
    text = text[:a] + block + text[b:]

    # Block B
    b = text.find("#### Block B — H2 TLS→H1")
    c = text.find("#### Block C — H3→H1")
    block = text[b:c]
    if sat_win:
        w = block.find("**Windows**")
        block = replace_table_at(
            block,
            block.find("| Arm |", w),
            sat_block_bc(win["saturation"], "nginx-reverse-http2", "yarp-reverse-http2", "twp-reverse-http2-cleartext"),
        )
    if sat_lin:
        l = block.find("**Linux**")
        block = replace_table_at(
            block,
            block.find("| Arm |", l),
            sat_block_bc(lin["saturation"], "nginx-reverse-http2", "yarp-reverse-http2", "twp-reverse-http2-cleartext"),
        )
    if sat_mac:
        mac_b = sat_block_bc(
            mac["saturation"],
            "nginx-reverse-http2",
            "yarp-reverse-http2",
            "twp-reverse-http2-cleartext",
        )
        if "**macOS**" not in block:
            block = block.rstrip() + f"\n\n**macOS** (`{mac_folder}`, Apple Silicon)\n\n" + mac_b + "\n"
        else:
            mpos = block.find("**macOS**")
            block = replace_table_at(block, block.find("| Arm |", mpos), mac_b)
    text = text[:b] + block + text[c:]

    # Block C
    c = text.find("#### Block C — H3→H1")
    # End of saturation section (do not use early "## How to read the tables" TOC heading)
    how = text.find("\n## Windows — Titanium", c)
    if how < 0:
        how = text.find("\n## Windows", c)
    if how < 0:
        raise SystemExit("missing end of saturation Block C")
    block = text[c:how]
    if sat_win:
        w = block.find("**Windows**")
        block = replace_table_at(
            block,
            block.find("| Arm |", w),
            sat_block_bc(
                win["saturation"],
                "nginx-reverse-http3-cleartext",
                "yarp-reverse-http3-cleartext",
                "twp-reverse-http3-cleartext",
                win_no_nginx=True,
            ),
        )
    if sat_lin:
        l = block.find("**Linux**")
        block = replace_table_at(
            block,
            block.find("| Arm |", l),
            sat_block_bc(
                lin["saturation"],
                "nginx-reverse-http3-cleartext",
                "yarp-reverse-http3-cleartext",
                "twp-reverse-http3-cleartext",
            ),
        )
    if sat_mac:
        mac_table = sat_block_bc(
            mac["saturation"],
            "nginx-reverse-http3-cleartext",
            "yarp-reverse-http3-cleartext",
            "twp-reverse-http3-cleartext",
        )
        if "**macOS**" not in block:
            block = block.rstrip() + f"\n\n**macOS** (`{mac_folder}`, Apple Silicon)\n\n" + mac_table + "\n"
        else:
            mpos = block.find("**macOS**")
            block = replace_table_at(block, block.find("| Arm |", mpos), mac_table)
    remeasure = block.find("Re-measured @")
    win_heading = block.find("\n\n**Windows**")
    if sat_win and sat_lin and remeasure > 0 and win_heading > remeasure:
        def peer_aside(os_name: str, data: dict) -> str:
            bits = []
            for label, arm in (
                ("HAProxy", "haproxy-reverse-http3-cleartext"),
                ("Envoy", "envoy-reverse-http3-cleartext"),
            ):
                measured = data.get(arm)
                if measured and ((measured.get("Sustain") or 0) > 0 or (measured.get("Peak") or 0) > 0):
                    bits.append(f"{label} {fmt_cell(measured)}")
            if not bits:
                return ""
            return f" On the {os_name} run " + " and ".join(bits) + "."

        para = (
            f"Medals are nginx / YARP / Titanium only, from the same run as Blocks A and B "
            f"([{rid_s}]({run_url(rid_s)}))."
            + peer_aside("Linux", lin["saturation"])
            + peer_aside("macOS", mac["saturation"])
        )
        block = block[:remeasure] + para + "\n" + block[win_heading + 2 :]
    text = text[:c] + block + text[how:]

    def patch_heavier(heading: str, new_hdr: str, new_table: str) -> None:
        nonlocal text
        i = text.find(heading)
        if i < 0:
            raise SystemExit(f"missing {heading}")
        # Find first data table after heading (Body/Client/Scenario/Workload)
        m = re.search(r"\n(\| (?:Body|Client|Scenario|Workload) \|)", text[i:])
        if not m:
            raise SystemExit(f"no table under {heading}")
        tbl = i + m.start() + 1
        # Replace Median / Userspace header line(s) between heading and table
        chunk = text[i:tbl]
        # Drop stale peer-omission notes; new_hdr may re-add a single copy.
        chunk = re.sub(
            r"(?:\*Not possible:\* \*\*HAProxy\*\* and \*\*Envoy\*\* columns are omitted[^\n]*\n)+",
            "",
            chunk,
        )
        chunk = re.sub(
            r"\nH1-client rows in this table were re-measured @[^\n]*\n",
            "\n",
            chunk,
        )
        chunk2 = re.sub(
            r"(Median of \*\*3\*\*[^\n]*\n|Userspace[^\n]*\n(?:[^\n]*\n)?)",
            new_hdr if new_hdr.endswith("\n") else new_hdr + "\n",
            chunk,
            count=1,
        )
        text = text[:i] + chunk2 + text[tbl:]
        tbl = text.find(m.group(1), i)
        text = replace_table_at(text, tbl, new_table)

    patch_heavier(
        "### Windows — heavier reverse GET (64 KiB / 256 KiB)",
        f"Median of **3** repeats on `windows-latest` @ `{HEAD}`. Source: Actions [{rid_b}]({run_url(rid_b)}) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.\n\n{WIN_NO_HAPROXY_ENVOY_NOTE}",
        bodies_table(win["bodies"], True),
    )
    patch_heavier(
        "### Linux — heavier reverse GET (64 KiB / 256 KiB)",
        f"Median of **3** repeats @ `{HEAD}`. Source: Actions [{rid_b}]({run_url(rid_b)}) (`compare-bodies`). Warmup 2s / measure 8s.\n",
        bodies_table(lin["bodies"], False),
    )
    if has_os(runs["post"], "windows-latest"):
        patch_heavier(
            "### Windows — POST 64 KiB request + 64 KiB response",
            f"Median of **3** repeats on `windows-latest` @ `{HEAD}`. Source: Actions [{rid_p}]({run_url(rid_p)}) (`compare-post`).\n\n{WIN_NO_HAPROXY_ENVOY_NOTE}",
            post_table(win["post"], True),
        )
    if has_os(runs["post"], "ubuntu-latest"):
        patch_heavier(
            "### Linux — POST 64 KiB request + 64 KiB response",
            f"Median of **3** repeats @ `{HEAD}`. Source: Actions [{rid_p}]({run_url(rid_p)}) (`compare-post`).\n",
            post_table(lin["post"], False),
        )
    if has_os(runs["lossy"], "windows-latest"):
        patch_heavier(
            "### Windows — lossy / high-RTT (H2 HOL / H3 loss)",
            f"Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2); UDP is **loss% only** (no per-datagram delay; MsQuic-safe) + **1%** datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `{HEAD}` — [{rid_l}]({run_url(rid_l)}) (`compare-lossy`).\n\n{WIN_NO_HAPROXY_ENVOY_NOTE}",
            lossy_table(win["lossy"], True),
        )
    if has_os(runs["lossy"], "ubuntu-latest"):
        patch_heavier(
            "### Linux — lossy / high-RTT (H2 HOL / H3 loss)",
            f"Median of **3** repeats @ `{HEAD}`. Source: [{rid_l}]({run_url(rid_l)}) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).\n",
            lossy_table(lin["lossy"], False),
        )

    arch_win = has_os(runs["arch"], "windows-latest")
    arch_lin = has_os(runs["arch"], "ubuntu-latest")
    if arch_win and arch_lin:
        text = re.sub(
            r"Median of \*\*3\*\* repeats on matched 4 vCPU / 16 GiB runners @ `[^`]+` \(\[[0-9]+\]\([^)]+\)\)(?: \(`compare-arch`\))?\.",
            f"Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `{HEAD}` ([{rid_a}]({run_url(rid_a)})).",
            text,
            count=1,
        )
    arch = text.find("### Architecture-sensitive")
    if arch_win:
        w = text.find("#### Windows", arch)
        arch_w = text.find("| Scenario |", w)
        text = replace_table_at(text, arch_w, arch_table(win["arch"], True))
    if arch_lin:
        l = text.find("#### Linux", text.find("### Architecture-sensitive"))
        arch_l = text.find("| Scenario |", l)
        text = replace_table_at(text, arch_l, arch_table(lin["arch"], False))

    if has_os(runs["tls"], "windows-latest"):
        tls = text.find("### TLS termination cost")
        w = text.find("#### Windows", tls)
        text2 = text[w:]
        text2 = re.sub(
            r"Median of \*\*3\*\* repeats on `windows-latest` @ `[^`]+`\. Source: Actions \[[0-9]+\]\([^)]+\)(?: \(`compare-tls-cost`\))?\.[^\n]*\n",
            f"Median of **3** repeats on `windows-latest` @ `{HEAD}`. Source: Actions [{rid_t}]({run_url(rid_t)}). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.\n",
            text2,
            count=1,
        )
        tls_tbl_w = text2.find("| Workload |")
        text2 = replace_table_at(text2, tls_tbl_w, tls_table(win["tls"], True))
        text = text[:w] + text2

    if has_os(runs["tls"], "ubuntu-latest"):
        tls = text.find("### TLS termination cost")
        l = text.find("#### Linux", tls)
        text2 = text[l:]
        text2 = re.sub(
            r"Median of \*\*3\*\* repeats @ `[^`]+`\. Source: Actions \[[0-9]+\]\([^)]+\)(?: \(`compare-tls-cost`\))?\.\n",
            f"Median of **3** repeats @ `{HEAD}`. Source: Actions [{rid_t_lin}]({run_url(rid_t_lin)}).\n",
            text2,
            count=1,
        )
        tls_tbl_l = text2.find("| Workload |")
        text2 = replace_table_at(text2, tls_tbl_l, tls_table(lin["tls"], False))
        text = text[:l] + text2

    if has_os(runs["tls"], mac_folder):
        tls = text.find("### TLS termination cost")
        mac_hdr = (
            f"#### macOS\n\n"
            f"Median of **3** repeats on `{mac_folder}` @ `{HEAD}`. "
            f"Source: Actions [{rid_t_mac}]({run_url(rid_t_mac)}).\n\n"
        )
        mac_tbl = tls_table(mac["tls"], False)
        mac_section = mac_hdr + mac_tbl + "\n\n"
        m = text.find("#### macOS", tls)
        next_h2 = text.find("\n## ", tls + 1)
        if next_h2 < 0:
            next_h2 = len(text)
        prose_pat = re.compile(
            r"\n(?:All three workloads are \*\*>1\.00×\*\* YARP|On Windows, TWP leads YARP on keep-alive tiny)[^\n]*\n",
        )
        mac_prose = (
            "\nPrefer **TWP÷YARP** in the table. Absolute RPS on GHA swings hard. "
            "New-connection is Darwin SslStream-bound for TWP and YARP (handshake p99 SLO "
            "**500 ms** on macOS only — Win/Linux stay at **200 ms**).\n"
        )
        if m >= 0 and m < next_h2:
            # Replace existing macOS TLS subsection through next ####/## or prose.
            end = next_h2
            prose_m = prose_pat.search(text, m, next_h2)
            if prose_m:
                end = prose_m.start()
            else:
                nxt = text.find("\n#### ", m + 1)
                if 0 <= nxt < next_h2:
                    end = nxt
            text = text[:m] + mac_section.rstrip() + "\n" + text[end:]
        else:
            # Insert after Linux TLS table, before closing prose / next ##.
            linux = text.find("#### Linux", tls)
            tbl = text.find("| Workload |", linux)
            # skip table
            lines = text[tbl:].splitlines()
            i = 0
            while i < len(lines) and (lines[i].startswith("|") or not lines[i].strip()):
                i += 1
            insert_at = tbl + sum(len(lines[j]) + 1 for j in range(i))
            # Prefer splicing just before the summary prose if present.
            prose_m = prose_pat.search(text, linux, next_h2)
            if prose_m:
                insert_at = prose_m.start()
            text = text[:insert_at].rstrip() + "\n\n" + mac_section + text[insert_at:].lstrip("\n")

        text, n_prose = prose_pat.subn(mac_prose, text, count=1)
        if n_prose == 0:
            # Ensure prose sits after macOS table when the old sentence was already edited.
            grpc = text.find("\n## Unary gRPC", tls)
            if grpc > 0 and "Darwin SslStream-bound" not in text[tls:grpc]:
                text = text[:grpc] + mac_prose + text[grpc:]

    def ensure_mac_heading(anchor: str, following: str, heading: str, hdr: str, table: str) -> None:
        nonlocal text
        a = text.find(anchor)
        b = text.find(following, a + len(anchor)) if a >= 0 else -1
        if a < 0 or b < 0:
            raise SystemExit(f"missing anchor {anchor!r} -> {following!r}")
        block = f"{heading}\n\n{hdr}\n\n{table}\n\n"
        pos = text.find(heading, a, b)
        if pos < 0:
            text = text[:b] + block + text[b:]
            return
        text = text[:pos] + block + text[b:]

    mac_intro = (
        f"Median of **3** repeats on `{mac_folder}` (Apple Silicon M1, 3-core / 7 GB) @ `{HEAD}`."
    )
    ensure_mac_heading(
        "### Linux — heavier reverse GET (64 KiB / 256 KiB)",
        "### Windows — POST 64 KiB request + 64 KiB response",
        "### macOS — heavier reverse GET (64 KiB / 256 KiB)",
        f"{mac_intro} Source: Actions [{rid_b_mac}]({run_url(rid_b_mac)}) (`compare-bodies`). Warmup 2s / measure 8s.",
        bodies_table(mac["bodies"], False),
    )
    if has_os(runs["post"], mac_folder):
        ensure_mac_heading(
            "### Linux — POST 64 KiB request + 64 KiB response",
            "### Windows — lossy / high-RTT (H2 HOL / H3 loss)",
            "### macOS — POST 64 KiB request + 64 KiB response",
            f"{mac_intro} Source: Actions [{rid_p_mac}]({run_url(rid_p_mac)}) (`compare-post`).",
            post_table(mac["post"], False),
        )
    if has_os(runs["lossy"], mac_folder):
        ensure_mac_heading(
            "### Linux — lossy / high-RTT (H2 HOL / H3 loss)",
            "### Architecture-sensitive",
            "### macOS — lossy / high-RTT (H2 HOL / H3 loss)",
            f"{mac_intro} Source: [{rid_l_mac}]({run_url(rid_l_mac)}) (`compare-lossy`; lossy H3 uses `quic-http3`, UDP drop-only).",
            lossy_table(mac["lossy"], False),
        )
    if has_os(runs["arch"], mac_folder):
        ensure_mac_heading(
            "#### Linux",
            "Slow consumer is sleep-bound",
            "#### macOS",
            f"{mac_intro} Source: Actions [{rid_a_mac}]({run_url(rid_a_mac)}) (`compare-arch`).",
            arch_table(mac["arch"], False),
        )

    text = re.sub(r"\nH1-client rows[^\n]*\n", "\n", text)
    text = re.sub(
        r"\nnginx/Windows collapses on large reverse bodies[^\n]*\n",
        "\nnginx/Windows collapses on large reverse bodies in this harness; treat as same-OS only. Prefer the ratio columns; absolute RPS swings by VM.\n",
        text,
        count=1,
    )
    text = re.sub(
        r"\nOn this GHA pass TWP÷YARP H1 TLS[^\n]*\n",
        "\nPrefer the ratio columns; absolute RPS swings by VM.\n",
        text,
        count=1,
    )
    text = re.sub(
        r"\nH1 is near parity with YARP\.[^\n]*\n",
        "\nH1, H2, and H3 loss sustain are in the table. Prefer the ratio columns.\n",
        text,
        count=1,
    )
    text = re.sub(
        r"\nSlow consumer is sleep-bound;[^\n]*\n",
        "\nSlow consumer is sleep-bound; H1/H2/H3 sit in the same band. Prefer the ratio columns for early-response, duplex, and WebSocket rows.\n",
        text,
        count=1,
    )
    text = re.sub(
        r"\(H1-client rows re-measured @ `[^`]+`; other rows stay @ `[^`]+`\)",
        f"(@ `{HEAD}`)",
        text,
        count=1,
    )

    WIKI.write_text(text, encoding="utf-8")
    print("heavier+saturation pasted")
    for s in ("9d7c2966", "32871900682", "32866709227", HEAD, str(rid_b), str(rid_t_mac)):
        print(f"  count {s}={text.count(s)}")


if __name__ == "__main__":
    main()
