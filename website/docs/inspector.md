# Inspector

Desktop MITM debugger (Avalonia). Licensed under [PolyForm Noncommercial](https://github.com/justcoding121/titanium-web-proxy/blob/develop/licenses/PolyForm-Noncommercial-1.0.0.txt).

## Install

Windows:

- [MSI](https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/TitaniumInspector-win-x64.msi)
- [Portable zip](https://github.com/justcoding121/titanium-web-proxy/releases/latest/download/TitaniumInspector-win-x64.zip)

```shell
winget install justcoding121.TitaniumInspector
```

## Quick use

1. Launch Inspector — by default it starts listening and enables system proxy (Capturing on).
2. Check **Decrypt HTTPS** when you want MITM (installs the root CA if needed; may prompt for admin).
3. Use the toolbar **System proxy** / **Capturing** checkboxes to pause either without quitting.

Default bind is typically `127.0.0.1:8866`. HTTPS stays opaque CONNECT tunnels until **Decrypt HTTPS** is enabled.

Capture menu latching options (**Capturing**, **Decrypt HTTPS**, **System proxy**, auto-start prefs, **Debug file logging**) show a check when on. Turning on debug file logging also writes the log path to the status bar.

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

Search for WebSocket traffic with `is:ws`.

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
| Cancel elevation | Leaves settings unchanged | Leaves settings unchanged | Leaves settings unchanged |

Notes:

- Headless Linux without polkit/GUI cannot show an admin dialog; use Export CA and install manually.
- Firefox may require trusting the CA in its own certificate store.
- KDE proxy reload is best-effort; a session restart may be needed if apps do not pick up changes.
- If user-level CA install fails, Inspector offers an elevated retry (OS admin prompt).
## Other features

- Session grid: method, status, host, URL, protocol, duration, TTFB, size, process
- HAR / archive: Export all writes every captured session; Export selected writes the grid multi-selection. Import appends sessions from the file. Replay selected session.
- System proxy and root CA install / untrust / export / device setup
- Search (`method:GET status:200 host:example is:ws`)
- Optional Plus panels when `Titanium.Plus.dll` is present

## See also

- [Download](/download)
- [Editions](/docs/editions)
- [Library](/docs/library) for embedding the same engine
