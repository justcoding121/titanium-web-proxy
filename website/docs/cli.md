# CLI (`titanium` / `twp`)

Standalone reverse / edge proxy. MIT licensed. Binaries: [Download](/download).

## Commands

```text
titanium run -c <config> [-v|--verbose]
titanium test -c <config>
titanium version [--check] [--plus] [--channel beta]
titanium update [--plus] [--channel beta]
```

`twp` is an alias for the same binary.

| Command | Purpose |
|---------|---------|
| `run` | Start the proxy from YAML/JSON (or other dialects) |
| `test` | Validate config without serving traffic |
| `version` | Print local version; `--check` compares to the update feed |
| `update` | Download a newer CLI zip; `--plus` updates the Plus sidecar DLL |

Channels: `stable` (default) or `beta` via `--channel` or `TITANIUM_UPDATE_CHANNEL`.

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
