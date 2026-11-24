#!/usr/bin/env python3
"""Classify GitHub Actions job failures as infra-only and optionally rerun them."""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

AGGREGATORS = {
    "merge-gate",
    "release-dry-run",
    "release-gate",
    "test-gate",
    "call",
}

INFRA_ANNOTATION_NEEDLES = (
    "not acquired by Runner",
    "runner has received a shutdown signal",
    "lost communication with the server",
    "hosted runner encountered an error",
    "No space left on device",
)

API_VERSION = "2022-11-28"


def write_summary(text: str) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(text if text.endswith("\n") else text + "\n")
    print(text)


def api_get(url: str, token: str) -> Any:
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": API_VERSION,
            "User-Agent": "classify-infra-failures",
        },
    )
    with urllib.request.urlopen(req) as resp:
        return json.load(resp)


def api_post(url: str, token: str) -> int:
    req = urllib.request.Request(
        url,
        data=b"",
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": API_VERSION,
            "User-Agent": "classify-infra-failures",
        },
    )
    try:
        with urllib.request.urlopen(req) as resp:
            return resp.status
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        write_summary(f"rerun POST failed: {exc.code} {body}")
        return exc.code


def steps_completed(job: dict[str, Any]) -> int:
    completed = 0
    for step in job.get("steps") or []:
        if step.get("conclusion") in ("success", "failure"):
            completed += 1
    return completed


def annotation_texts(annotations: list[dict[str, Any]]) -> list[str]:
    texts: list[str] = []
    for item in annotations:
        for key in ("message", "title", "raw_details"):
            value = item.get(key)
            if value:
                texts.append(str(value))
    return texts


def is_infra_job(job: dict[str, Any], annotations: list[dict[str, Any]]) -> bool:
    conclusion = job.get("conclusion")
    completed = steps_completed(job)
    texts = annotation_texts(annotations)
    joined = "\n".join(texts)

    if conclusion == "cancelled" and completed == 0:
        return True

    for needle in INFRA_ANNOTATION_NEEDLES:
        if needle in joined:
            return True

    if "The operation was canceled" in joined and completed == 0:
        return True

    return False


def classify(
    jobs: list[dict[str, Any]],
    annotations_by_job: dict[str, list[dict[str, Any]]],
    run_conclusion: str | None = None,
) -> dict[str, Any]:
    if run_conclusion == "cancelled":
        return {
            "rerun": False,
            "reason": "workflow run conclusion is cancelled; never auto-rerun user/concurrency cancels",
            "failed": [],
            "infra": [],
            "real": [],
        }

    failed: list[dict[str, Any]] = []
    for job in jobs:
        name = job.get("name") or ""
        if name in AGGREGATORS:
            continue
        if job.get("conclusion") in ("failure", "cancelled"):
            failed.append(job)

    infra: list[str] = []
    real: list[str] = []
    for job in failed:
        job_id = str(job.get("id") or job.get("name"))
        anns = annotations_by_job.get(job_id, [])
        label = job.get("name") or job_id
        if is_infra_job(job, anns):
            infra.append(label)
        else:
            real.append(label)

    if not failed:
        return {
            "rerun": False,
            "reason": "no non-aggregator failed jobs",
            "failed": [],
            "infra": [],
            "real": [],
        }
    if real:
        return {
            "rerun": False,
            "reason": "real failures present: " + ", ".join(real),
            "failed": [j.get("name") for j in failed],
            "infra": infra,
            "real": real,
        }
    return {
        "rerun": True,
        "reason": "infra-only failures: " + ", ".join(infra),
        "failed": [j.get("name") for j in failed],
        "infra": infra,
        "real": [],
    }


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def self_test(fixtures_dir: Path) -> None:
    cases = {
        "infra-only.json": True,
        "mixed.json": False,
        "real-failure.json": False,
        "user-cancelled.json": False,
    }
    for name, expect_rerun in cases.items():
        data = load_json(fixtures_dir / name)
        result = classify(
            data["jobs"],
            {str(k): v for k, v in data.get("annotations", {}).items()},
            data.get("run_conclusion"),
        )
        if result["rerun"] != expect_rerun:
            raise SystemExit(
                f"{name}: expected rerun={expect_rerun} got {result['rerun']} ({result['reason']})"
            )
        print(f"ok {name}: {result['reason']}")
    print("classify-infra-failures self-test ok")


def live(repo: str, run_id: str, do_rerun: bool) -> int:
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not token:
        raise SystemExit("GH_TOKEN or GITHUB_TOKEN required")

    run = api_get(f"https://api.github.com/repos/{repo}/actions/runs/{run_id}", token)
    if run.get("conclusion") == "cancelled":
        decision = classify([], {}, "cancelled")
        write_summary(f"### ci-infra-retry\n{decision['reason']}")
        return 0

    jobs_payload = api_get(
        f"https://api.github.com/repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100",
        token,
    )
    jobs = jobs_payload.get("jobs") or []
    annotations_by_job: dict[str, list[dict[str, Any]]] = {}
    for job in jobs:
        if job.get("conclusion") not in ("failure", "cancelled"):
            continue
        job_id = job.get("id")
        if not job_id:
            continue
        try:
            anns = api_get(
                f"https://api.github.com/repos/{repo}/check-runs/{job_id}/annotations",
                token,
            )
            if isinstance(anns, list):
                annotations_by_job[str(job_id)] = anns
        except urllib.error.HTTPError:
            annotations_by_job[str(job_id)] = []

    decision = classify(jobs, annotations_by_job, run.get("conclusion"))
    write_summary(
        "### ci-infra-retry\n"
        f"- rerun: `{decision['rerun']}`\n"
        f"- reason: {decision['reason']}\n"
        f"- infra: {', '.join(decision['infra']) or '(none)'}\n"
        f"- real: {', '.join(decision['real']) or '(none)'}\n"
    )
    if decision["rerun"] and do_rerun:
        status = api_post(
            f"https://api.github.com/repos/{repo}/actions/runs/{run_id}/rerun-failed-jobs",
            token,
        )
        write_summary(f"- rerun-failed-jobs HTTP {status}")
        return 0 if status in (201, 204) else 1
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo")
    parser.add_argument("--run")
    parser.add_argument("--rerun", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--fixtures-dir", default="tools/ci/testdata/infra-failures")
    args = parser.parse_args()

    if args.self_test:
        self_test(Path(args.fixtures_dir))
        return 0
    if not args.repo or not args.run:
        parser.error("--repo and --run are required unless --self-test")
    return live(args.repo, args.run, args.rerun)


if __name__ == "__main__":
    raise SystemExit(main())
