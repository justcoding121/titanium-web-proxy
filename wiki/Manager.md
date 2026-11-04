> For operators running `titanium-manager` with Plus agents. Canonical how-to: [Manager](https://titaniumproxy.com/docs/manager).

`titanium-manager` is a SQLite-backed control plane for Titanium fleets. Operators publish YAML revisions; Plus agents long-poll, apply, and ack or nack. The manager never pushes. **v1 is single-instance only** (no HA / multi-writer).

## Quick start

```text
titanium-manager --listen http://127.0.0.1:7700/ --db ./manager.db --admin-token <secret>
```

Bootstrap the admin token with `--admin-token` or `TITANIUM_MANAGER_ADMIN_TOKEN`. Agents enroll with `plus.options` `manager.url` and `manager.tokenFile` (see [Plus](https://titaniumproxy.com/docs/plus#managed-fleet-agent)).

## What it covers

- Staged publish waves (`matchLabels`, `maxUnavailable`, `bakeSeconds`) with auto-pause and rollback
- Last-known-good YAML on each agent when the manager is unreachable
- GitOps via the publish API (no built-in git sync; agents pull)

Full tables and flags: [titaniumproxy.com/docs/manager](https://titaniumproxy.com/docs/manager). Related: [Capabilities](Capabilities), [Features](Features).
