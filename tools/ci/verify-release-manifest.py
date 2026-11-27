#!/usr/bin/env python3
"""Verify release-manifest.json against SHA256SUMS and/or staged artifacts."""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import sys
import tempfile

RIDS = (
    "win-x64",
    "linux-x64",
    "linux-arm64",
    "linux-musl-x64",
    "linux-musl-arm64",
    "osx-x64",
    "osx-arm64",
)

LINUX_GLIBC_RIDS = frozenset({"linux-x64", "linux-arm64"})
MAC_RIDS = frozenset({"osx-x64", "osx-arm64"})


class ManifestError(Exception):
    pass


def cli_keys_for_rid(rid: str) -> list[str]:
    keys = [rid]
    if rid in LINUX_GLIBC_RIDS:
        keys.extend([f"{rid}.AppImage", f"{rid}.deb", f"{rid}.rpm"])
    return keys


def inspector_keys_for_rid(rid: str) -> list[str]:
    """Mirror UpdateService.ResolveAsset manifest key layout."""
    if rid == "win-x64":
        return ["win-x64-msi", "win-x64"]
    if rid in LINUX_GLIBC_RIDS:
        return [rid, f"{rid}.AppImage", f"{rid}.deb", f"{rid}.rpm"]
    if rid in MAC_RIDS:
        return [rid, f"{rid}.dmg"]
    return [rid]


def expected_filename(product: str, key: str) -> str:
    prefix = "Titanium.Cli-" if product == "cli" else "TitaniumInspector-"
    if product == "inspector" and key == "win-x64-msi":
        return f"{prefix}win-x64.msi"
    if key.endswith((".AppImage", ".deb", ".rpm", ".dmg")):
        return f"{prefix}{key}"
    return f"{prefix}{key}.zip"


def parse_sha256sums(path: pathlib.Path) -> tuple[set[str], dict[str, str]]:
    hashes: set[str] = set()
    by_name: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        match = re.match(r"^([0-9a-fA-F]{64})\s+\*?(.*)$", line)
        if not match:
            continue
        digest, name = match.group(1).lower(), match.group(2)
        hashes.add(digest)
        by_name[name] = digest
    return hashes, by_name


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    digest.update(path.read_bytes())
    return digest.hexdigest()


def basename_from_url(url: str) -> str:
    return url.rstrip("/").rsplit("/", 1)[-1]


def iter_manifest_assets(manifest: dict) -> list[tuple[str, str, dict]]:
    out: list[tuple[str, str, dict]] = []
    products = manifest.get("products") or {}
    for product in ("cli", "inspector"):
        assets = (products.get(product) or {}).get("assets") or {}
        for key, entry in assets.items():
            out.append((product, key, entry))
    plus = products.get("plus") or {}
    for slot in ("bundle", "asset"):
        entry = plus.get(slot)
        if entry and isinstance(entry, dict):
            out.append(("plus", slot, entry))
    return out


def assert_required_keys(manifest: dict) -> None:
    products = manifest.get("products") or {}
    for rid in RIDS:
        cli_assets = (products.get("cli") or {}).get("assets") or {}
        for key in cli_keys_for_rid(rid):
            if key not in cli_assets:
                raise ManifestError(f"missing products.cli.assets[{key!r}]")
            entry = cli_assets[key]
            if not entry.get("url") or not entry.get("sha256"):
                raise ManifestError(f"products.cli.assets[{key!r}] needs url and sha256")

        inspector_assets = (products.get("inspector") or {}).get("assets") or {}
        for key in inspector_keys_for_rid(rid):
            if key not in inspector_assets:
                raise ManifestError(f"missing products.inspector.assets[{key!r}]")
            entry = inspector_assets[key]
            if not entry.get("url") or not entry.get("sha256"):
                raise ManifestError(f"products.inspector.assets[{key!r}] needs url and sha256")

    plus = products.get("plus") or {}
    bundle = plus.get("bundle")
    asset = plus.get("asset")
    if not bundle and not asset:
        raise ManifestError("products.plus needs bundle or asset")
    picked = bundle if bundle else asset
    if not picked.get("url") or not picked.get("sha256"):
        raise ManifestError("products.plus bundle/asset needs url and sha256")


