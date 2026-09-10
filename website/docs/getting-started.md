# Getting started

Titanium Web Proxy helps you do three things:

1. **Inspect** HTTP/HTTPS traffic in a desktop app (Inspector)
2. **Run a reverse proxy** in front of your apps (CLI)
3. **Embed** the same engine in a .NET application (Library)

Optional **Plus** adds a dashboard and ops features on top of the CLI. Everything runs on **Windows, Linux, and macOS**.

## Choose a path

| I want to… | Start here |
|------------|------------|
| Debug browser or app traffic | [Download Inspector](/download) → [Inspector guide](/docs/inspector) |
| Put a proxy in front of my site or API | [Download CLI](/download) → create `twp.yaml` below → [CLI](/docs/cli) |
| Add dashboard / metrics / auth helpers | [Plus](/docs/plus) after installing the CLI |
| Use Titanium inside a .NET app | [Library](/docs/library) + [NuGet](https://www.nuget.org/packages/Titanium.Web.Proxy) |

## Reverse proxy in a minute (CLI)

1. [Download](/download) the CLI for your OS and extract it (or use winget / Homebrew — see [Install](/docs/install)).
2. Create `twp.yaml` that forwards to your backend (example: local port 8080):

```yaml
schemaVersion: "7.1"
listeners:
  - host: "127.0.0.1"
    port: 8000
    # false = do not decrypt HTTPS (plain reverse proxy)
    decryptSsl: false
    forwardHost: "127.0.0.1"
    forwardPort: 8080
```

3. Validate and run:

```shell
titanium test -c twp.yaml
titanium run -c twp.yaml
```

`titanium run` stays in the foreground (Ctrl+C to stop). To start at boot: `titanium service install -c twp.yaml` — see [CLI — service](/docs/cli#service). More YAML: [Configuration](/docs/configuration).

## Inspect traffic (Inspector)

1. [Download](/download) and install Inspector.
2. Launch it — capturing and system proxy are on by default on the local machine.
3. Turn on **Decrypt HTTPS** when you need to see inside HTTPS (installs a local root certificate — only on machines you control).

Details: [Inspector](/docs/inspector).

## Embed in .NET (Library)

```shell
dotnet add package Titanium.Web.Proxy
# Newer than stable:
dotnet add package Titanium.Web.Proxy --prerelease
```

Minimal sample and trust notes: [Library](/docs/library). Full API: [ProxyServer](/api/Titanium.Web.Proxy.ProxyServer.html){target="_blank" rel="noreferrer"}.

## Platforms at a glance

| Product | Platforms |
|---------|-----------|
| CLI / Plus | Windows, Linux, macOS (self-contained — no .NET SDK to *run*) |
| Inspector | Windows, macOS, Linux |
| Library | .NET 10 (NuGet) |

## Next

- [Install](/docs/install)
- [Editions & licenses](/docs/editions)
- [Performance](/docs/performance)
