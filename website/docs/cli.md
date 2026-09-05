# CLI (`titanium` / `twp`)

Standalone reverse / edge proxy for any backend stack. MIT licensed. Self-contained binaries for Windows, Linux, and macOS — [Download](/download).

## Commands

```text
titanium run -c <config> [-v|--verbose] [--service]
titanium test -c <config>
titanium version [--check] [--plus] [--channel beta]
titanium update [--plus] [--channel beta]
titanium http3-deps status|install
titanium service install|uninstall|start|stop|restart|status
```

`twp` is an alias for the same binary. Nested help: `titanium <command> --help` (and `titanium service install --help`, etc.).

| Command | Purpose |
|---------|---------|
| `run` | Start the proxy from YAML/JSON (or other dialects) |
| `test` | Validate config without serving traffic |
| `version` | Print local version; `--check` compares to the update feed |
| `update` | Self-update the CLI from the release feed (download, verify SHA256, replace install); `--plus` updates the Plus DLL |
| `http3-deps` | Report Quic availability; optionally install system MsQuic on edge hosts |
| `service` | Install / start / stop an OS service so the proxy survives reboot |

Channels: `stable` (default) or `beta` via `--channel` or `TITANIUM_UPDATE_CHANNEL` (other values are rejected). Messages always label the channel and print local → remote versions. `titanium update` upgrades when the feed is newer; same-semver beta switches are allowed; it does not reinstall when already current. `titanium update` does **not** use winget (so beta and non-Windows stay consistent); use winget only for the initial install on Windows stable.

**Alpine / Kubernetes:** download the `linux-musl-*` RID zip (not `linux-x64`). See [HTTP/3](/docs/http3).

## `run`

```text
titanium run -c <config> [-v|--verbose] [--service] [--name <service-name>]
```

| Flag | Meaning |
|------|---------|
| `-c`, `--config` | Path to config (required) |
| `-v`, `--verbose` | Debug console logging |
| `--service` | Service-worker mode (used by `titanium service install`; no “Press Ctrl+C” prompt; SIGTERM / SCM stop) |
| `--name` | Windows SCM name when `--service` is set (default `titanium`) |

Foreground run blocks until Ctrl+C. Exit `0` on clean stop; `1` on config/start errors.

## `test`

```text
titanium test -c <config>
```

Loads and validates the config without opening listeners. Exit `0` when OK; `1` when validation fails.

## `version`

```text
titanium version [--check] [--plus] [--channel stable|beta]
```

Prints local Cli / Core / Abstractions / Configuration versions. With `--check`, compares to the update feed (`0` up to date, `2` update available, `1` feed error). `--plus` includes the Plus DLL.

## `update`

```text
titanium update [--plus] [--channel stable|beta]
```

Downloads the CLI zip (or Plus DLL with `--plus`), verifies SHA256, and replaces the install. If an OS service is running, stop it first so the executable can be replaced:

```shell
titanium service stop
titanium update
titanium service start
```

## `http3-deps`

```text
titanium http3-deps status|install
```

Reports `QuicListener.IsSupported` and optionally installs system MsQuic. Prefer the matching RID zip, which already bundles natives — see [HTTP/3](/docs/http3).

## `service` {#service}

Register the same `titanium run` binary with the OS so it starts at boot and restarts on failure (Windows Service, Linux systemd, or macOS launchd) — the same split nginx uses: one process, the OS supervises it.

```text
titanium service install -c <config> [--name titanium] [--user] [--no-start]
titanium service uninstall [--name titanium] [--user]
titanium service start|stop|restart|status [--name titanium] [--user]
```

| Flag | Meaning |
|------|---------|
| `-c`, `--config` | Config path for **install** (validated; stored as an absolute path) |
| `--name` | Service / unit name (default `titanium`). On macOS the launchd label is `com.justcoding121.<name>` unless the name already starts with `com.` |
| `--user` | Per-user systemd unit or LaunchAgent (no root). **Not supported on Windows.** Ports 80/443 usually fail without privileges. |
| `--no-start` | Install and enable, but do not start immediately |

The unit runs:

```text
titanium run -c <abs-config> --service
```

Working directory is the config file’s directory (so relative cert / static paths in YAML still resolve). Machine services require **Administrator** (Windows) or **sudo** (Linux/macOS). Elevation is not auto-requested — re-run from an elevated prompt.

### Examples

```shell
# Windows (elevated PowerShell / cmd)
titanium service install -c C:\proxy\twp.yaml
titanium service status
titanium service stop
titanium service start

# Linux (systemd)
sudo titanium service install -c /etc/titanium/twp.yaml
# Per-user (no sudo); for start-at-boot without login:
#   loginctl enable-linger $USER
titanium service install -c ~/twp.yaml --user

# macOS (LaunchDaemon)
sudo titanium service install -c /usr/local/etc/titanium/twp.yaml
```

### Logs

| OS | Where to look |
|----|----------------|
| Windows | `%ProgramData%\Titanium\logs\titanium.log` when YAML has no file log (SCM has no console). Event Viewer for service start/stop. |
| Linux | `journalctl -u titanium` (system) or `journalctl --user -u titanium` (`--user`) |
| macOS | `/Library/Logs/Titanium/` (daemon) or `~/Library/Logs/Titanium/` (`--user`) |

### Status exit codes

`titanium service status` exits `0` when the unit is installed (running or stopped), `1` when not installed.

## Minimal ForwardHost reverse

```yaml
schemaVersion: "7.0"
listeners:
  - host: "127.0.0.1"
    port: 8000
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

```shell
titanium test -c twp.yaml
titanium run -c twp.yaml
```

## Routes and clusters

For path-based routing and load balancing, see [Configuration](/docs/configuration).

## Plus sidecar

```shell
titanium update --plus
```

Enable in config (`plus.enabled: true` + control-plane shared secret). Details: [Plus](/docs/plus).

## Config dialects

| Extension | Dialect |
|-----------|---------|
| `.yaml` / `.yml` / `.json` | Native `twp` schema 7.0 |
| `.twp` | Compact site-file (`host / => http://origin`) |
| `.conf` | HTTP-server style (`listen`, `server_name`, `location`, `proxy_pass`) for familiar reverse-proxy configs |

## See also

- [Configuration](/docs/configuration)
- [Install](/docs/install)
- [Download](/download)
