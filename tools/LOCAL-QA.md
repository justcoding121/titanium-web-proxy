# Local QA recipe (solo / per-OS)

Run this on each OS you care about after a Release build of CLI (and Plus if you want `run-plus`).

```powershell
# Automated suites (PR CI categories)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E|TestCategory=E2E-UI|TestCategory=E2E-UI-Headless"

# CLI process checklist (nested help, dialects, live run, unelevated service messages)
dotnet run --project tools/CliQaProbe -- all

# Optional: live OS service install/start/stop/uninstall as name titanium-qa-probe.
# Prefer the login user (passwordless sudo for machine units; systemd --user as that user):
dotnet run --project tools/CliQaProbe -- all --elevated
# Also OK: sudo -E dotnet run --project tools/CliQaProbe -- all --elevated
# (probe drops to SUDO_USER for --user; bare root without SUDO_USER skips --user)

# Inspector System proxy / CA / browser / loopback UX (desktop dialogs; not CI)
dotnet run --project tools/InspectorDesktopProbe -- all
```

## Probes

| Probe | Purpose |
|-------|---------|
| [CliQaProbe](CliQaProbe/README.md) | Spawn `titanium` / `titanium.exe`; operator CLI surface including `service` |
| [InspectorDesktopProbe](InspectorDesktopProbe/README.md) | In-process Avalonia; full UI chrome + System proxy, root CA, browsers, Store loopback |

Results: `tools/CliQaProbe/results/last-run.json`, `tools/InspectorDesktopProbe/results/last-run.json`.

## Notes

- CliQaProbe never mutates the default service name `titanium`; elevated runs use `titanium-qa-probe` and uninstall in `finally`.
- `titanium update` and `http3-deps install` are omitted (network / machine mutation).
- InspectorDesktopProbe `all` starts with `chrome` (full menu / context / Delete-key / Options / Tools click-through), then OS proxy/CA scenarios.
