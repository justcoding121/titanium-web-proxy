# InspectorDesktopProbe

On-demand desktop UX + OS proxy/CA validation for Titanium Inspector. **Not CI.** Mutates system proxy and may show OS cert dialogs.

## Prerequisites

- Built Inspector dependencies (`dotnet build` restores Avalonia)
- Browsers as needed: Edge (Windows), Chrome, Firefox
- Interactive session (Windows Trusted Root **Yes/No**, macOS Keychain password still need a human click once)
- Avalonia confirm dialogs are auto-accepted by the probe

## Commands

```powershell
dotnet run --project tools/InspectorDesktopProbe -- status
dotnet run --project tools/InspectorDesktopProbe -- proxy --browser auto --timeout-sec 45
dotnet run --project tools/InspectorDesktopProbe -- cert
dotnet run --project tools/InspectorDesktopProbe -- firefox
dotnet run --project tools/InspectorDesktopProbe -- loopback      # Windows Store apps
dotnet run --project tools/InspectorDesktopProbe -- exclusions
dotnet run --project tools/InspectorDesktopProbe -- pac
dotnet run --project tools/InspectorDesktopProbe -- all
```

| Command | Validates |
|---------|-----------|
| `status` | OS proxy dump, trust suppress flag, optional `--ui` harness |
| `proxy` | System proxy checkbox → WinINET/gsettings/scutil → browser HTTPS **without** `--proxy-server` |
| `cert` | Install/Remove CA menus; **Decrypt HTTPS auto-off** after remove |
| `firefox` | Trust CA in Firefox + system-proxy capture |
| `loopback` | Allow Store apps dialog (Win8+) |
| `exclusions` | Excluded hosts + Proxy localhost |
| `pac` | PAC replace confirm cancel/accept when PAC is active |
| `all` | Applicable scenarios for this OS |

## Logs (MCP-friendly)

- `tools/InspectorDesktopProbe/results/*.log`
- `tools/InspectorDesktopProbe/results/last-run.json` — structured step pass/fail

## vs unit tests

Unit/integration suites set `CertificateManager.SuppressInteractiveRootStoreMutations` and `TITANIUM_SKIP_ROOT_STORE_UI=1` so they **never** open CryptUI / Keychain / polkit. This probe clears suppress and exercises the real Inspector window + OS.

Related local suites: `dotnet test --filter TestCategory=E2E-Slow` (service-level, no desktop window).
