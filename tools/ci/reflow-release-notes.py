#!/usr/bin/env python3
"""Reflow a flattened What's Changed section and prepend release highlights."""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys


def reflow_whats_changed(body: str) -> str:
    marker = "## What's Changed"
    idx = body.find(marker)
    if idx < 0:
        return body
    head, tail = body[:idx], body[idx:]
    if "\n* " in tail or "\n- " in tail:
        return body
    fixed = re.sub(r"[ \t]+\*[ \t]+", "\n* ", tail)
    fixed = re.sub(r"## What's Changed\n\*", "## What's Changed\n\n*", fixed, count=1)
    fixed = re.sub(r"[ \t]+\*\*Full Changelog\*\*", "\n\n**Full Changelog**", fixed)
    if not fixed.endswith("\n"):
        fixed += "\n"
    return head.rstrip() + "\n\n" + fixed


def prepend_highlights(body: str, highlights: str) -> str:
    text = highlights.strip()
    if not text:
        return body
    if not text.endswith("\n"):
        text += "\n"
    body = body.lstrip("\n")
    if body.startswith("## Highlights"):
        return body
    return text + "\n" + body


def apply_notes(body: str, highlights_path: pathlib.Path | None) -> str:
    new = reflow_whats_changed(body)
    if highlights_path is not None and highlights_path.is_file():
        new = prepend_highlights(new, highlights_path.read_text(encoding="utf-8"))
    return new


def self_test() -> None:
    root = pathlib.Path(__file__).resolve().parents[1]
    fixture = root / "ci" / "testdata" / "release-notes-flattened.md"
    highlights = root / "ci" / "testdata" / "release-highlights-sample.md"
    body = fixture.read_text(encoding="utf-8")
    out = apply_notes(body, highlights)
    assert out.startswith("## Highlights\n"), out[:80]
    assert out.count("\n* ") >= 10, out
    assert "\n\n**Full Changelog**" in out, out
    assert out.index("## Highlights") < out.index("## What's Changed")
    bullets = [line for line in out.splitlines() if line.startswith("* ")]
    assert len(bullets) >= 10, len(bullets)
    print("reflow-release-notes self-test ok")


def main() -> None:
    if len(sys.argv) >= 2 and sys.argv[1] == "--self-test":
        self_test()
        return

    tag = sys.argv[1]
    version = tag[1:] if tag.startswith("v") else tag
    body = subprocess.check_output(
        ["gh", "release", "view", tag, "--json", "body", "-q", ".body"],
        text=True,
    )
    root = pathlib.Path(__file__).resolve().parents[1]
    highlights = root / "packaging" / "release-highlights" / f"{version}.md"
    if not highlights.is_file():
        print(f"warn: no highlights file at {highlights}", file=sys.stderr)
        highlights = None
    new = apply_notes(body, highlights)
    if new == body:
        print("release notes already well formed")
        return
    path = pathlib.Path("/tmp/release-notes.md")
    path.write_text(new, encoding="utf-8")
    subprocess.check_call(["gh", "release", "edit", tag, "--notes-file", str(path)])
    print("updated release notes (reflow and/or highlights)")


if __name__ == "__main__":
    main()
