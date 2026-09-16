# Titanium E2E Tests

Process-level and service-level end-to-end coverage for CLI, CLI+Plus, and Inspector.

## Categories

| Category | CI | Description |
|----------|----|-------------|
| `E2E` | Yes (`cli-e2e` matrix: Windows / Linux / macOS) | Spawn `titanium`, full CLI command tree, OS services, Plus control plane + features |
| `E2E-UI` | Yes (`ui-portable` + Windows `build`) | ViewModel commands, feature sanity |
| `E2E-UI-Headless` | Yes (`ui-portable` + Windows `build`) | Avalonia Headless click/type by AutomationId |
| `E2E-UI-Visual` | Yes (`ui-portable` + Windows `build`) | Sparse Skia `CaptureRenderedFrame` smoke |
| `E2E-UI-Plus-Dashboard` | Yes (`ui-portable` + Windows `build`) | Playwright Chromium vs Plus HTML dashboard (in-process + CLI-hosted) |
| `E2E-UI-Mac` / `E2E-UI-Linux` | Yes (macOS / Linux runners only) | System-proxy backend factory selection for that OS |
| `E2E-UI-Window` | No (opt-in) | Windows FlaUI / real HWND smoke against `TitaniumInspector.exe` |
| `E2E-Slow` | No | Chrome/Firefox + system proxy (WinINET / macOS networksetup); Firefox tests are macOS-only |

**CLI command tree + Plus:** `CliHelpMatrixE2ETests`, `CliMetaAndUpdateE2ETests`, `CliHttp3DepsE2ETests`, `CliRunDialectsE2ETests`, `CliServiceLifecycleE2ETests`, `CliCommandE2ETests`, `CliPlusE2ETests`, `CliPlusControlPlaneE2ETests`, `CliPlusFeaturesE2ETests`, `CliHostedPlusDashboardPlaywrightTests`.

**Happy path (all three products):** `HappyPathSanityE2ETests` — Inspector sessions in the UI collection, CLI explicit MITM + debug log file, CLI+Plus control-plane auth + MITM + debug log.

```powershell
# CLI / Plus process E2E (same as CI cli-e2e)
dotnet build src/Titanium.Cli -c Release
dotnet build src/Titanium.Plus -c Release
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E"

# Inspector ViewModel / UI
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-UI"

# Only the cohesive happy-path trio
dotnet test tests/Titanium.E2E.Tests -c Release --filter "FullyQualifiedName~HappyPathSanity"

# Optional Chrome/Firefox + system proxy (mutates OS proxy; restores in finally).
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-Slow"

# Optional real Inspector window (Windows)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-UI-Window"
```

Build `Titanium.Cli` and `Titanium.Plus` (Release or Debug) before process tests so `CliProcessHarness` can locate `titanium.dll` / the apphost.
