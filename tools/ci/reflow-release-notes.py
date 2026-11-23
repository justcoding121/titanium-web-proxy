#!/usr/bin/env python3
"""Put a flattened What's Changed section back onto separate lines."""
import pathlib
import re
import subprocess
import sys


def main() -> None:
    tag = sys.argv[1]
    body = subprocess.check_output(
        ["gh", "release", "view", tag, "--json", "body", "-q", ".body"],
        text=True,
    )
    marker = "## What's Changed"
    idx = body.find(marker)
    if idx < 0:
        return
    head, tail = body[:idx], body[idx:]
    if "\n* " in tail or "\n- " in tail:
        return
    fixed = re.sub(r"[ \t]+\*[ \t]+", "\n* ", tail)
    fixed = re.sub(r"## What's Changed\n\*", "## What's Changed\n\n*", fixed, count=1)
    fixed = re.sub(r"[ \t]+\*\*Full Changelog\*\*", "\n\n**Full Changelog**", fixed)
    if not fixed.endswith("\n"):
        fixed += "\n"
    new = head.rstrip() + "\n\n" + fixed
    path = pathlib.Path("/tmp/release-notes.md")
    path.write_text(new, encoding="utf-8")
    subprocess.check_call(["gh", "release", "edit", tag, "--notes-file", str(path)])
    print("reflowed What's Changed onto separate lines")


if __name__ == "__main__":
    main()
