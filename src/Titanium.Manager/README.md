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

## Docker

Build from the repository root (multi-arch publish mirrors the CLI/gateway images):

```bash
docker buildx build -f packaging/docker/Dockerfile.manager -t titanium-manager:dev .
```

Run with a volume for SQLite and an admin bootstrap token:

```bash
docker run --rm -p 7700:7700 \
  -e TITANIUM_MANAGER_ADMIN_TOKEN=secret \
  -v manager-data:/var/lib/titanium-manager \
  --read-only --tmpfs /tmp \
  titanium-manager:dev
```

The image listens on `http://+:7700/`, stores the DB at `/var/lib/titanium-manager/manager.db`, runs as UID 1654, and HEALTHCHECKs `GET /v1/healthz`. Published releases land at `ghcr.io/justcoding121/titanium-manager`.

## Helm

Chart: [`deploy/helm/titanium-manager`](../../deploy/helm/titanium-manager). Single replica, PVC for SQLite, Secret for `TITANIUM_MANAGER_ADMIN_TOKEN`, probes on `/v1/healthz`.

```bash
helm install manager deploy/helm/titanium-manager -n titanium-manager --create-namespace
```

## Auth

| Surface | Mechanism |
|---------|-----------|
| Admin REST (`/v1/fleets`, revisions, tokens, audit, …) | Bearer admin token (hashed in SQLite with role `viewer` / `editor` / `admin`) |
| Agent (`/v1/agent/config`, `/v1/agent/ack`) | Bearer agent token created via `POST /v1/agent-tokens` |
| `GET /v1/healthz` | Unauthenticated |

OIDC for admins is a follow-up; P4 uses local hashed tokens.

## Config revisions

Uploaded YAML is validated with `TwpConfigValidator` (same as the CLI). Inline secret literals are rejected — secrets must use `*Env` or `*File` references so values never sit in the manager database.
