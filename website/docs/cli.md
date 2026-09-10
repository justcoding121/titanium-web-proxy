# CLI

Standalone reverse / edge proxy for any backend stack. MIT licensed. For operators who want YAML (or a familiar reverse-proxy dialect) instead of embedding .NET.

**Next:** [Download](/download) a self-contained binary → write a config → `titanium test` / `titanium run`. Alias: `twp`.

## Quick start

```yaml
schemaVersion: "7.1"
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

Foreground run blocks until Ctrl+C (or SIGTERM). Exit `0` on clean stop; `1` on config/start errors. Opt-in NDJSON access logs via `server.accessLog` (see [Configuration](/docs/configuration)); bodies are never buffered for logging.

Path-based routing and load balancing: [Configuration](/docs/configuration).

## Commands

```text
titanium run -c <config> [-v|--verbose] [--service]
titanium test -c <config>
titanium service install|uninstall|start|stop|restart|status
titanium version [--check] [--plus] [--channel beta]
titanium update [--plus] [--remove-plus] [--channel beta]
titanium http3-deps status|install
```

Nested help: `titanium <command> --help` (and `titanium service install --help`, etc.).

| Command | Purpose |
|---------|---------|
| `run` | Start the proxy from a config file |
| `test` | Validate config without serving traffic |
| `service` | Install / start / stop an OS service so the proxy survives reboot |
| `version` | Print local version; `--check` compares to the update feed |
| `update` | Self-update the CLI (download, verify SHA256, replace install); `--plus` installs/updates Plus; `--remove-plus` deletes it |
| `http3-deps` | Report Quic availability; optionally install system MsQuic on edge hosts |

### `run`

```text
titanium run -c <config> [-v|--verbose] [--service] [--name <service-name>]
```

| Flag | Meaning |
|------|---------|
| `-c`, `--config` | Path to config (required) |
| `-v`, `--verbose` | Debug console logging |
| `--service` | Service-worker mode (used by `titanium service install`; no “Press Ctrl+C” prompt; SIGTERM / SCM stop) |
| `--name` | Windows SCM name when `--service` is set (default `titanium`) |

### `test`

```text
titanium test -c <config>
```

Loads and validates the config without opening listeners. Exit `0` when OK; `1` when validation fails.

### `service` {#service}

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

Working directory is the config file’s directory (so relative cert / static paths in YAML still resolve). Machine services require **Administrator** (Windows) or **root** (Linux/macOS). In an interactive terminal, Titanium asks the OS for permission (UAC on Windows, sudo on Linux/macOS). If you cancel the prompt, or the session is not interactive (CI / redirected IO), re-run from an elevated prompt — or use `--user` on Linux/macOS.

#### Examples

```shell
# Windows — UAC prompt if you are not already Administrator
titanium service install -c C:\proxy\twp.yaml
titanium service status
titanium service stop
titanium service start

# Linux (systemd) — sudo prompt if you are not root
titanium service install -c /etc/titanium/twp.yaml
# Per-user (no elevation); for start-at-boot without login:
#   loginctl enable-linger $USER
titanium service install -c ~/twp.yaml --user

# macOS (LaunchDaemon) — sudo prompt if you are not root
titanium service install -c /usr/local/etc/titanium/twp.yaml
```

#### Logs

| OS | Where to look |
|----|----------------|
| Windows | `%ProgramData%\Titanium\logs\titanium.log` when YAML has no file log (SCM has no console). Event Viewer for service start/stop. |
| Linux | `journalctl -u titanium` (system) or `journalctl --user -u titanium` (`--user`) |
| macOS | `/Library/Logs/Titanium/` (daemon) or `~/Library/Logs/Titanium/` (`--user`) |

#### Status exit codes

`titanium service status` exits `0` when the unit is installed (running or stopped), `1` when not installed.

### `version`

```text
titanium version [--check] [--plus] [--channel stable|beta]
```

Prints local CLI / Core / Abstractions / Configuration versions. With `--check`, compares to the update feed (`0` up to date, `2` update available, `1` feed error). `--plus` includes Plus.

### `update`

```text
titanium update [--plus] [--remove-plus] [--channel stable|beta]
```

Downloads the CLI zip (or Plus with `--plus`), verifies SHA256, and replaces the install. `--remove-plus` deletes Plus beside the CLI (no network; mutually exclusive with `--plus`). If an OS service is running, stop it first so the executable can be replaced:

```shell
titanium service stop
titanium update
titanium service start
```

Channels: `stable` (default) or `beta` via `--channel` or `TITANIUM_UPDATE_CHANNEL` (other values are rejected). Messages always label the channel and print local → remote versions. `titanium update` upgrades when the feed is newer; same-semver beta switches are allowed; it does not reinstall when already current. `titanium update` uses the product download feed on every OS (not OS package managers).

### `http3-deps`

```text
titanium http3-deps status|install
```

Reports Quic support and optionally installs system MsQuic. Prefer the matching download zip, which already bundles natives — see [HTTP/3](/docs/http3).

## Plus sidecar

```shell
titanium update --plus
titanium update --remove-plus
```

Enable in config (`plus.enabled: true` + control-plane shared secret); disable with `plus.enabled: false`. Use `--remove-plus` to delete Plus from disk. Details: [Plus](/docs/plus).

## More

### Config dialects

| Extension | Dialect |
|-----------|---------|
| `.yaml` / `.yml` / `.json` | Native `twp` schema 7.1 |
| `.twp` | Compact site-file (`host / => http://origin`) |
| `.conf` | HTTP-server style (`listen`, `server_name`, `location`, `proxy_pass`) for familiar reverse-proxy configs |

### Alpine / containers

Download the `linux-musl-*` zip (not `linux-x64`) for Alpine or musl-based Kubernetes images. See [HTTP/3](/docs/http3).

### Reload without restart (Unix)

On Linux/macOS, **SIGHUP** reloads routes/clusters from the same config path without dropping the process or in-flight connections (listeners stay bound).

## See also

- [Configuration](/docs/configuration)
- [Install](/docs/install)
- [Download](/download)
