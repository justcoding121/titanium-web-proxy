# Production-parity re-audit (P10 / F34)

> Operators and maintainers: snapshot of the peer-gap audit after the leftovers branch landed. Pair with [Capability coverage](/docs/capabilities).

**Branch tip audited:** `feat/production-parity` (after F25–F29).  
**Leftovers `BASELINE_SHA`:** `46163c95ce7df6e6d3271fc1d2cf26c655fca0aa` (see `tools/RpsLoadProbe/PERF-GATES.md`).

## Section 2B checklist vs capability matrix

| Peer gap | Matrix status | Notes |
|----------|---------------|-------|
| WebSocket over HTTP/3 (RFC 9220) to H1/H2/H3 origins | supported | `server.enableRfc8441` |
| Gateway TLSRoute / TCPRoute / UDPRoute / BackendTLSPolicy / GRPCRoute | supported | Optional CRD informers; BackendTLSPolicy RBAC gated |
| GeoIP route policy | supported | Operator-supplied MMDB; CLI satellite |
| Response compression on all bridges + optional zstd | partial / supported | Fast-path passthrough stays identity |
| gRPC active health | supported | `grpc.health.v1` |
| DNS SRV discovery | supported | Plus; off by default |
| TCP TLS terminate / originate | supported | Passthrough listeners |
| Zone-aware balancing | supported | Off unless configured |
| Dashboard / overlay editing | partial | Overlay + validated snapshot PUT; edit forms incomplete |
| xDS ADS client with dynamic listeners | supported | Lazy `Titanium.Xds`; no xDS server |
| Listener changes without restart | supported | `server.reloadListeners`; xDS forces on |
| Linux TCP hot restart / SO_REUSEPORT | partial | ReusePort available; full pid-file handover incomplete |
| Overload protection | supported | Off by default |

## Still not offered (by design or runtime)

| Topic | Reason |
|-------|--------|
| Explicit-proxy h2c Upgrade | Removed from RFC 9113 |
| Decrypting QUIC inside CONNECT-UDP | End-to-end encrypted |
| 0-RTT / QUIC connection migration | Not exposed by `System.Net.Quic` on .NET 10 |
| WebTransport | Needs RFC 9221 QUIC datagrams; streams-only today |
| Cipher-suite policy on Windows | No public API |
| Cross-replica TLS session-ticket key sharing | Not exposed by `SslStream` |
| OpenResty / Kong / APISIX Lua APIs | Vendor APIs; use Lua 5.4 + proxy-wasm instead |
| xDS server | Data-plane product; out of scope (C3) |
| win-arm64 scripting natives | Upstream native builds |

## Newly noted gaps (follow-ups, not blockers for this PR)

1. **Dashboard edit forms** — overlay and control-plane APIs exist; operator UI pages remain incomplete.
2. **Hot-restart handover** — `ReusePort` + config keys ship; SIGUSR2 / pid-file kill-handover and HTTP/3 deferred bind need a dedicated follow-up.
3. **xDS proto fidelity** — trimmed Envoy subsets; replace with pinned full upstream files when expanding the field list.
4. **Delta xDS** — state-of-the-world only in v1.
5. **Checkpoint A/B** — C0–C6 RPS compare-spot against `BASELINE_SHA` must be linked in the Draft PR before merge (not run in this local pass).

## Runtime re-check (.NET 10)

Re-checked against current .NET 10 `System.Net.Quic` / `SslStream` surface: no public datagrams, 0-RTT accept, connection migration, or shared session-ticket keys. Nothing in the blocked list was promoted.

## Whole-plan proof status

| Gate | Status |
|------|--------|
| Capability matrix matches code for leftovers features | Updated in this branch |
| Nothing-loaded-when-off for GeoIP / xDS satellites | Tests present |
| Final compare-spot / compare-editions / publish gates | Pending CI on Draft PR (checkpoint C6) |
