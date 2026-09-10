# Titanium E2E Tests

Process-level and service-level end-to-end coverage for CLI, CLI+Plus, and Inspector.

## Categories

| Category | CI | Description |
|----------|----|-------------|
| `E2E` | Yes (Windows build job) | Spawn `titanium`, reverse/edge + Plus control plane; MITM happy-path smoke only |
| `E2E-UI` |
| `E2E-UI-Headless` | Yes (`ui-portable` + Windows `build`) | Avalonia Headless click/type by AutomationId |
| `E2E-UI-Visual` | Yes (`ui-portable` + Windows `build`) | Sparse Skia `CaptureRenderedFrame` smoke |
| `E2E-UI-Plus-Dashboard` | Yes (`ui-portable` + Windows `build`) | Playwright Chromium vs Plus HTML dashboard | Yes (`ui-portable` on Windows/Linux/macOS + dedicated `inspector-ui-macos`) | ViewModel commands, feature sanity (capture/proxy/CA/tools/composer), elevate-CA UX |
| `E2E-UI-Mac` / `E2E-UI-Linux` | Yes (macOS / Linux runners only) | System-proxy backend factory selection for that OS |
| `E2E-UI-Window` | No (opt-in) | Windows FlaUI / real HWND smoke against `TitaniumInspector.exe` |
| `E2E-Slow` | No | Chrome/Firefox + system proxy (WinINET / macOS networksetup); Firefox tests are macOS-only. Shares helpers with `tools/InspectorDesktopProbe`. |

**Happy path (all three products):** `HappyPathSanityE2ETests` — Inspector sessions in the UI collection, CLI explicit MITM + debug log file, CLI+Plus control-plane auth + MITM + debug log.

Inspector Fiddler-like flow: auto-start + system proxy (settings), Decrypt HTTPS off by default (CONNECT), CA install prompt on enable, Remove/Install root CA, Windows AppContainer loopback exemption (`AppContainerLoopback`; `TryProbeApis` covers FirewallAPI get + `ConvertStringSidToSidW`; identity `SetExemptions` re-apply is asserted not to throw — elevation may still make mutation return false).

```powershell
# PR / local fast suite (same filter as Windows CI build job)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E|TestCategory=E2E-UI"

# Only the cohesive happy-path trio
dotnet test tests/Titanium.E2E.Tests -c Release --filter "FullyQualifiedName~HappyPathSanity"

# Optional Chrome/Firefox + system proxy (mutates OS proxy; restores in finally).
# Firefox tests are macOS-only and require Firefox.app.
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-Slow"

# Optional real Inspector window (Windows)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-UI-Window"

# On-demand desktop UI + OS dialogs (not CI) — see tools/InspectorDesktopProbe/README.md
dotnet run --project tools/InspectorDesktopProbe -- all

# On-demand CLI process checklist (not CI) — see tools/CliQaProbe/README.md and tools/LOCAL-QA.md
dotnet run --project tools/CliQaProbe -- all
```

### Local Linux UI via Docker

From a Windows (or any) host with Docker:

```powershell
./tools/InspectorUiDocker/run-e2e-ui.ps1
```

```bash
./tools/InspectorUiDocker/run-e2e-ui.sh
```

Docker helper runs ViewModel `E2E-UI`. Prefer CI `E2E-UI-Headless` / `E2E-UI-Visual` for real Avalonia Headless + Skia frames. It does **not** run `E2E-UI-Window` / FlaUI.

Build `Titanium.Cli` and `Titanium.Plus` (Release or Debug) before process tests so `CliProcessHarness` can locate `titanium.dll`.
