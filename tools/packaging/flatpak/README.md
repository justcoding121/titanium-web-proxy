# Flathub packaging (Titanium Inspector + CLI)

First listing is a **human Flathub review**. Later stables should be version / URL / SHA bumps only.

## App IDs

| App | Flatpak ID | Manifest |
| --- | --- | --- |
| Inspector | `io.github.justcoding121.TitaniumInspector` | [`io.github.justcoding121.TitaniumInspector.yml`](io.github.justcoding121.TitaniumInspector.yml) |
| CLI | `io.github.justcoding121.TitaniumCli` | [`io.github.justcoding121.TitaniumCli.yml`](io.github.justcoding121.TitaniumCli.yml) |

## Formal Flathub submissions (v7.0.5)

- Inspector: https://github.com/flathub/flathub/pull/10049
- CLI: https://github.com/flathub/flathub/pull/10050

## Beta dry-run (before stable Flathub submit)

1. Point manifests at a polished **beta** linux-x64 zip + SHA256 (temporary).
2. Run [`.github/workflows/packaging-catalog-smoke.yml`](../../.github/workflows/packaging-catalog-smoke.yml) (`run_flatpak=true`) or locally:

```shell
flatpak-builder --user --force-clean /tmp/ti-build \
  tools/packaging/flatpak/io.github.justcoding121.TitaniumInspector.yml
```

3. Do **not** request formal Flathub merge while URLs still point at a `-beta` tag.

## First / formal submission checklist

1. Cut a **signed/stable** GitHub Release that includes `TitaniumInspector-linux-x64.zip` and `Titanium.Cli-linux-x64.zip`.
2. Retarget `url:` + `sha256:` in both manifests from beta → stable. Prefer AppImage source later if Flathub reviewers prefer a single file; zip is fine for first listing.
3. Create Flathub account + two new app repos (or one PR per app) under [flathub/flathub](https://github.com/flathub/flathub) following [App Submission](https://docs.flathub.org/docs/for-app-authors/submission).
4. Copy this directory’s manifests, `.desktop`, and `.metainfo.xml` into each Flathub app repo.
5. Respond to reviewer sandbox / metainfo feedback (network + display sockets are already declared for Inspector).

## Later versions (`flathub-updates`)

Manifests include `x-checker-data` so [flathub-external-data-checker](https://github.com/flathub/flatpak-external-data-checker) can open PRs when GitHub Releases change.

After each stable cut you can also bump manually:

```shell
# Example: recompute sha of the linux-x64 zip used by the manifest
sha256sum TitaniumInspector-linux-x64.zip
# Edit url tag + sha256 in the Flathub app repo; open PR
```

Optional CI in this monorepo: add a workflow that opens a PR against the Flathub app repos with updated URL/SHA — keep secrets for a bot token with access to those forks.

## Local build smoke

```shell
flatpak-builder --user --install --force-clean /tmp/ti-build \
  tools/packaging/flatpak/io.github.justcoding121.TitaniumInspector.yml
flatpak run io.github.justcoding121.TitaniumInspector
```
