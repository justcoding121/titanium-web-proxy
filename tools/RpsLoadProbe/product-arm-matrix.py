#!/usr/bin/env python3
"""Product-possible vs harness-present reverse arms (compare-product 5×5).

TWP and YARP already cover all 25 Client×Origin cells. This file tracks nginx /
HAProxy / Envoy: present ProbeMode, product-possible but no arm, or impossible.

    python3 tools/RpsLoadProbe/product-arm-matrix.py          # gap table
    python3 tools/RpsLoadProbe/product-arm-matrix.py --json
    python3 tools/RpsLoadProbe/product-arm-matrix.py --counts

Linux/macOS only for HAProxy and Envoy (no official Windows port). Windows nginx
has no QUIC — H3 inbound arms exist but cells are OS-impossible, not harness gaps.

Heavier modes (`compare-bodies` / `post` / `lossy` / `tls-cost` / `arch` slow+early)
clone terminate-to-H1c plus remainder wires (h2c→H1, H2 TLS→h2c, H2 TLS→H2 TLS,
H3→H2 TLS, H3→H1 TLS). Arch duplex H2 TLS↔H2 TLS includes HAProxy/Envoy.
  present     ProbeMode + compare-product inclusion
  absent      product can do the wire; no host/ProbeMode/ramp arm yet
  impossible  stock product cannot speak that origin (or inbound) protocol
"""

from __future__ import annotations

import argparse
import json
from typing import Dict, List, Optional, Tuple

H1C = "HTTP/1 · plain"
H1T = "HTTP/1 · TLS"
H2C = "HTTP/2 · plain"
H2T = "HTTP/2 · TLS"
H3 = "HTTP/3 · QUIC"

CLIENTS = (H1C, H1T, H2C, H2T, H3)
ORIGINS = CLIENTS
PRODUCTS = ("nginx", "haproxy", "envoy")

# Planned CSV names that now have ProbeModes (kept for docs / paste scripts).
IMPLEMENTED_REMAINDER: Dict[Tuple[str, str, str], str] = {
    # nginx: prior-knowledge h2c inbound (mainline 1.25.1+ `http2 on` without ssl).
    ("nginx", H2C, H1C): "nginx-reverse-h2c-to-h1",
    ("nginx", H2C, H1T): "nginx-reverse-h2c-to-https",
    # HAProxy / Envoy: every cell that is not one of the eight terminate-to-H1 arms.
    ("haproxy", H1C, H2C): "haproxy-reverse-http1-plain-to-h2c",
    ("haproxy", H1C, H2T): "haproxy-reverse-http1-plain-to-http2",
    ("haproxy", H1C, H3): "haproxy-reverse-http1-plain-to-http3",
    ("haproxy", H1T, H2C): "haproxy-reverse-http1-to-h2c",
    ("haproxy", H1T, H2T): "haproxy-reverse-http11-to-http2",
    ("haproxy", H1T, H3): "haproxy-reverse-http1-to-http3",
    ("haproxy", H2C, H1C): "haproxy-reverse-h2c-to-h1",
    ("haproxy", H2C, H1T): "haproxy-reverse-h2c-to-https",
    ("haproxy", H2C, H2C): "haproxy-reverse-h2c-to-h2c",
    ("haproxy", H2C, H2T): "haproxy-reverse-h2c",
    ("haproxy", H2C, H3): "haproxy-reverse-h2c-to-h3",
    ("haproxy", H2T, H2C): "haproxy-reverse-http2-to-h2c",
    ("haproxy", H2T, H2T): "haproxy-reverse-http2-to-https",
    ("haproxy", H2T, H3): "haproxy-reverse-http2-to-http3",
    ("haproxy", H3, H2C): "haproxy-reverse-http3-to-h2c",
    ("haproxy", H3, H2T): "haproxy-reverse-http3-to-http2",
    ("haproxy", H3, H3): "haproxy-reverse-http3-to-http3",
    ("envoy", H1C, H2C): "envoy-reverse-http1-plain-to-h2c",
    ("envoy", H1C, H2T): "envoy-reverse-http1-plain-to-http2",
    ("envoy", H1C, H3): "envoy-reverse-http1-plain-to-http3",
    ("envoy", H1T, H2C): "envoy-reverse-http1-to-h2c",
    ("envoy", H1T, H2T): "envoy-reverse-http11-to-http2",
    ("envoy", H1T, H3): "envoy-reverse-http1-to-http3",
    ("envoy", H2C, H1C): "envoy-reverse-h2c-to-h1",
    ("envoy", H2C, H1T): "envoy-reverse-h2c-to-https",
    ("envoy", H2C, H2C): "envoy-reverse-h2c-to-h2c",
    ("envoy", H2C, H2T): "envoy-reverse-h2c",
    ("envoy", H2C, H3): "envoy-reverse-h2c-to-h3",
    ("envoy", H2T, H2C): "envoy-reverse-http2-to-h2c",
    ("envoy", H2T, H2T): "envoy-reverse-http2-to-https",
    ("envoy", H2T, H3): "envoy-reverse-http2-to-http3",
    ("envoy", H3, H2C): "envoy-reverse-http3-to-h2c",
    ("envoy", H3, H2T): "envoy-reverse-http3-to-http2",
    ("envoy", H3, H3): "envoy-reverse-http3-to-http3",
}

