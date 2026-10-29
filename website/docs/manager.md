# Manager

`titanium-manager` is a SQLite-backed control plane for Titanium fleets. Operators publish YAML revisions; Plus agents on each node long-poll for config, apply it, and ack or nack. The manager never pushes.

**Related:** [Plus](/docs/plus) · [Configuration](/docs/configuration) · [Capability coverage](/docs/capabilities)

## Run the manager

```text
titanium-manager --listen http://127.0.0.1:7700/ --db ./manager.db --admin-token <secret>
```

| Flag / env | Role |
|------------|------|
| `--listen` | Listen prefix (default `http://127.0.0.1:7700/`) |
| `--db` | SQLite path (default `./titanium-manager.db`) |
| `--admin-token` / `TITANIUM_MANAGER_ADMIN_TOKEN` | Bootstrap admin bearer token (hashed into SQLite as role `admin`) |

`GET /v1/healthz` is unauthenticated (container HEALTHCHECK). Admin REST (`/v1/fleets`, revisions, tokens, audit, rollouts) and agent endpoints require a bearer token. Docker and Helm packaging live under `packaging/docker/Dockerfile.manager` and `deploy/helm/titanium-manager`.

## Single-instance SQLite

**v1 is single-instance only.** There is no HA, leader election, or multi-writer support. Run one manager process per deployment and put the SQLite file on durable storage (a PVC in Kubernetes). Scaling out writers is not supported.

## Agent options (Plus)

On each proxy, set Plus options so the agent dials the manager. Without `manager.url` the agent does not start.

| Key | Role |
|-----|------|
| `manager.url` | Manager base URL (required to enroll) |
| `manager.tokenFile` / `manager.token` | Agent bearer token from `POST /v1/agent-tokens` |
| `manager.fleet` | Fleet id the token is scoped to |
| `manager.nodeId` | Node identity (default: machine name) |
| `manager.labels` | JSON object of labels for staged waves |
| `manager.mode` | `advisory` (default) or `managed` |
| `manager.stateDir` | Directory for last-known-good files |
| `manager.applyDebounceMs` | Debounce before applying a pulled revision |
| `manager.tls.insecure` | Skip TLS verification (lab only) |

```yaml twp
plus:
  enabled: true
  options:
    manager.url: "https://manager.example:7700/"
    manager.tokenFile: /run/secrets/manager-agent-token
    manager.fleet: "1"
    manager.nodeId: "edge-a"
    manager.mode: managed
    manager.labels: '{"wave":"1","region":"us-east"}'
```

In `managed` mode the agent applies published YAML through the same path as a file reload. `advisory` reports status without applying. `managed` conflicts with Gateway API / Ingress controller options and falls back to advisory with a warning.

## Last-known-good

After a successful managed apply, the agent writes `last-known-good.yaml` and `last-known-good.hash` under `manager.stateDir` (or the Plus state directory). If the manager is unreachable on the first poll, the agent applies that snapshot so a cold start can still serve the last good revision.

## Staged rollouts

`POST .../revisions/{id}/publish` can take a `waves` array. Each wave has `matchLabels`, `maxUnavailable`, and `bakeSeconds`. Agents whose labels match the current wave receive the new revision; the next wave waits until ready agents meet the unavailability budget and the bake time elapses. The manager auto-pauses a rollout that fails health checks; operators can pause, resume, or roll back through the rollouts API and UI. An empty `waves` list publishes to the whole fleet at once.

## GitOps

There is no built-in git sync. A pipeline that validates YAML and calls the manager publish API (create a revision, then publish, optionally with waves) is the supported GitOps shape. Agents pull; the manager does not push config into the cluster.

## See also

- [Plus](/docs/plus#managed-fleet-agent)
- [Configuration — graceful reload](/docs/configuration#graceful-reload)
- [Capability coverage](/docs/capabilities)
