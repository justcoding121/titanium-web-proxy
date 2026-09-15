# Inspector

Desktop debugger for HTTP and HTTPS traffic. Decrypt HTTPS (man-in-the-middle / MITM) only on machines you control.

![Titanium Inspector screenshot](../../wiki/images/inspector-screenshot.jpg)

## Quick use

1. [Download](/download) and install Inspector for your OS.
2. Launch it — by default it starts listening and turns on system proxy (**Capturing** on). Default bind is usually `127.0.0.1:8866`.
3. Turn on **Decrypt HTTPS** when you need to see inside HTTPS (installs a local root certificate if needed; you may get an OS trust prompt).
4. Use the toolbar **System proxy** / **Capturing** checkboxes to pause either without quitting.

HTTPS stays encrypted (opaque tunnels) until **Decrypt HTTPS** is on.

Capture menu options (**Capturing**, **Decrypt HTTPS**, **System proxy**, **Capture local traffic**, auto-start prefs) show a check when on. **Allow Store apps…** (Windows) sits with System proxy. Preferences such as **Session retention…**, **Excluded hosts…**, **Ignore insecure server certificates**, and **Logging…** live under **Options**.

## Install

Prefer the [Download](/download) page.

### Windows

- **MSI** — installer wizard; uninstall from **Settings → Apps**.
- **Portable zip** — extract and run `TitaniumInspector.exe`.

