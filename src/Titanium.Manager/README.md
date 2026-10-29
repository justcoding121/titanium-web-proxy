# Titanium Manager (`titanium-manager`)

SQLite-backed multi-cluster configuration control plane for Titanium Web Proxy fleets.

## Deployment model (v1)

**Single-instance only.** There is no HA, leader election, or multi-writer support in this release.
Run one manager process per deployment; agents dial out to pull config and post acks — the manager never pushes.

Staged rollout waves land in a later release; publish currently applies the revision to all agents in the fleet immediately.

## Quick start

```text
titanium-manager --listen http://127.0.0.1:7700/ --db ./manager.db --admin-token <secret>
```

Or set `TITANIUM_MANAGER_ADMIN_TOKEN` instead of `--admin-token`.

## Auth

| Surface | Mechanism |
|---------|-----------|
| Admin REST (`/v1/fleets`, revisions, tokens, audit, …) | Bearer admin token (hashed in SQLite with role `viewer` / `editor` / `admin`) |
| Agent (`/v1/agent/config`, `/v1/agent/ack`) | Bearer agent token created via `POST /v1/agent-tokens` |
| `GET /v1/healthz` | Unauthenticated |

OIDC for admins is a follow-up; P4 uses local hashed tokens.

## Config revisions

Uploaded YAML is validated with `TwpConfigValidator` (same as the CLI). Inline secret literals are rejected — secrets must use `*Env` or `*File` references so values never sit in the manager database.