# Eight terminate-to-H1 (or H1 TLS) arms that already exist for all three peers.
PRESENT_ARMS: Dict[Tuple[str, str, str], str] = {}
for prefix in PRODUCTS:
    PRESENT_ARMS[(prefix, H1C, H1C)] = f"{prefix}-reverse-http1"
    PRESENT_ARMS[(prefix, H1C, H1T)] = f"{prefix}-reverse-http1-to-https"
    PRESENT_ARMS[(prefix, H1T, H1C)] = f"{prefix}-reverse-http1-tls"
    PRESENT_ARMS[(prefix, H1T, H1T)] = f"{prefix}-reverse-http1-tls-to-https"
    PRESENT_ARMS[(prefix, H2T, H1C)] = f"{prefix}-reverse-http2"
    PRESENT_ARMS[(prefix, H2T, H1T)] = f"{prefix}-reverse-http2-to-https-http1"
    PRESENT_ARMS[(prefix, H3, H1C)] = f"{prefix}-reverse-http3-cleartext"
    PRESENT_ARMS[(prefix, H3, H1T)] = f"{prefix}-reverse-http3-to-https-http1"

PRESENT_ARMS.update(IMPLEMENTED_REMAINDER)
ABSENT_ARMS: Dict[Tuple[str, str, str], str] = {}

# How to build the remainder wire (config hint).
ABSENT_HOW: Dict[Tuple[str, str], str] = {
    ("nginx", H2C): "mainline 1.25.1+ `http2 on` without ssl (prior-knowledge h2c) → existing H1 origin",
    ("haproxy", H2C): "`bind ... proto h2` (h2c) or existing QUIC/TLS frontend",
    ("haproxy", H2T): "`server ... ssl verify none alpn h2 proto h2`",
    ("haproxy", H3): "`server ... quic4@127.0.0.1:<port> ssl verify none alpn h3` (3.2 USE_QUIC)",
    ("envoy", H2C): "TCP listener codec HTTP2, no TLS; cluster `http2_protocol_options` without TLS",
    ("envoy", H2T): "cluster `explicit_http_config.http2_protocol_options` + UpstreamTlsContext",
    ("envoy", H3): "cluster QuicUpstreamTransport + `http3_protocol_options` (upstream H3 is alpha)",
}

IMPOSSIBLE_REASON = {
    "h2-up": "Not possible (no H2 upstream)",
    "h3-up": "Not possible (no H3 upstream)",
}

SPECIAL_ABSENT: Tuple = ()


def _origin_kind(origin: str) -> str:
    if origin == H3:
        return "h3"
    if origin in (H2C, H2T):
        return "h2"
    return "h1"


def status(product: str, client: str, origin: str) -> str:
    if (product, client, origin) in PRESENT_ARMS:
        return "present"
    if (product, client, origin) in ABSENT_ARMS:
        return "absent"
    return "impossible"


