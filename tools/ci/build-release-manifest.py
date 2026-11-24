#!/usr/bin/env python3
"""Build dist/release-manifest.json from staged release artifacts."""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import pathlib
import sys


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    digest.update(path.read_bytes())
    return digest.hexdigest()


def build_manifest(
    dist: pathlib.Path,
    tag: str,
    version: str,
    github_repository: str,
) -> dict:
    channel = "beta" if ("-" in version or "beta" in version.lower()) else "stable"
    assets: dict[str, dict] = {}
    for p in dist.iterdir():
        if p.is_file():
            assets[p.name] = {"path": str(p), "sha256": sha256_file(p)}

    manifest: dict = {
        "version": version,
        "channel": channel,
        "releasedUtc": datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
        "abstractionsVersion": version.split("-")[0],
        "products": {
            "cli": {"assets": {}},
            "plus": {"version": version, "requiredAbstractions": "8.0.0", "asset": None},
            "inspector": {"assets": {}},
        },
        "nuget": {
            "Titanium.Web.Proxy": version.split("-")[0],
            "Titanium.Web.Proxy.Abstractions": version.split("-")[0],
            "Titanium.Web.Proxy.Configuration": version.split("-")[0],
        },
    }

    def put(product: str, key: str, entry: dict) -> None:
        manifest["products"][product]["assets"][key] = entry

    for name, meta in assets.items():
        url = f"https://github.com/{github_repository}/releases/download/{tag}/{name}"
        entry = {"url": url, "sha256": meta["sha256"]}
        if name.startswith("Titanium.Cli-"):
            stem = name.removeprefix("Titanium.Cli-")
            for ext in (".zip", ".AppImage", ".deb", ".rpm"):
                if stem.endswith(ext):
                    rid = stem[: -len(ext)]
                    key = rid if ext == ".zip" else f"{rid}{ext}"
                    put("cli", key, entry)
                    break
        elif name.startswith("TitaniumInspector-"):
            stem = name.removeprefix("TitaniumInspector-")
            if stem.endswith(".msi"):
                put("inspector", "win-x64-msi", entry)
            else:
                for ext in (".zip", ".AppImage", ".deb", ".rpm", ".dmg"):
                    if stem.endswith(ext):
                        rid = stem[: -len(ext)]
                        key = rid if ext == ".zip" else f"{rid}{ext}"
                        put("inspector", key, entry)
                        break
        elif name.startswith("Titanium.Plus") and name.endswith(".zip"):
            bundle = dict(entry)
            bundle["size"] = pathlib.Path(meta["path"]).stat().st_size
            manifest["products"]["plus"]["bundle"] = bundle
            manifest["products"]["plus"]["version"] = version
        elif name.startswith("Titanium.Plus") and name.endswith(".dll"):
            manifest["products"]["plus"]["asset"] = entry
            manifest["products"]["plus"]["version"] = version

    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", default="dist", help="Directory of release files")
    parser.add_argument(
        "--output",
        default="",
        help="Manifest output path (default: <dist>/release-manifest.json)",
    )
    parser.add_argument("--tag", default=os.environ.get("RELEASE_TAG", ""))
    parser.add_argument("--version", default=os.environ.get("VERSION", ""))
    parser.add_argument(
        "--github-repository",
        default=os.environ.get("GITHUB_REPOSITORY", "justcoding121/titanium-web-proxy"),
    )
    args = parser.parse_args()

    if not args.tag:
        parser.error("--tag or RELEASE_TAG is required")
    if not args.version:
        parser.error("--version or VERSION is required")

    dist = pathlib.Path(args.dist)
    if not dist.is_dir():
        parser.error(f"dist directory missing: {dist}")

    out = pathlib.Path(args.output) if args.output else dist / "release-manifest.json"
    manifest = build_manifest(dist, args.tag, args.version, args.github_repository)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())