# Inspector

Desktop MITM debugger (Avalonia). Licensed under [PolyForm Noncommercial](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt).

![Titanium Inspector screenshot](../../wiki/images/inspector-screenshot.jpg)

## Install

Prefer the [Download](/download) page (resolves the newest release that has Inspector assets, including prereleases).

### Windows

- **MSI** — guided wizard (license, install folder, progress, Finished with optional Launch). Uninstall from **Settings → Apps** (or Programs and Features); the entry uses the Inspector icon.
- **Portable zip** — extract and run `TitaniumInspector.exe`.

```shell
# Stable community package (v7.0.4)
winget install justcoding121.TitaniumInspector
```

Windows **stable** (`v7.0.4`):

- [MSI](https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.4/TitaniumInspector-win-x64.msi)
- [Portable zip](https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.4/TitaniumInspector-win-x64.zip)

Windows **beta** (`v7.0.4-beta`):

- [MSI](https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.4-beta/TitaniumInspector-win-x64.msi)
- [Portable zip](https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.4-beta/TitaniumInspector-win-x64.zip)

### Linux

Extract the RID zip, then either run `./TitaniumInspector` (portable) or:

```shell
chmod +x install.sh uninstall.sh TitaniumInspector
./install.sh          # ~/.local/share/TitaniumInspector + desktop entry
# later:
./uninstall.sh
```

### macOS

Extract the RID zip, then either run `./TitaniumInspector` (portable) or:

```shell
chmod +x install-app.sh uninstall-app.sh TitaniumInspector
./install-app.sh      # ~/Applications/Titanium Inspector.app
# later:
./uninstall-app.sh
```

## Updates

**Help → Update channel** — Stable (default) or Beta. **Help → Check for updates…** checks the latest release on the selected channel and offers an install only when it is a real change:

- **Newer** release → update dialog (**Update and restart**)
- **Channel switch** (for example Beta → Stable at the same or older version) → switch dialog (**Switch and restart**)
- Already on that channel build (including website/MSI installs of the same version) → **up to date** (no reinstall prompt)

Choosing the accept action downloads the package (MSI for a Program Files install, otherwise the RID zip), closes Inspector, replaces the current installation, and relaunches. Windows Installer cannot apply the **same or an older** ProductVersion over an existing MSI install; in that case Inspector explains that you must uninstall first or use a website package. **Help → About Titanium Inspector…** shows the installed version and licensing details.

**Options → Check for updates on startup** uses the same channel and confirm dialog (never silent-install).

## Quick use

1. Launch Inspector — by default it starts listening and enables system proxy (Capturing on).
2. Check **Decrypt HTTPS** when you want MITM (installs the root CA if needed; may prompt for admin).
3. Use the toolbar **System proxy** / **Capturing** checkboxes to pause either without quitting.

Default bind is typically `127.0.0.1:8866`. Bind address/port are **start-time** settings on the toolbar: editable when the proxy is stopped; disabled while running. Use **Start proxy** / **Stop proxy** (toolbar button or Capture menu) to switch. After Stop → Start, system proxy is turned back on if it was on before Stop, or if **Auto system proxy on start** is checked.

HTTPS stays encrypted (opaque tunnels) until **Decrypt HTTPS** is enabled.

Capture menu latching options (**Capturing**, **Decrypt HTTPS**, **System proxy**, auto-start prefs) show a check when on. Preferences such as **Session retention…**, **HTTPS sites to decrypt…**, **Ignore insecure server certificates** (off by default), and **Logging…** live under **Options**. **Reset Inspector settings…** restores preferences to factory defaults; it does not remove the root CA or clear sessions.

The status strip keeps command feedback on the left and a live **Sessions: N** count on the right, so capture traffic does not wipe tips or export paths.

**Install root CA (current user)** trusts the MITM CA on this PC. On Windows, the OS may show a Trusted Root **Yes/No** security dialog the first time that certificate is added (this is not UAC). On macOS/Linux, Inspector also trusts the CA in Keychain / user NSS (`certutil`). If tools are missing or Keychain needs **Always Trust**, a single recovery dialog offers the next step (install NSS tools via package manager or Homebrew, open Keychain Access, or elevate). Re-installing when the CA is already trusted does not prompt again; orphan same-name roots are cleaned up only when a new thumbprint is installed, or via **Remove** / **Clear and reinstall**. **Remove root CA** clears every same-name Titanium root in the current-user Trusted Root store (including orphans from earlier installs) and best-effort clears Firefox policy/profile trust we added. **Clear and reinstall root CA…** mints a new private key, clears this install’s leaf certificate cache (next to `%AppData%\TitaniumInspector\rootCert.pfx`), removes same-name trusted roots, and prompts to reinstall trust. **Trust CA in Firefox…** (opt-in) enables Windows `ImportEnterpriseRoots` when possible, otherwise imports into the default Firefox profile via NSS `certutil` (may ask you to quit Firefox; on Linux/macOS can offer to install `certutil` first). **Device CA setup…** opens a dialog with steps for phones/other devices and can **Export CA** from there (or use **Export root CA…** on the Capture menu).

