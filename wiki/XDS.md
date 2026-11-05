> Plus-gated xDS **ADS** client (`plus.enabled: true` and a loaded Plus module). Lazy-loads `Titanium.Xds` when `xds:` is present. The satellite ships in the Plus bundle (`titanium update --plus`), not in the MIT CLI zip.

Configure `xds.server`, optional `xds.tls` and `xds.node`, and optional `server.reloadListeners` (forced on for xDS). The client subscribes to **CDS, EDS, SDS, RDS, LDS** in that order, ACK/NACKs each response, validates translated config, and applies it atomically. Unsupported fields are **NACKed** with a field path; the last accepted snapshot keeps serving.

Supported-field table, NACK rules, listener reconciliation (drain, rollback), and migration notes: [xDS documentation](https://titaniumproxy.com/docs/xds).
