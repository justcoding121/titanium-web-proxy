"""Shared helpers for the RpsLoadProbe analysis scripts (stdlib only, no CSV schema change)."""
from __future__ import annotations

import csv
import math
import os
import statistics
from pathlib import Path

# Two-sided 95% Student t critical values by degrees of freedom.
_T95 = {
    1: 12.706, 2: 4.303, 3: 3.182, 4: 2.776, 5: 2.571, 6: 2.447, 7: 2.365, 8: 2.306, 9: 2.262,
    10: 2.228, 11: 2.201, 12: 2.179, 13: 2.160, 14: 2.145, 15: 2.131, 16: 2.120, 17: 2.110,
    18: 2.101, 19: 2.093, 20: 2.086, 25: 2.060, 30: 2.042, 40: 2.021, 60: 2.000,
}


def t95(df: int) -> float:
    if df < 1:
        return float("nan")
    if df in _T95:
        return _T95[df]
    keys = sorted(_T95)
    for k in keys:
        if k >= df:
            return _T95[k]
    return 1.96


def mean_ci95(values: list[float]) -> tuple[float, float, float]:
    """Return (mean, ci_low, ci_high); CI is NaN for n < 2."""
    n = len(values)
    if n == 0:
        return (float("nan"),) * 3
    m = statistics.fmean(values)
    if n < 2:
        return m, float("nan"), float("nan")
    sd = statistics.stdev(values)
    half = t95(n - 1) * sd / math.sqrt(n)
    return m, m - half, m + half


def _f(text: str) -> float | None:
    text = (text or "").strip()
    if not text:
        return None
    try:
        return float(text)
    except ValueError:
        return None


def load_rows(path: str | Path) -> list[dict]:
    rows = []
    with open(path, newline="", encoding="utf-8") as fh:
        for r in csv.DictReader(fh):
            rows.append({
                "arm": r["arm"],
                "concurrency": int(float(r["concurrency"])),
                "ok": int(float(r["ok"])),
                "errors": int(float(r["errors"])),
                "rps": _f(r["rps"]) or 0.0,
                "p50_ms": _f(r["p50_ms"]),
                "p99_ms": _f(r["p99_ms"]),
                "meets_slo": r.get("meets_slo") == "1",
                "rss_bytes": _f(r.get("proxy_rss_peak_bytes", "")),
                "cpu_pct": _f(r.get("proxy_cpu_avg_pct", "")),
                "source": str(path),
            })
    return rows


def cpu_us_per_request(cpu_pct: float | None, rps: float, cpus: int) -> float | None:
    """CPU-microseconds per request: proxy_cpu_avg_pct is normalised to the whole machine
    (100 = every logical CPU busy), so busy cores = pct * cpus / 100 and cost = busy cores / rps."""
    if cpu_pct is None or rps <= 0:
        return None
    return cpu_pct * cpus / 100.0 / rps * 1e6


def default_cpus() -> int:
    return os.cpu_count() or 1


def peak_row(rows: list[dict], concurrency: int | None = None) -> dict | None:
    pool = [r for r in rows if concurrency is None or r["concurrency"] == concurrency]
    return max(pool, key=lambda r: r["rps"]) if pool else None
