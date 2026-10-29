Titanium Manager as a single-replica Kubernetes Deployment with a PVC for SQLite and a Secret for the admin bootstrap token.

## Install

```bash
helm install manager deploy/helm/titanium-manager -n titanium-manager --create-namespace
```

Override the image tag after a release:

```bash
helm upgrade --install manager deploy/helm/titanium-manager -n titanium-manager \
  --set image.tag=7.0.27
```

## Admin token

The chart creates Secret `<release>-titanium-manager-admin` (or uses `admin.token.existingSecret`) and injects `TITANIUM_MANAGER_ADMIN_TOKEN`. Read it with:

```bash
kubectl -n titanium-manager get secret manager-titanium-manager-admin \
  -o jsonpath='{.data.admin-token}' | base64 -d
```

## Persistence

`persistence.enabled` must stay true. The database is `<mountPath>/manager.db` (default `/var/lib/titanium-manager/manager.db`). Keep `replicaCount: 1`.

## Probes

Startup, liveness, and readiness all GET `/v1/healthz` on port 7700.
