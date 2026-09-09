#!/usr/bin/env python3
"""Paste heavier + saturation tables into wiki/Performance.md from gha-dl CSVs."""
from __future__ import annotations

import csv
import re
from pathlib import Path
from typing import Dict, List, Optional, Tuple

ROOT = Path("tools/RpsLoadProbe/results/gha-dl")
WIKI = Path("wiki/Performance.md")
HEAD = "84b225f7"
RUNS = {
    "saturation": 34355140813,
    "bodies": 34355153953,
    "post": 34355158209,
    "lossy": 34355162890,
    "tls": 34355173969,
    "arch": 34355169025,
}
MEDAL = "\U0001F947"
STEPS = 4


def median(vals: List[float]) -> Optional[float]:
    if not vals:
        return None
    s = sorted(vals)
    return s[(len(s) - 1) // 2]


def arm_metrics(csv_path: Path, arm: str) -> Optional[dict]:
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
            c64_any = [r for r in chunk if r.get("concurrency") == "64"]
            if c64_any:
                r = c64_any[-1]
                sustains.append(0.0)
                peaks.append(float(r["rps"]))
                rss.append(float(r.get("proxy_rss_peak_bytes") or 0))
                cpu.append(float(r.get("proxy_cpu_avg_pct") or 0))
        i += STEPS
    if not peaks:
        c64 = [r for r in rows if r.get("concurrency") == "64"]
        if not c64:
            return None
        r = c64[-1]
        ok = r.get("meets_slo") == "1"
        return {
            "Sustain": float(r["rps"]) if ok else 0.0,
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


def load_os(run_id: int, os_folder: str) -> Dict[str, dict]:
    d = ROOT / str(run_id) / f"rps-csv-{os_folder}"
    files = list(d.glob("*.csv"))
    if not files:
        return {}
    arms = {r["arm"] for r in csv.DictReader(files[0].open(newline=""))}
    return {a: m for a in arms if (m := arm_metrics(files[0], a))}


def fmt_cell(m: Optional[dict], medal: bool = False, peak: bool = False, impossible: Optional[str] = None) -> str:
    if impossible:
        return f"*{impossible}*"
    if not m:
        return "*Not measured*"
    r = round(m["Peak"] if peak else m["Sustain"])
    mb = round(m["Rss"] / (1024 * 1024))
    cpu = round(m["Cpu"], 1)
    prefix = f"{MEDAL} " if medal else ""
    return f"{prefix}**{r:,}**<br><sub>({mb} MiB / {cpu}% CPU)</sub>"


def pick_medal(cands: List[Tuple[str, Optional[dict]]]) -> Optional[str]:
    valid = [(k, m) for k, m in cands if m and (m["Sustain"] or 0) > 0]
    if not valid:
        return None
    return min(valid, key=lambda km: (-km[1]["Sustain"], km[1]["Rss"], km[1]["Cpu"]))[0]


PEER_COLS = (
    "TWP sustain | TWP peak | nginx sustain | nginx peak | "
    "HAProxy sustain | HAProxy peak | Envoy sustain | Envoy peak | YARP sustain | YARP peak"
)
PEER_RULE = "---:|---:|---:|---:|---:|---:|---:|---:|---:|---:"


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
) -> str:
    twp = data.get(twp_a) if twp_a else None
    nginx = data.get(nginx_a) if nginx_a else None
    haproxy_a = nginx_a.replace("nginx-", "haproxy-", 1) if nginx_a else None
    envoy_a = nginx_a.replace("nginx-", "envoy-", 1) if nginx_a else None
    haproxy = data.get(haproxy_a) if haproxy_a else None
    envoy = data.get(envoy_a) if envoy_a else None
    yarp = data.get(yarp_a) if yarp_a else None
    nginx_imp = _peer_impossible(nginx_a, nginx_a, win_no_quic)
    haproxy_imp = "Not possible" if win_no_haproxy_envoy else _peer_impossible(haproxy_a, nginx_a, win_no_quic)
    envoy_imp = "Not possible" if win_no_haproxy_envoy else _peer_impossible(envoy_a, nginx_a, win_no_quic)
    if win_no_quic and nginx_a and "http3" in nginx_a:
        nginx = None
        nginx_imp = "Not possible (no QUIC)"
    if win_no_haproxy_envoy:
        haproxy = None
        envoy = None
    medal = pick_medal(
        [("twp", twp), ("nginx", nginx), ("haproxy", haproxy), ("envoy", envoy), ("yarp", yarp)]
    )
    cells = prefix + [
        fmt_cell(twp, medal=(medal == "twp")),
        fmt_cell(twp, peak=True),
        fmt_cell(nginx, medal=(medal == "nginx"), impossible=nginx_imp),
        fmt_cell(nginx, peak=True, impossible=nginx_imp),
        fmt_cell(haproxy, medal=(medal == "haproxy"), impossible=haproxy_imp),
        fmt_cell(haproxy, peak=True, impossible=haproxy_imp),
        fmt_cell(envoy, medal=(medal == "envoy"), impossible=envoy_imp),
        fmt_cell(envoy, peak=True, impossible=envoy_imp),
        fmt_cell(yarp, medal=(medal == "yarp")),
        fmt_cell(yarp, peak=True),
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
    win = {k: load_os(rid, "windows-latest") for k, rid in RUNS.items()}
    lin = {k: load_os(rid, "ubuntu-latest") for k, rid in RUNS.items()}
    text = WIKI.read_text(encoding="utf-8")
    rid_b, rid_p, rid_l, rid_a, rid_t, rid_s = (
        RUNS[k] for k in ("bodies", "post", "lossy", "arch", "tls", "saturation")
    )

    body_spec = [
        ("64 KiB", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-body64k", "nginx-reverse-http1-tls-body64k", "yarp-reverse-http1-tls-body64k"),
        ("64 KiB", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-body64k", "nginx-reverse-http2-body64k", "yarp-reverse-http2-body64k"),
        ("64 KiB", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-body64k", "nginx-reverse-http3-cleartext-body64k", "yarp-reverse-http3-cleartext-body64k"),
        ("256 KiB", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-body256k", "nginx-reverse-http1-tls-body256k", "yarp-reverse-http1-tls-body256k"),
        ("256 KiB", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-body256k", "nginx-reverse-http2-body256k", "yarp-reverse-http2-body256k"),
        ("256 KiB", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-body256k", "nginx-reverse-http3-cleartext-body256k", "yarp-reverse-http3-cleartext-body256k"),
    ]

    def bodies_table(data: dict, is_win: bool) -> str:
        rows = [
            f"| Body | Client | Origin | {PEER_COLS} |",
            f"|---|---|---|{PEER_RULE}|",
        ]
        for body, c, o, t, n, y in body_spec:
            rows.append(
                peer_row([body, c, o], t, n, y, data, win_no_quic=is_win, win_no_haproxy_envoy=is_win)
            )
        return "\n".join(rows)

    post_spec = [
        ("HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-post64k", "nginx-reverse-http1-tls-post64k", "yarp-reverse-http1-tls-post64k"),
        ("HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-post64k", "nginx-reverse-http2-post64k", "yarp-reverse-http2-post64k"),
        ("HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-post64k", "nginx-reverse-http3-cleartext-post64k", "yarp-reverse-http3-cleartext-post64k"),
    ]

    def post_table(data: dict, is_win: bool) -> str:
        rows = [
            f"| Client | Origin | {PEER_COLS} |",
            f"|---|---|{PEER_RULE}|",
        ]
        for c, o, t, n, y in post_spec:
            rows.append(
                peer_row([c, o], t, n, y, data, win_no_quic=is_win, win_no_haproxy_envoy=is_win)
            )
        return "\n".join(rows)

    lossy_spec = [
        ("HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-lossy", "nginx-reverse-http1-tls-lossy", "yarp-reverse-http1-tls-lossy"),
        ("HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-lossy", "nginx-reverse-http2-lossy", "yarp-reverse-http2-lossy"),
        ("HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-lossy", "nginx-reverse-http3-cleartext-lossy", "yarp-reverse-http3-cleartext-lossy"),
    ]

    def lossy_table(data: dict, is_win: bool) -> str:
        rows = [
            f"| Client | Origin | {PEER_COLS} |",
            f"|---|---|{PEER_RULE}|",
        ]
        for c, o, t, n, y in lossy_spec:
            rows.append(
                peer_row([c, o], t, n, y, data, win_no_quic=is_win, win_no_haproxy_envoy=is_win)
            )
        return "\n".join(rows)

    arch_spec = [
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-slow256k", "nginx-reverse-http1-tls-slow256k", "yarp-reverse-http1-tls-slow256k"),
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-slow256k", "nginx-reverse-http2-slow256k", "yarp-reverse-http2-slow256k"),
        ("Slow consumer (256 KiB GET, throttled client read)", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-slow256k", "nginx-reverse-http3-cleartext-slow256k", "yarp-reverse-http3-cleartext-slow256k"),
        ("Early response (origin writes after first request chunk)", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-early64k", "nginx-reverse-http1-tls-early64k", "yarp-reverse-http1-tls-early64k"),
        ("Early response (origin writes after first request chunk)", "HTTP/2 · TLS", "HTTP/1 · plain", "twp-reverse-http2-cleartext-early64k", "nginx-reverse-http2-early64k", "yarp-reverse-http2-early64k"),
        ("Early response (origin writes after first request chunk)", "HTTP/3 · QUIC", "HTTP/1 · plain", "twp-reverse-http3-cleartext-early64k", "nginx-reverse-http3-cleartext-early64k", "yarp-reverse-http3-cleartext-early64k"),
        ("Duplex (both directions live)", "HTTP/2 · TLS", "HTTP/2 · TLS", "twp-reverse-http2-duplex-h2", None, "yarp-reverse-http2-to-https-duplex-h2"),
        ("Duplex (WebSocket / extended CONNECT)", "HTTP/1 · TLS", "HTTP/1 · plain", "twp-reverse-http1-tls-duplex-ws", "nginx-reverse-http1-tls-duplex-ws", "yarp-reverse-http1-tls-duplex-ws"),
    ]

    def arch_table(data: dict, is_win: bool) -> str:
        rows = [
            f"| Scenario | Client | Origin | {PEER_COLS} |",
            f"|---|---|---|{PEER_RULE}|",
        ]
        for sc, c, o, t, n, y in arch_spec:
            rows.append(
                peer_row(
                    [sc, c, o],
                    t,
                    n,
                    y,
                    data,
                    win_no_quic=is_win and bool(n and "http3" in n),
                    win_no_haproxy_envoy=is_win,
                )
            )
        return "\n".join(rows)

    tls_spec = [
        ("Keep-alive · tiny GET", "twp-reverse-http1-tls-ka-tiny", "nginx-reverse-http1-tls-ka-tiny", "yarp-reverse-http1-tls-ka-tiny"),
        ("New-connection · tiny GET", "twp-reverse-http1-tls-nc-tiny", "nginx-reverse-http1-tls-nc-tiny", "yarp-reverse-http1-tls-nc-tiny"),
        ("Keep-alive · 256 KiB GET", "twp-reverse-http1-tls-ka-256k", "nginx-reverse-http1-tls-ka-256k", "yarp-reverse-http1-tls-ka-256k"),
    ]

    def tls_table(data: dict, is_win: bool) -> str:
        rows = [
            f"| Workload | {PEER_COLS} |",
            f"|---|{PEER_RULE}|",
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
            "| Arm | Generator | Sustain | Peak | % of origin-HttpClient |",
            "|---|---|---:|---:|---:|",
        ]
        for arm, gen, is_peer in arms:
            m = data.get(arm)
            med = is_peer and medal == arm
            pct = "—"
            if m and op and op > 0:
                pct = f"**{m['Peak'] / op * 100:.1f}%**"
            rows.append(
                f"| {arm} | {gen} | {fmt_cell(m, medal=med)} | {fmt_cell(m, peak=True)} | {pct} |"
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
            "| Arm | Generator | Sustain | Peak | ÷YARP | ÷nginx |",
            "|---|---|---:|---:|---:|---:|",
        ]
        for arm, m, imp, key in [
            (nginx_a, nginx, "Not possible (no QUIC)" if win_no_nginx else None, "nginx"),
            (yarp_a, yarp, None, "yarp"),
            (twp_a, twp, None, "twp"),
        ]:
            if imp:
                rows.append(f"| {arm} | dotnet-httpclient | *{imp}* | *{imp}* | — | — |")
                continue
            rows.append(
                f"| {arm} | dotnet-httpclient | {fmt_cell(m, medal=(medal == key))} | {fmt_cell(m, peak=True)} | "
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
    text = re.sub(
        r"Calibration for the shared 4 vCPU loopback shape:.*?(?=\n\n\n```powershell\npwsh tools/RpsLoadProbe/run-rps\.ps1 -Mode compare-saturation)",
        sat_intro.rstrip() + "\n",
        text,
        count=1,
        flags=re.S,
    )

    # Block A
    a = text.find("#### Block A — H1 plain")
    b = text.find("#### Block B — H2 TLS→H1")
    block = text[a:b]
    w = block.find("**Windows**")
    l = block.find("**Linux**")
    block = replace_table_at(block, block.find("| Arm | Generator | Sustain", w), sat_block_a(win["saturation"]))
    l = block.find("**Linux**")
    block = replace_table_at(block, block.find("| Arm | Generator | Sustain", l), sat_block_a(lin["saturation"]))
    text = text[:a] + block + text[b:]

    # Block B
    b = text.find("#### Block B — H2 TLS→H1")
    c = text.find("#### Block C — H3→H1")
    block = text[b:c]
    w = block.find("**Windows**")
    block = replace_table_at(
        block,
        block.find("| Arm |", w),
        sat_block_bc(win["saturation"], "nginx-reverse-http2", "yarp-reverse-http2", "twp-reverse-http2-cleartext"),
    )
    l = block.find("**Linux**")
    block = replace_table_at(
        block,
        block.find("| Arm |", l),
        sat_block_bc(lin["saturation"], "nginx-reverse-http2", "yarp-reverse-http2", "twp-reverse-http2-cleartext"),
    )
    text = text[:b] + block + text[c:]

    # Block C
    c = text.find("#### Block C — H3→H1")
    how = text.find("**How to read the tables**")
    block = text[c:how]
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
    text = text[:c] + block + text[how:]

    def patch_heavier(heading: str, new_hdr: str, new_table: str, chart_md: Optional[str] = None) -> None:
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
        chunk2 = re.sub(
            r"(Median of \*\*3\*\*[^\n]*\n|Userspace[^\n]*\n(?:[^\n]*\n)?)",
            new_hdr if new_hdr.endswith("\n") else new_hdr + "\n",
            chunk,
            count=1,
        )
        text = text[:i] + chunk2 + text[tbl:]
        tbl = text.find(m.group(1), i)
        if chart_md:
            before = text[i:tbl]
            if chart_md not in before:
                text = text[:tbl] + chart_md + "\n\n" + text[tbl:]
                tbl = text.find(m.group(1), i)
        text = replace_table_at(text, tbl, new_table)

    patch_heavier(
        "### Windows — heavier reverse GET (64 KiB / 256 KiB)",
        f"Median of **3** repeats on `windows-latest` @ `{HEAD}`. Source: Actions [{rid_b}]({run_url(rid_b)}) (`compare-bodies`). Warmup 2s / measure 8s. **RPS cells** include `(MiB / CPU%)` footprints.\n",
        bodies_table(win["bodies"], True),
        "![Windows heavier bodies](images/rps-heavier-bodies-windows.png)",
    )
    patch_heavier(
        "### Linux — heavier reverse GET (64 KiB / 256 KiB)",
        f"Median of **3** repeats @ `{HEAD}`. Source: Actions [{rid_b}]({run_url(rid_b)}) (`compare-bodies`). Warmup 2s / measure 8s.\n",
        bodies_table(lin["bodies"], False),
        "![Linux heavier bodies](images/rps-heavier-bodies-linux.png)",
    )
    patch_heavier(
        "### Windows — POST 64 KiB request + 64 KiB response",
        f"Median of **3** repeats on `windows-latest` @ `{HEAD}`. Source: Actions [{rid_p}]({run_url(rid_p)}) (`compare-post`).\n",
        post_table(win["post"], True),
        "![Windows heavier POST](images/rps-heavier-post-windows.png)",
    )
    patch_heavier(
        "### Linux — POST 64 KiB request + 64 KiB response",
        f"Median of **3** repeats @ `{HEAD}`. Source: Actions [{rid_p}]({run_url(rid_p)}) (`compare-post`).\n",
        post_table(lin["post"], False),
        "![Linux heavier POST](images/rps-heavier-post-linux.png)",
    )
    patch_heavier(
        "### Windows — lossy / high-RTT (H2 HOL / H3 loss)",
        f"Userspace **5 ms** one-way delay + **1%** TCP connection stall (H1/H2) or UDP datagram drop (H3); **64 KiB** GET. Median of **3** repeats on `windows-latest` @ `{HEAD}` — [{rid_l}]({run_url(rid_l)}) (`compare-lossy`).\n",
        lossy_table(win["lossy"], True),
        "![Windows heavier lossy](images/rps-heavier-lossy-windows.png)",
    )
    patch_heavier(
        "### Linux — lossy / high-RTT (H2 HOL / H3 loss)",
        f"Median of **3** repeats @ `{HEAD}`. Source: [{rid_l}]({run_url(rid_l)}) (`compare-lossy`; lossy H3 uses `quic-http3`).\n",
        lossy_table(lin["lossy"], False),
        "![Linux heavier lossy](images/rps-heavier-lossy-linux.png)",
    )

    text = re.sub(
        r"Median of \*\*3\*\* repeats on matched 4 vCPU / 16 GiB runners @ `[^`]+` \(\[[0-9]+\]\([^)]+\)\) \(`compare-arch`\)\.",
        f"Median of **3** repeats on matched 4 vCPU / 16 GiB runners @ `{HEAD}` ([{rid_a}]({run_url(rid_a)})) (`compare-arch`).",
        text,
        count=1,
    )
    arch = text.find("### Architecture-sensitive")
    w = text.find("#### Windows", arch)
    l = text.find("#### Linux", w)
    arch_w = text.find("| Scenario |", w)
    before_arch_w = text[w:arch_w]
    arch_chart_w = "![Windows heavier arch](images/rps-heavier-arch-windows.png)"
    if arch_chart_w not in before_arch_w:
        text = text[:arch_w] + arch_chart_w + "\n\n" + text[arch_w:]
        arch_w = text.find("| Scenario |", w)
    text = replace_table_at(text, arch_w, arch_table(win["arch"], True))
    l = text.find("#### Linux", text.find("### Architecture-sensitive"))
    arch_l = text.find("| Scenario |", l)
    before_arch_l = text[l:arch_l]
    arch_chart_l = "![Linux heavier arch](images/rps-heavier-arch-linux.png)"
    if arch_chart_l not in before_arch_l:
        text = text[:arch_l] + arch_chart_l + "\n\n" + text[arch_l:]
        arch_l = text.find("| Scenario |", l)
    text = replace_table_at(text, arch_l, arch_table(lin["arch"], False))

    tls = text.find("### TLS termination cost")
    w = text.find("#### Windows", tls)
    text2 = text[w:]
    text2 = re.sub(
        r"Median of \*\*3\*\* repeats on `windows-latest` @ `[^`]+`\. Source: Actions \[[0-9]+\]\([^)]+\) \(`compare-tls-cost`\)\.[^\n]*\n",
        f"Median of **3** repeats on `windows-latest` @ `{HEAD}`. Source: Actions [{rid_t}]({run_url(rid_t)}) (`compare-tls-cost`). Absolute RPS on GHA swings hard; prefer **TWP÷YARP**.\n",
        text2,
        count=1,
    )
    tls_tbl_w = text2.find("| Workload |")
    tls_chart_w = "![Windows heavier TLS cost](images/rps-heavier-tls-cost-windows.png)"
    if tls_chart_w not in text2[:tls_tbl_w]:
        text2 = text2[:tls_tbl_w] + tls_chart_w + "\n\n" + text2[tls_tbl_w:]
        tls_tbl_w = text2.find("| Workload |")
    text2 = replace_table_at(text2, tls_tbl_w, tls_table(win["tls"], True))
    text = text[:w] + text2

    tls = text.find("### TLS termination cost")
    l = text.find("#### Linux", tls)
    text2 = text[l:]
    text2 = re.sub(
        r"Median of \*\*3\*\* repeats @ `[^`]+`\. Source: Actions \[[0-9]+\]\([^)]+\) \(`compare-tls-cost`\)\.\n",
        f"Median of **3** repeats @ `{HEAD}`. Source: Actions [{rid_t}]({run_url(rid_t)}) (`compare-tls-cost`).\n",
        text2,
        count=1,
    )
    tls_tbl_l = text2.find("| Workload |")
    tls_chart_l = "![Linux heavier TLS cost](images/rps-heavier-tls-cost-linux.png)"
    if tls_chart_l not in text2[:tls_tbl_l]:
        text2 = text2[:tls_tbl_l] + tls_chart_l + "\n\n" + text2[tls_tbl_l:]
        tls_tbl_l = text2.find("| Workload |")
    text2 = replace_table_at(text2, tls_tbl_l, tls_table(lin["tls"], False))
    text = text[:l] + text2

    WIKI.write_text(text, encoding="utf-8")
    print("heavier+saturation pasted")
    for s in ("9d7c2966", "32871900682", "32866709227", HEAD, str(rid_b)):
        print(f"  count {s}={text.count(s)}")


if __name__ == "__main__":
    main()