Stable links (`v7.0.5`): [MSI](https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.5/TitaniumInspector-win-x64.msi) · [zip](https://github.com/justcoding121/titanium-web-proxy/releases/download/v7.0.5/TitaniumInspector-win-x64.zip). Beta: the Download beta section.

### Linux

Extract the zip, then run `./TitaniumInspector`, or:

```shell
chmod +x install.sh uninstall.sh TitaniumInspector
./install.sh          # ~/.local/share/TitaniumInspector + desktop entry
```

### macOS

Extract the zip, then run `./TitaniumInspector`, or:

```shell
chmod +x install-app.sh uninstall-app.sh TitaniumInspector
./install-app.sh      # ~/Applications/Titanium Inspector.app
```

## Updates

**Help → Update channel** — Stable (default) or Beta. **Help → Check for updates…** offers install only when there is a real change (newer build or channel switch). Accept downloads the package, closes Inspector, replaces the install, and relaunches.

## Right pane: Inspect and tools

A far-right **icon rail** (Inspect, Composer, Breakpoints, AutoResponder, Scripts, Map Remote) is always visible. Clicking an icon opens that tool’s content to the **left of the rail** and **pushes** the session grid; the grid scrolls horizontally when columns no longer fit. Click the same icon again (or ✕) to close the content pane; the rail stays. Tooltips show full names.

Inspect keeps Headers / Body / Hex (and Diff / WS / SSE / Protobuf when relevant) as tabs inside the Inspect content. Selecting a session while a tool is open does **not** switch away from that tool. Tools change how **all** traffic is handled and do not require a selected session.

Use **Tools → Composer / Breakpoints / AutoResponder / Scripts…** to open the matching icon. Opening content when it was closed from a session click lands on **Inspect**.

### Inspect (this session)

- **Headers** — request/response headers, cookies, query (labeled sections). **Copy headers** copies the dump.
- **Body** — request and response as `=== Request ===` / `=== Response ===`. **Pretty** / **Raw** toggles JSON, XML, and HTML source indent (Pretty runs when the Body tab is selected). Images show a bitmap preview instead of mojibake. Banners explain truncated, not-captured, or streaming bodies. **Save request…** / **Save response…** write the **captured** bytes (incomplete when truncated).
- **Hex** — labeled hex dump (first 4 KB of the captured preview).
- **WS Frames** — shown for WebSocket sessions; live frames when available (direction, opcode, payload preview)
- **SSE** — shown for `text/event-stream` responses; Inspector does **not** buffer SSE in `BeforeResponse` (events stream to the client; a 2 MiB preview may fill while open)
- **Protobuf** — wire-format field dump for gRPC and gRPC-JSON-transcoded upstream frames (field number, wire type, value). MVP does **not** require a `.protoset` / descriptor set; the optional settings field `ProtobufDescriptorSetPath` is stored for a future typed decode. Until then, the Protobuf tab always shows the JSON wire dump.

**Body capture limits:** finite bodies with known `Content-Length` up to 32 MiB are buffered then previewed at 2 MiB (text view capped at 256 KiB). Larger known-length bodies are **not captured** so downloads are not stalled. Chunked/finite unknown-length bodies still buffer (capped). Size column shows the original length when known.

Search for WebSocket traffic with `is:ws`. Search for gRPC with `is:grpc`, and for gRPC-JSON transcoded sessions with `is:transcoded` (client REST/JSON vs upstream gRPC faces appear in the Headers/Body inspect panes). Quick filters on the toolbar toggle `hide:tunnel`, `hide:image`, and `is:error` into the same search box. Status classes (`status:2xx` … `status:5xx`), `process:`, and `content-type:` are also supported. The status strip shows **Sessions: N** with no filter, and **visible / total** when a search or quick filter is active.

**Network throttle:** use the toolbar **Throttle** combo (`None`, `Slow 3G`, `Fast 3G`, `LTE`) to add latency and bandwidth shaping on body writes / WebSocket frames during capture. Off by default (`None`); the hot path skips delay work when no profile is enabled.

### Tools (all traffic)

Pipeline order on each request:

**Scripts → AutoResponder → Map Remote → Breakpoints → origin**

#### Composer

Build and send a request through the proxy. **Load from selected** copies method/URL/headers/body from the current session. **Load body from file…** loads small files into the editor, or streams large files on **Send** without stuffing them into the text box. Responses are preview-capped (no hang on endless streams).

#### Breakpoints

Pause matching requests (URL glob; `*` = all) so you can edit the body, **Continue**, or **Abort** (403). At most one pause at a time; unmatched overflow auto-continues; pauses time out after **120 seconds**. Optional **Break on response**. Editing a streaming (SSE) body is refused.

Match **URL** and optional **GraphQL operation** together: leave GraphQL blank to pause every operation; set it (for example `GetUser`) to pause only that client request. **From selected session** copies the operation name from the session selected in the grid.

#### AutoResponder

If **Enabled**, the first matching rule returns a fake status/body **before** the real server (and before breakpoints). Match URLs with `*` wildcards. Inline body Add/Update is capped at 256 KiB — use Map Local for larger stubs.

**Map Local:** set an optional file path on the rule (or use **Browse…**). When the path is set, the response body is **streamed from that file** (with `Content-Length`) instead of the inline body field. Inline body is used when Map Local is empty. Missing or oversized files cause the rule to be skipped (request continues to breakpoints/origin).

**Match** (URL + optional GraphQL) decides which client requests a rule applies to. **Respond with** is the fake status/body. Same `/graphql` URL can have one rule per operation.

#### Map Remote

If **Enabled**, the first matching rule rewrites the request URL to another absolute origin **before** breakpoints and the real server. Match with `*` wildcards. A single `*` in both match and target preserves the captured path/query suffix (for example match `https://prod.example/*` → target `http://127.0.0.1:5000/*`). Map Remote does not run when AutoResponder / Map Local already answered the request. Optional GraphQL operation on **Match** rewrites only that operation.

GraphQL matching reads the client JSON `operationName` (or a named `query`/`mutation`). It does **not** force buffering of huge request bodies (that would reset HTTP/2).

#### Scripts

One command per line (not JavaScript or C#). Comments start with `#` or `//`. Applies to every captured request/response.

| Command | Meaning |
|---------|---------|
| `set-header Name: Value` | Add or replace a header. Traffic still continues. |
| `set-status 404` | On request: answer immediately and skip AutoResponder, breakpoints, and the origin. On response: rewrite the status the client sees. |
| `abort` | Block with 403. Combine with `set-status` to pick a different code. |

```text
set-header X-Debug: 1
set-status 404
abort
```

## Advanced

### Excluded hosts

**Options → Excluded hosts…** edits OS bypass, tunnel-only, and auto-tunnel learning. **Capture → Capture local traffic** controls whether loopback uses the system proxy (not a host list).

| Layer | Effect |
|-------|--------|
| **OS bypass** | Traffic never reaches Inspector (when **System proxy** is on) |
| **Not decrypted hosts** | Session stays visible but HTTPS stays opaque |
| **Automatically not decrypt on decrypt failure** | Session-only list of hosts learned after origin TLS fails under decrypt, or after the origin server returns **403/429**. Document navigations auto-retry via meta-refresh onto an opaque CONNECT. **On by default.** Cap + TTL; cleared on restart unless you **Add to not decrypted hosts**. |

Factory seeds keep common identity / SSO hosts on OS bypass so sign-in keeps working. Right-click a session → **Exclude host…** to stop reading that host's HTTPS (it still appears in the list). Opaque sessions show why they stayed encrypted (including **auto-tunneled after decrypt failure**). Search: `is:opaque`, `opaque-reason:learned`. **Chrome QUIC** may bypass the proxy entirely — not fixable via host lists.

Hostile hosts that return 403/429 under MITM on a **document** navigation are recovered automatically (brief meta-refresh; no manual reload). Non-document 403/429 need repeated strikes before later CONNECTs tunnel. Automation browsers may still see site captchas after tunneling.

### Root certificate (Decrypt HTTPS)

**Install root CA (current user)** trusts the decrypt certificate on this PC (OS may show a Yes/No trust dialog). Use **Export root CA…** / **Setup external device CA** for phones or other devices. **Remove root CA** / **Clear and reinstall…** / **Trust CA in Firefox…** are on the Capture menu when you need cleanup or Firefox-specific trust. Prefer those menu actions over editing certificate stores by hand.

### Platform matrix (system proxy and root CA)

| Feature | Windows | macOS | Linux |
|---------|---------|-------|-------|
| System proxy | WinINET (automatic) | `networksetup` (disables PAC/WPAD/SOCKS so CFNetwork/Firefox see HTTP(S); admin prompt if required) | GNOME `gsettings` + KDE + process `http(s)_proxy` + Chromium/Edge launch hooks + Firefox profile prefs |
| Instant browser switch | OS settings (live for most apps) | OS settings (live for most apps; Firefox may need restart) | Chromium/Edge quit+relaunch with `--proxy-server`; Firefox prefs + quit/relaunch |
| Root CA user trust | Current-user Root store | Login keychain (`security`) + .NET store | .NET store + user NSS (`certutil`, Chrome/Edge/Chromium incl. Snap/Flatpak DBs) |
| Root CA machine / admin | UAC + `certutil` | System keychain (macOS auth dialog) | `pkexec` + `update-ca-certificates` |
| Missing `certutil` | N/A for OS trust | **Trust CA in Firefox…** can run `brew install nss` when Homebrew is present | Recovery dialog can install `libnss3-tools` / `nss-tools` / `mozilla-nss-tools` via `pkexec` |
| Firefox | **Trust CA in Firefox…** sets `ImportEnterpriseRoots` (restart Firefox) | Install root CA writes profile `user.js` (`security.enterprise_roots.enabled`); NSS `certutil` is the fallback. Never modifies `Firefox.app`. | Profile `user.js` OS-root trust first; NSS import fallback; system proxy also writes `network.proxy.*` in the default profile |
| Cancel elevation / recovery | Leaves settings unchanged | Leaves settings unchanged | Leaves settings unchanged |

Notes:

- Headless Linux without polkit/GUI cannot show an admin dialog; use Export CA and install manually.
- **Trust CA in Firefox…** remains available for NSS profile import. **Install root CA** also best-effort writes `security.enterprise_roots.enabled` in the default profile `user.js` (macOS includes `FirefoxDeveloperEdition` and `Firefox Nightly` profile roots; Linux includes Snap and Flatpak). Inspector does **not** write into `Firefox.app`. If Firefox is running, Inspector can ask it to quit gracefully (with consent) before writing `cert9.db`; on macOS it falls back to SIGTERM if osascript is blocked.
- On Linux, **System proxy** also updates Chromium-family managed policy / Preferences / `.desktop` helpers and Firefox `prefs.js`, then relaunches already-open Chrome, Edge, Chromium, Brave, and Firefox so traffic switches without a manual restart. On exit or proxy off, settings are restored and browsers are relaunched without the Inspector endpoint (with a short fail-open tunnel if Chromium still pointed at a dead port).
- On Windows, the first Current User Root install may show an OS Trusted Root **Yes/No** dialog (not UAC); choose **Yes**. Inspector cannot replace that dialog.
- macOS without Homebrew: OS-root trust via `user.js` does not need `certutil`. Export CA and import under Firefox → Authorities only if enterprise-roots is not enough.
- Enabling **System proxy** on macOS turns off PAC, WPAD, and SOCKS so Firefox (CFNetwork) sees the HTTP(S) proxy; previous PAC/SOCKS settings are restored when System proxy is turned off. Firefox that was already open may need a restart.
- KDE proxy reload is best-effort; a session restart may be needed if apps do not pick up changes.
- If user-level CA install fails, Inspector offers an adaptive recovery dialog (tools / Keychain / admin).
- **Contributors:** unit/integration tests never open OS cert UI (`TITANIUM_SKIP_ROOT_STORE_UI=1`). For live System proxy / Install CA / browsers / Store apps UX, run [`tools/InspectorDesktopProbe`](../../tools/InspectorDesktopProbe/README.md) (`dotnet run --project tools/InspectorDesktopProbe -- all`). Results: `tools/InspectorDesktopProbe/results/last-run.json`.

## Other features

- Session grid: method, status, host, URL, Protocol, duration, Wait (TTFB), size, process. Right-click menu: Replay, Load into Composer, Export selected HAR/archive, Copy URL, Copy as curl, Copy as fetch, Diff selected (exactly two sessions).
- **Copy as curl / fetch:** with one session selected, generate a shell `curl` command or a JavaScript `fetch(...)` call from the request URL, method, headers, and body (CONNECT tunnels are skipped). The snippet is copied to the clipboard.
- **Session Diff:** with exactly two sessions selected, compare method/URL/status/headers/bodies offline. The result opens on the Inspect **Diff** tab and is copied to the clipboard.
- HAR / archive: Export all writes every captured session; Export selected writes the grid multi-selection. Import appends sessions from the file. Replay selected session.
- System proxy, **Capture local traffic**, and root CA install / untrust / export; **Setup external device CA** dialog for external devices; **Allow Store apps…** on Windows
- Search (`method:GET status:2xx host:example process:chrome is:ws hide:tunnel`); quick filters: Hide CONNECT, Hide images, Errors only
- Optional Plus panels when `Titanium.Plus.dll` is present

## See also

- [Download](/download)
- [Editions](/docs/editions)
- [Library](/docs/library) for embedding the same engine
