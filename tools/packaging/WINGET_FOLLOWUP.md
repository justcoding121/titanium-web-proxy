# Winget follow-up

Status (2026-09-02): `winget search justcoding121.TitaniumCli` / Inspector — **not in catalog**. Prior `microsoft/winget-pkgs` PRs for unsigned `7.0.4` were closed by the MS bot and never landed on master.

## Do not

- Resubmit another unsigned `7.0.4` with the same installer SHA256s.

## After first signed stable (`7.0.5+` recommended if binaries change)

1. Merge repo stub so `twp` → `twp.exe` ([`TitaniumCli.yaml`](winget/TitaniumCli.yaml)).
2. Compute new SHA256 for `Titanium.Cli-win-x64.zip` and `TitaniumInspector-win-x64.msi` (+ zip if submitted).
3. Open fresh PRs against `microsoft/winget-pkgs` using the stubs under [`winget/`](winget/).
4. Note Authenticode publisher **Jehonathan Thomas** in PR description if SmartScreen/winget validation asks.