def arm_name(product: str, client: str, origin: str) -> Optional[str]:
    if (product, client, origin) in PRESENT_ARMS:
        return PRESENT_ARMS[(product, client, origin)]
    if (product, client, origin) in ABSENT_ARMS:
        return ABSENT_ARMS[(product, client, origin)]
    return None


def impossible_reason(product: str, client: str, origin: str) -> Optional[str]:
    if status(product, client, origin) != "impossible":
        return None
    if product != "nginx":
        return "Not possible"
    kind = _origin_kind(origin)
    if kind == "h3":
        return IMPOSSIBLE_REASON["h3-up"]
    if kind == "h2":
        return IMPOSSIBLE_REASON["h2-up"]
    return "Not possible"


def how(product: str, client: str, origin: str) -> Optional[str]:
    if status(product, client, origin) != "absent":
        return None
    if product == "nginx":
        return ABSENT_HOW[("nginx", H2C)]
    origin_kind = _origin_kind(origin)
    if origin_kind == "h1":
        if client == H2C:
            return ABSENT_HOW[(product, H2C)]
        return "existing H1 cluster + inbound protocol from client cell"
    if origin_kind == "h2":
        return ABSENT_HOW[(product, H2C if origin == H2C else H2T)]
    return ABSENT_HOW[(product, H3)]


def absent_rows() -> List[Dict[str, str]]:
    rows = []
    for product in PRODUCTS:
        for client in CLIENTS:
            for origin in ORIGINS:
                if status(product, client, origin) != "absent":
                    continue
                rows.append(
                    {
                        "product": product,
                        "client": client,
                        "origin": origin,
                        "arm": ABSENT_ARMS[(product, client, origin)],
                        "how": how(product, client, origin) or "",
                        "suite": "compare-product",
                    }
                )
    for spec in SPECIAL_ABSENT:
        for product in ("haproxy", "envoy"):
            rows.append(
                {
                    "product": product,
                    "client": H2T,
                    "origin": H2T,
                    "arm": spec[product],
                    "how": spec["note"],
                    "suite": spec["suite"],
                }
            )
    return rows


def counts() -> Dict[str, Dict[str, int]]:
    out: Dict[str, Dict[str, int]] = {}
    for product in PRODUCTS:
        c = {"present": 0, "absent": 0, "impossible": 0}
        for client in CLIENTS:
            for origin in ORIGINS:
                c[status(product, client, origin)] += 1
        out[product] = c
    return out


def as_json() -> dict:
    wires = []
    for client in CLIENTS:
        for origin in ORIGINS:
            cell = {"client": client, "origin": origin}
            for product in PRODUCTS:
                st = status(product, client, origin)
                cell[product] = {
                    "status": st,
                    "arm": arm_name(product, client, origin),
                    "reason": impossible_reason(product, client, origin),
                    "how": how(product, client, origin),
                }
            wires.append(cell)
    return {
        "suite": "compare-product 5×5 reverse (Linux/macOS peers)",
        "windows": "HAProxy and Envoy are OS-impossible on Windows; nginx H3 inbound is OS-impossible (no QUIC).",
        "counts": counts(),
        "wires": wires,
        "special": list(SPECIAL_ABSENT),
        "absent": absent_rows(),
    }


def print_gaps() -> None:
    rows = absent_rows()
    print(f"{len(rows)} product-possible / harness-absent arms\n")
    print(f"{'suite':<16} {'product':<8} {'client':<16} {'origin':<16} arm")
    print("-" * 110)
    for r in rows:
        print(f"{r['suite']:<16} {r['product']:<8} {r['client']:<16} {r['origin']:<16} {r['arm']}")
    print()
    c = counts()
    for product in PRODUCTS:
        n = c[product]
        print(
            f"{product}: present {n['present']}  absent {n['absent']}  "
            f"impossible {n['impossible']}  (of 25)"
        )


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    p.add_argument("--json", action="store_true")
    p.add_argument("--counts", action="store_true")
    args = p.parse_args()
    if args.json:
        print(json.dumps(as_json(), indent=2))
        return
    if args.counts:
        print(json.dumps(counts(), indent=2))
        return
    print_gaps()


if __name__ == "__main__":
    main()
