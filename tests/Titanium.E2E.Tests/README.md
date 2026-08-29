# Titanium E2E Tests

Process-level and service-level end-to-end coverage for CLI, CLI+Plus, and Inspector.

## Categories

| Category | CI | Description |
|----------|----|-------------|
| `E2E` | Yes (Windows build job) | Spawn `titanium`, reverse/edge + Plus control plane; MITM happy-path smoke only |
| `E2E-UI` | Yes (`ui-portable` on Windows/Linux/macOS + dedicated `inspector-ui-macos`) | ViewModel commands, feature sanity (capture/proxy/CA/tools/composer), elevate-CA UX |
| `E2E-UI-Mac` / `E2E-UI-Linux` | Yes (macOS / Linux runners only) | System-proxy backend factory selection for that OS |
| `E2E-UI-Window` | No (opt-in) | Windows FlaUI / real HWND smoke against `TitaniumInspector.exe` |
| `E2E-Slow` | No | Windows WinINET system proxy + Chrome `--disable-quic` |

**Happy path (all three products):** `HappyPathSanityE2ETests` — Inspector sessions in the UI collection, CLI explicit MITM + debug log file, CLI+Plus control-plane auth + MITM + debug log.

Inspector Fiddler-like flow: auto-start + system proxy (settings), Decrypt HTTPS off by default (CONNECT), CA install prompt on enable, Remove/Install root CA, Windows AppContainer loopback exemption (`AppContainerLoopback`; elevation may be required — not mutated in default CI beyond API probe).

```powershell
# PR / local fast suite (same filter as Windows CI build job)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E|TestCategory=E2E-UI"

# Only the cohesive happy-path trio
dotnet test tests/Titanium.E2E.Tests -c Release --filter "FullyQualifiedName~HappyPathSanity"

# Optional Chrome + system proxy (mutates WinINET; restores in finally)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-Slow"

# Optional real Inspector window (Windows)
dotnet test tests/Titanium.E2E.Tests -c Release --filter "TestCategory=E2E-UI-Window"
```

### Local Linux UI via Docker

From a Windows (or any) host with Docker:

```powershell
./tools/InspectorUiDocker/run-e2e-ui.ps1
```

```bash
./tools/InspectorUiDocker/run-e2e-ui.sh
```

This runs `TestCategory=E2E-UI` inside a Linux SDK container (Avalonia Headless). It does **not** run `E2E-UI-Window` / FlaUI.

Build `Titanium.Cli` and `Titanium.Plus` (Release or Debug) before process tests so `CliProcessHarness` can locate `titanium.dll`.