def verify_asset_hashes(
    manifest: dict,
    *,
    sums_path: pathlib.Path | None,
    stage_dir: pathlib.Path | None,
    tag: str | None,
) -> None:
    sum_hashes: set[str] = set()
    sum_by_name: dict[str, str] = {}
    if sums_path:
        if not sums_path.is_file():
            raise ManifestError(f"SHA256SUMS missing: {sums_path}")
        sum_hashes, sum_by_name = parse_sha256sums(sums_path)

    for product, key, entry in iter_manifest_assets(manifest):
        expected = str(entry.get("sha256", "")).lower()
        if len(expected) != 64:
            raise ManifestError(f"{product} {key}: invalid sha256")

        url = str(entry.get("url", ""))
        if tag and tag not in url:
            raise ManifestError(f"{product} {key}: url does not contain tag {tag!r}")

        name = basename_from_url(url) if url else ""
        alt = expected_filename(product, key) if product in ("cli", "inspector") else name
        if product in ("cli", "inspector") and not name:
            name = alt

        staged: pathlib.Path | None = None
        if stage_dir:
            candidates: list[str] = []
            for candidate in (name, alt):
                if candidate and candidate not in candidates:
                    candidates.append(candidate)
            for candidate in candidates:
                path = stage_dir / candidate
                if path.is_file():
                    staged = path
                    break

        verified = False
        if staged is not None:
            actual = sha256_file(staged)
            if actual != expected:
                raise ManifestError(
                    f"{product} {key}: staged {staged.name} sha256 {actual} != manifest {expected}"
                )
            size_key = entry.get("size")
            if size_key is not None:
                actual_size = staged.stat().st_size
                if int(size_key) != actual_size:
                    raise ManifestError(
                        f"{product} {key}: size {size_key} != staged {actual_size}"
                    )
            verified = True

        if not verified and sum_hashes:
            if expected in sum_hashes:
                verified = True
            elif name and sum_by_name.get(name) == expected:
                verified = True

        if not verified:
            raise ManifestError(
                f"{product} {key}: sha256 not in SHA256SUMS and no matching staged file"
            )


def verify(
    manifest_path: pathlib.Path,
    *,
    sums_path: pathlib.Path | None,
    stage_dir: pathlib.Path | None,
    tag: str | None,
) -> None:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert_required_keys(manifest)
    verify_asset_hashes(manifest, sums_path=sums_path, stage_dir=stage_dir, tag=tag)


def self_test() -> None:
    root = pathlib.Path(tempfile.mkdtemp(prefix="twp-manifest-verify-"))
    try:
        tag = "v99.0.0-test"
        repo = "example/titanium-web-proxy"
        dist = root / "dist"
        dist.mkdir()

        def touch(name: str, content: bytes = b"x") -> str:
            path = dist / name
            path.write_bytes(content)
            return hashlib.sha256(content).hexdigest()

        # Minimal artifact set for all manifest keys.
        files: list[tuple[str, str]] = []
        for rid in RIDS:
            h = touch(f"Titanium.Cli-{rid}.zip")
            files.append((f"Titanium.Cli-{rid}.zip", h))
            if rid in LINUX_GLIBC_RIDS:
                for ext in (".AppImage", ".deb", ".rpm"):
                    name = f"Titanium.Cli-{rid}{ext}"
                    files.append((name, touch(name)))
        for rid in RIDS:
            h = touch(f"TitaniumInspector-{rid}.zip")
            files.append((f"TitaniumInspector-{rid}.zip", h))
            if rid == "win-x64":
                files.append(("TitaniumInspector-win-x64.msi", touch("TitaniumInspector-win-x64.msi")))
            if rid in LINUX_GLIBC_RIDS:
                for ext in (".AppImage", ".deb", ".rpm"):
                    name = f"TitaniumInspector-{rid}{ext}"
                    files.append((name, touch(name)))
            if rid in MAC_RIDS:
                name = f"TitaniumInspector-{rid}.dmg"
                files.append((name, touch(name)))

        touch("Titanium.Plus-99.0.0.zip", b"plus-bundle")

        import importlib.util

        build_path = pathlib.Path(__file__).with_name("build-release-manifest.py")
        spec = importlib.util.spec_from_file_location("build_release_manifest", build_path)
        assert spec and spec.loader
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)

        manifest = mod.build_manifest(dist, tag, "99.0.0-test", repo)
        manifest_path = dist / "release-manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

        sums_lines = [f"{h}  {n}" for n, h in files]
        sums_path = dist / "SHA256SUMS"
        sums_path.write_text("\n".join(sums_lines) + "\n", encoding="utf-8")

        verify(manifest_path, sums_path=sums_path, stage_dir=dist, tag=tag)

        bad = json.loads(manifest_path.read_text(encoding="utf-8"))
        del bad["products"]["cli"]["assets"]["win-x64"]
        bad_path = dist / "bad-manifest.json"
        bad_path.write_text(json.dumps(bad), encoding="utf-8")
        try:
            verify(bad_path, sums_path=sums_path, stage_dir=dist, tag=tag)
        except ManifestError:
            pass
        else:
            raise SystemExit("self-test: expected missing cli asset to fail")

        print("verify-release-manifest self-test ok")
    finally:
        import shutil

        shutil.rmtree(root, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=pathlib.Path)
    parser.add_argument("--sums", type=pathlib.Path, default=None)
    parser.add_argument("--stage-dir", type=pathlib.Path, default=None)
    parser.add_argument("--tag", default=None)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return 0

    if not args.manifest:
        parser.error("--manifest is required unless --self-test")
    if not args.sums and not args.stage_dir:
        parser.error("provide --sums and/or --stage-dir")

    try:
        verify(args.manifest, sums_path=args.sums, stage_dir=args.stage_dir, tag=args.tag)
    except ManifestError as exc:
        print(f"verify failed: {exc}", file=sys.stderr)
        return 1
    print("release-manifest ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())