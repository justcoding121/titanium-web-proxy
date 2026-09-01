# Migrating from Titanium Web Proxy 6.x to 7.0

## What stays the same

- `ForwardHost` / transparent reverse behavior is unchanged when you do **not** configure a route table.
- Existing public Core APIs remain; 7.0 does not remove 6.x surface for this delivery.
- Target framework is still **.NET 10**.

## Package split

| Package | Role |
|---------|------|
| `Titanium.Web.Proxy` | Engine (MIT) |
| `Titanium.Web.Proxy.Abstractions` | Shared route/cluster/middleware/plugin contracts (MIT) |
| `Titanium.Web.Proxy.Configuration` | YAML/JSON + dialect readers (MIT) — **optional** for embedders |

Apps that only construct `ProxyServer` and set `ForwardHost` **do not** need Configuration.

## Optional reverse-proxy routes

Pass `ReverseProxyOptions` (routes/clusters) only when you want declarative routing. When routes are unset/`null`, Core keeps the 6.x control flow (zero-cost default).

A single-destination route table that matches `ForwardHost:port` remains terminate-lite eligible.

## When interception turns on

Full session interception is used when:

- `EnableHttpInterception` is set, or
- session event handlers are subscribed, or
- config requires middleware, transforms that need the session path, ACME challenge handling, or static-file synthetic responses.

Simple reverse configs in the CLI must **not** set `EnableHttpInterception` or subscribe session handlers.

## Editions

- **Titanium.Cli** (`titanium` / `twp`) — MIT daemon
- **Titanium.Plus** — PolyForm Noncommercial plugin DLL (ALC); not on nuget.org
- **Titanium Inspector** — PolyForm Noncommercial desktop app

See the README Editions table for licenses and distribution channels.
