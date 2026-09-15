# Winget follow-up

## Submitted winget PRs (v7.0.6) ù first listing

- CLI: https://github.com/microsoft/winget-pkgs/pull/432942
- Inspector (Authenticode MSI): https://github.com/microsoft/winget-pkgs/pull/432943

## Closed (v7.0.5 ù superseded before first moderator merge)

- CLI: https://github.com/microsoft/winget-pkgs/pull/428410
- Inspector: https://github.com/microsoft/winget-pkgs/pull/428421

Closed in favor of 7.0.6 as the initial New-Package listing (neither version had been approved yet).

## Do not

- Resubmit beta tags to `microsoft/winget-pkgs`.
- Open duplicate New-Package PRs for older versions while the first listing is still under review.
- Retarget or bump the open 7.0.6 winget PRs for 7.0.9-beta / 7.0.10-beta / 7.0.10 while first-listing moderation is in progress.

## 2026-09-15: hold winget for 7.0.10

Leave [CLI #432942](https://github.com/microsoft/winget-pkgs/pull/432942) and [Inspector #432943](https://github.com/microsoft/winget-pkgs/pull/432943) alone until they clear moderator review. Do not open winget PRs for 7.0.9-beta, 7.0.10-beta, or 7.0.10 GA in this window.

## After merge

1. Verify `winget search Titanium` / `winget show justcoding121.TitaniumCli` and Inspector.
2. Note Authenticode publisher **Jehonathan Thomas** when relevant.
