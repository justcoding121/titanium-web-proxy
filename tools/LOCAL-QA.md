# Local QA recipe (solo / per-OS)

CLI and CLI Plus command-tree / control-plane process E2E runs in CI on **Windows, Linux, and macOS** (`cli-e2e` job, `TestCategory=E2E`). You do not need CliQaProbe for day-to-day command coverage.

```powershell
# Same filter as CI cli-e2e (after Release build of CLI + Plus)
dotnet build src/Titanium.Cli -c Release
dotnet build src/Titanium.Plus -c Release
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E"

# Inspector / UI categories (PR CI ui-portable + Windows build E2E-UI)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-UI|TestCategory=E2E-UI-Headless"

# Optional: maintainer probe (apphost checklist; overlaps CI E2E)
dotnet run --project tools/CliQaProbe -- all
dotnet run --project tools/CliQaProbe -- all --elevated

# Inspector System proxy / CA / browser / loopback UX (desktop dialogs; not CI)
dotnet run --project tools/InspectorDesktopProbe -- all
```

## Probes

| Probe | Purpose |
|-------|---------|
| [CliQaProbe](CliQaProbe/README.md) | Optional manual spawn of `titanium` apphost; CI `cli-e2e` is the source of truth |
| [InspectorDesktopProbe](InspectorDesktopProbe/README.md) | In-process Avalonia; full UI chrome + System proxy, root CA, browsers, Store loopback |

Results: `tools/CliQaProbe/results/last-run.json`, `tools/InspectorDesktopProbe/results/last-run.json`.

## Notes

- CliQaProbe never mutates the default service name `titanium`; elevated runs use `titanium-qa-probe` and uninstall in `finally`. E2E service tests use unique `titanium-e2e-*` names.
- Live production update.titaniumproxy.com and real Redis/Consul/K8s clusters are not required — E2E uses `TITANIUM_UPDATE_FEED` fakes and loopback stubs.
- `http3-deps install` is covered in E2E (Windows fail / already-supported / Unix package when Quic is false).
