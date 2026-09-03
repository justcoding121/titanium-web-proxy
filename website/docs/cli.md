# CLI (`titanium` / `twp`)

Standalone reverse / edge proxy for any backend stack. MIT licensed. Self-contained binaries for Windows, Linux, and macOS — [Download](/download).

## Commands

```text
titanium run -c <config> [-v|--verbose]
titanium test -c <config>
titanium version [--check] [--plus] [--channel beta]
titanium update [--plus] [--channel beta]
titanium http3-deps status|install
```

`twp` is an alias for the same binary.

| Command | Purpose |
|---------|---------|
| `run` | Start the proxy from YAML/JSON (or other dialects) |
| `test` | Validate config without serving traffic |
| `version` | Print local version; `--check` compares to the update feed |
| `update` | Self-update the CLI from the release feed (download, verify SHA256, replace install); `--plus` updates the Plus DLL |
| `http3-deps` | Report Quic availability; optionally install system MsQuic on edge hosts |

Channels: `stable` (default) or `beta` via `--channel` or `TITANIUM_UPDATE_CHANNEL` (other values are rejected). Messages always label the channel and print local → remote versions. `titanium update` upgrades when the feed is newer; same-semver beta switches are allowed; it does not reinstall when already current. `titanium update` does **not** use winget (so beta and non-Windows stay consistent); use winget only for the initial install on Windows stable.

**Alpine / Kubernetes:** download the `linux-musl-*` RID zip (not `linux-x64`). See [HTTP/3](/docs/http3).

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
| `.conf` | nginx-ish (`listen`, `server_name`, `location`, `proxy_pass`) |

## See also

- [Configuration](/docs/configuration)
- [Install](/docs/install)
- [Download](/download)