Leaf certificates for Inspector are stored under `%AppData%\TitaniumInspector\crts\` (beside the root PFX), not under the shared `%LocalAppData%\Titanium.Web.Proxy\crts` folder used by the library default. On first start after upgrade (and on every clear/reinstall), Inspector best-effort deletes that legacy shared `crts` folder; it never deletes a shared `rootCert.pfx`.

## Right pane: Inspect vs Tools

The right pane has two outer tabs:

| Outer tab | Purpose | Needs a selected session? |
|-----------|---------|---------------------------|
| **Inspect** | Look at one captured session | Yes (otherwise shows a hint) |
| **Tools** | Change how **all** traffic is handled | No — open via **Tools** menu |

Use **Tools → Composer / Breakpoints / AutoResponder / Scripts…** to open the pane on that tool without picking a row first. Selecting a session opens the pane on **Inspect**.

### Inspect (this session)

- **Headers** — request/response headers, cookies, query (labeled sections)
- **Body** — request and response bodies as `=== Request ===` / `=== Response ===` (decoded / JSON when possible; `(empty)` if missing)
- **Hex** — same labeled sections for raw bytes
- **WS Frames** — shown **only for WebSocket** sessions; best-effort text preview of messages (not a full opcode stream)

Search for WebSocket traffic with `is:ws`. Quick filters on the toolbar toggle `hide:tunnel`, `hide:image`, and `is:error` into the same search box. Status classes (`status:2xx` … `status:5xx`), `process:`, and `content-type:` are also supported. The status strip shows **Sessions: N** with no filter, and **visible / total** when a search or quick filter is active.

### Tools (all traffic)

Pipeline order on each request:

**Scripts → AutoResponder → Breakpoints → origin**

#### Composer

Build and send a request through the proxy. **Load from selected** copies method/URL/headers/body from the current session.

#### Breakpoints

Pause matching requests (URL glob; `*` = all) so you can edit the body, **Continue**, or **Abort** (403). At most one pause at a time; unmatched overflow auto-continues; pauses time out after **120 seconds**. Optional **Break on response**.

#### AutoResponder

If **Enabled**, the first matching rule returns a fake status/body **before** the real server (and before breakpoints). Match URLs with `*` wildcards.

#### Scripts

**Not JavaScript or C#.** One directive per line (comments with `#` or `//`):

```text
set-header X-Debug: 1
set-status 404
abort
```

Applies to every captured request/response. On request, `abort` or `set-status` short-circuits AutoResponder, breakpoints, and the origin.


## Platform matrix (system proxy and root CA)

| Feature | Windows | macOS | Linux |
|---------|---------|-------|-------|
| System proxy | WinINET (automatic) | `networksetup` (admin prompt if required) | GNOME `gsettings` + KDE + process `http(s)_proxy` |
| Root CA user trust | Current-user Root store | Login keychain (`security`) + .NET store | .NET store + user NSS (`certutil`, Chromium) |
| Root CA machine / admin | UAC + `certutil` | System keychain (macOS auth dialog) | `pkexec` + `update-ca-certificates` |
| Missing `certutil` | N/A for OS trust | **Trust CA in Firefox…** can run `brew install nss` when Homebrew is present | Recovery dialog can install `libnss3-tools` / `nss-tools` / `mozilla-nss-tools` via `pkexec` |
| Firefox | **Trust CA in Firefox…** sets `ImportEnterpriseRoots` (restart Firefox) | Profile NSS import (needs `certutil`) | Profile NSS import (needs `certutil`) |
| Cancel elevation / recovery | Leaves settings unchanged | Leaves settings unchanged | Leaves settings unchanged |

Notes:

- Headless Linux without polkit/GUI cannot show an admin dialog; use Export CA and install manually.
- **Trust CA in Firefox…** is opt-in under Capture (not auto-run after Install root CA). Default Firefox profile only. Profile roots include classic `~/.mozilla/firefox`, Ubuntu Snap, and Flatpak (`~/.var/app/org.mozilla.firefox/...`). If Firefox is running, Inspector can ask it to quit gracefully (with consent) before writing `cert9.db`.
- On Windows, the first Current User Root install may show an OS Trusted Root **Yes/No** dialog (not UAC); choose **Yes**. Inspector cannot replace that dialog.
- macOS without Homebrew: Export CA and import under Firefox → Authorities (Inspector does not install Homebrew).
- KDE proxy reload is best-effort; a session restart may be needed if apps do not pick up changes.
- If user-level CA install fails, Inspector offers an adaptive recovery dialog (tools / Keychain / admin).
## Other features

- Session grid: method, status, host, URL, Protocol, duration, Wait (TTFB), size, process. Right-click menu: Replay, Load into Composer, Export selected HAR/archive, Copy URL.
- HAR / archive: Export all writes every captured session; Export selected writes the grid multi-selection. Import appends sessions from the file. Replay selected session.
- System proxy and root CA install / untrust / export; Device CA setup dialog for external devices; **Allow Store apps…** on Windows
- Search (`method:GET status:2xx host:example process:chrome is:ws hide:tunnel`); quick filters: Hide CONNECT, Hide images, Errors only
- Optional Plus panels when `Titanium.Plus.dll` is present

## See also

- [Download](/download)
- [Editions](/docs/editions)
- [Library](/docs/library) for embedding the same engine
