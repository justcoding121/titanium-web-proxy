# Protocol Hardening Backlog

> **Context** — September 2026 principal-architect review of the core TWP NuGet package
> (HTTP/2, HTTP/3, HPACK, QPACK, flow-control, TLS, relay arms).
> Thirty-five findings were triaged. Twelve were fixed in commit `d11d03d9`; a QPACK
> fail-fast guard followed in `bb9abd85`. The remaining items are closed in this pass:
> either implemented, or reclassified as intentional design with an opt-in override
> where a performance trade-off is involved.
>
> After this document: a second review of TWP core should not re-open these as bugs.

---

## Final accounting of all 35 findings

| Status | Count | Items |
|--------|-------|-------|
| Fixed in `d11d03d9` / `bb9abd85` | 13 | F1–F12 + QPACK fail-fast (superseded by full RFC 9204 decode) |
| Intentional design — documented, no further code change | 7 | B1-a/b/c/d + B1-e (was B3-b) + B1-f (was B2-c) + B1-g (was B3-e) |
| Fixed in the protocol-gap pass | 15 | B2-a/b/d/e/f/g/h/i/j + B3-a/c (obs-fold) + B3-d + `Http2RelayValidation` policy |
| **Total** | **35** | |

---

## Part A — Re-review of fixes already applied (`d11d03d9`)

Each item below should be re-tested whenever a related arm is touched.

| ID | What was fixed | File(s) | Re-review trigger |
|----|----------------|---------|-------------------|
| F1 | **ServerHello delay** — cert-gen and h2 probe parallelised before CONNECT 200; `Http2ServerHelloProbeBudget` re-applied only after cert is ready | `ExplicitClientHandler.cs`, `TransparentClientHandler.cs`, `Http2NegotiationHandler.cs` | Any change to CONNECT timing, cert caching, or probe coalescing |
| F2 | **Speculation correctness** — cold-start h2 offer reverted to `clientOffersHttp2` (not gated on `AllowHttpProtocolTranslation`); `ApplyDeferredHttp2Negotiation` stays as the correct gating point post-TLS | `ExplicitClientHandler.cs`, `TransparentClientHandler.cs` | Any change to ALPN speculation or `AllowHttpProtocolTranslation` semantics |
| F3 | **HPACK plain CONNECT** — `:scheme` and `:path` omitted for plain CONNECT (RFC 9113 §8.3.1) | `Http2Helper.Hpack.cs` | Any change to HPACK encoder entry-point or CONNECT handling |
| F4 | **HPACK empty `:authority`** — empty authority is no longer encoded; Host header carries it instead | `Http2Helper.Hpack.cs` | Any change to authority/Host logic in bridge handlers |
| F5 | **GOAWAY CTS safety** — removed `Cancellation.Dispose()` inside GOAWAY stream loop while the stream is still registered | `Http2Helper.Copy.cs` | Any change to GOAWAY handling or stream lifecycle |
| F6 | **IPv6 `:authority`** — replaced `LastIndexOf(':')` with `AuthorityParser` in the HTTP/2 interception predicate | `Http2Helper.Copy.Headers.cs` | Any new authority/host-port parsing added to the H2 path |
| F7 | **Zero WINDOW_UPDATE** — stream-level increment=0 is now RST_STREAM(PROTOCOL_ERROR) not a connection error in `Http2OriginConnection` | `Http2OriginConnection.cs` | Any change to flow-control receive path in OriginConnection |
| F8 | **RFC 8441 false advertisement** — ENABLE_CONNECT_PROTOCOL=1 no longer injected toward client when origin never sent it; only recorded when origin sent it | `Http2Helper.Copy.cs` | Any change to SETTINGS relay or RFC 8441 enable logic |
| F9 | **H3 GOAWAY control stream** — client GOAWAY sends server GOAWAY + drains instead of returning (which would dispose the stream → `H3_CLOSED_CRITICAL_STREAM`) | `Http3Connection.cs` | Any change to H3 control stream processing |
| F10 | **H3 GOAWAY Last-Stream-ID atomicity** — `_highestStreamIdSeen` updated with CompareExchange loop (true atomic max) | `Http3Connection.cs` | Any change to stream ID tracking in H3 |
| F11 | **SslProtocol stored as negotiated not bitmask** — both transparent and explicit handlers store `sslStream.SslProtocol` post-handshake | `TransparentClientHandler.cs`, `ExplicitClientHandler.cs` | Any diagnostics or policy code that reads `Connection.SslProtocol` |
| F12 | **Explicit ClientHello drain loop** — replaced hard-throw single ReadAsync with loop matching transparent handler | `ExplicitClientHandler.cs` | Any change to opaque tunnel ClientHello relay |

### Re-review protocol

1. Run `dotnet test --filter "Http2"` after each related change.
2. Run `dotnet test --filter "Http3|Quic|H3"` after H3/QUIC changes.
3. Manual smoke-test with Chrome DevTools Network panel (`Protocol` column) and `net-export` to confirm no `ERR_HTTP2_PROTOCOL_ERROR` on cold page loads.
4. Run `dotnet test` on the full unit test suite (`Titanium.Web.Proxy.UnitTests`) — ignore the three pre-existing Firefox/HeaderBuilder failures.

---

## Part B — Intentional design (no further code change)

These were flagged as bugs or gaps. On investigation the current behaviour is deliberate. Document the intent so future reviewers do not re-open them.

#### B1-a  Flow-control window applied before overflow error (finding #13)
**Location:** `Http2FlowController.cs` `OnWindowUpdate`  
**Why intentional:** The stream (or connection) is immediately closed by the caller on `overflow=true`. The stale window value is never read again.

#### B1-b  Prefetch uses `CancellationToken.None` (finding #32)
**Location:** `ExplicitClientHandler.cs`, `Http2NegotiationHandler.cs`  
**Why intentional:** A client disconnect must not abort an in-flight TLS handshake to origin mid-way. `AbandonDeferredHttp2Negotiation` / `finally` return or close the prefetch.

#### B1-c  `IgnoreServerCertificateErrors` is a full cert bypass (finding #19)
**Location:** `CertificateHandler.cs`  
**Why intentional:** Explicit opt-in. Default is `false`. Prefer `ServerCertificateValidationCallback` for scoped trust. Documented in `wiki/Security-Considerations.md`. Mark `[Obsolete]` in a future major version.

#### B1-d  Dual-relay teardown waits on flow reservation (finding #22)
**Location:** `Http2Helper.cs`, `Http2FlowController.cs`  
**Why intentional:** Session cancellation unblocks `WaitAsync(ct)` immediately. The 60 s timeout is only a backstop against a peer that never sends WINDOW_UPDATE.

#### B1-e  Compressed relay skips HPACK semantic validation (was B3-b, finding #24)
**Location:** `Http2Helper.Copy.cs` (`useCompressedRelay`)  
**Why intentional:** Verbatim HPACK relay is the Balanced-profile RPS path when interception is off. Strict RFC 9113 §8.3 checks are opt-in via `PolicyFamily.Http2RelayValidation`:
- `Disabled` (Balanced, LegacyCompatible, `new ProxyServer()` default) — verbatim relay, no extra decode.
- `Observe` — decode and log; do not reject; do not dirty `MutationCount`.
- `Enforce` (PublicFacing / `ProxyPolicyModes.AllEnforce`) — decode and GOAWAY on semantic violations.

This is not a bug. Trusting the upstream peer on the no-interception path is the documented performance default.

#### B1-f  Unknown `:scheme` (ws, wss) treated as missing (was B2-c, finding #8)
**Location:** `Http2Helper.Copy.Headers.cs` `MyHeaderListener.Scheme`  
**Why intentional:** RFC 8441 §4 specifies `:scheme: https` or `:scheme: http` for WebSocket-over-HTTP/2, not `wss:` / `ws:`. RST(PROTOCOL_ERROR) for any other value is RFC 9113 §8.3. Clients that send `wss:` are non-compliant; the proxy does not invent a translation.

#### B1-g  RFC 8441 extended CONNECT is WebSocket-only on the H2↔H1 bridge (was B3-e, finding #16)
**Location:** `Http2ToHttp11BridgeHandler`  
**Why intentional:** The bridge translates `:protocol: websocket` to HTTP/1.1 Upgrade. `connect-tcp` (RFC 9298) and other `:protocol` values are not implemented. Advertising ENABLE_CONNECT_PROTOCOL when the origin (or the h1 bridge) can handle WebSocket is functionally correct for that case. Documented in `wiki/Protocol-Support.md`.

---

## Part C — Items implemented in the protocol-gap pass

| ID | What was done |
|----|----------------|
| B2-a | SETTINGS, PING, GOAWAY on a non-zero stream ID → GOAWAY(PROTOCOL_ERROR) in the MITM relay |
| B2-b | HEADERS/DATA pad-length ≥ payload → GOAWAY(PROTOCOL_ERROR); silent `fragmentLength = 0` clamp removed |
| B2-d | `:method`-bearing blocks classified as request headers; missing `:path` on non-CONNECT → RST(PROTOCOL_ERROR) |
| B2-e | SETTINGS_ENABLE_PUSH values other than 0 or 1 → GOAWAY(PROTOCOL_ERROR) |
| B2-f | Missing `:path` on HTTP/3 non-CONNECT → H3_MESSAGE_ERROR (no longer defaulted to `/`) |
| B2-g | Trailing HEADERS on H3 origin streams decoded into `TrailingHeaders` and emitted to the client (gRPC `grpc-status`) |
| B2-h | Origin H3 control stream whose first frame is not SETTINGS → `Http3ConnectionException(MissingSettings)` |
| B2-i | QUIC connection CTS is owned by `HandleQuicConnectionAsync`, not disposed when options-building returns |
| B2-j | All MITM-relay GOAWAY Last-Stream-ID fields use `connectionState.LastClientStreamId` (highest admitted stream). Rapid-reset GOAWAY still uses `ClientResetBudgetLastStreamId` |
| B3-a | QPACK decoder implements RFC 9204 Base-relative and post-base indexes. Static-only (`requiredInsertCount == 0`) path unchanged |
| B3-c | HTTP/1 obs-fold (leading SP/HTAB continuation) rejected as framing — always enforced, no `PolicyMode` |
| B3-d | Async `ServerCertificateValidationCallback` runs via `Task.Run` when not already completed; `IsCompletedSuccessfully` remains the outer fast path |

Header *count/size* numeric caps remain the reserved `PolicyFamily.HeaderLimits` family (already named; not yet wired to every H1 call site). That is a future resource-limit landing, not a protocol bug.

---

## Part D — Pre-existing test failures (not from this work)

These tests fail on the `develop` branch baseline:

| Test | File | Category | Status |
|------|------|----------|--------|
| `FirefoxEnterpriseRoots_HkcuAndTempProfile_DoNotTouchPoliciesJson` | `Titanium.Web.Proxy.UnitTests` | Firefox Windows registry interaction | Pre-existing; platform-specific |
| `HeaderText_SerializesRequestAndResponseStartLines` | `Titanium.Web.Proxy.UnitTests` | `HeaderBuilder.GetBuffer after Return` | Pre-existing; possibly flaky pool reuse |
| `WriteHeadersAsync_WritesAsciiHeaders` | `Titanium.Web.Proxy.UnitTests` | `HeaderBuilder.GetBuffer after Return` | Pre-existing; same root cause |

---

## Part E — Protected paths (do not regress)

When touching these sites, read the in-code comments first. They exist because of measured RPS cost, a past browser `PROTOCOL_ERROR`, or a CVE mitigation.

| Location | Why it must stay |
|----------|------------------|
| `Http2Helper.Copy.cs` compressed-relay `MutationCount` match | Observe-mode validation must not dirty `MutationCount` or the verbatim relay fast path dies |
| GOAWAY stream loop: cancel CTS, do not Dispose | Concurrent DATA/HEADERS can still touch the CTS |
| Defer client WINDOW_UPDATE until after SETTINGS | HttpClient / MSN / Wikipedia treat pre-SETTINGS WINDOW_UPDATE as PROTOCOL_ERROR |
| Rapid-reset GOAWAY uses `ClientResetBudgetLastStreamId`; do not return | CVE-2023-44487: already-admitted streams must drain |
| HPACK decoder resized, never recreated | Recreating discarded dynamic-table entries → `ERR_HTTP2_COMPRESSION_ERROR` |
| Lite H2 finish stays inline (not `Task.Run`) | Measured ~22k streams/s tax when offloaded |
| `CertificateHandler` `IsCompletedSuccessfully` outer guard | `.Wait()` on `Task.CompletedTask` parked a worker on the handshake path |
| H3 `FlushAsync` before FIN | Darwin MsQuic; skip-Flush dropped reverse RPS |
| H3 drain-to-FIN before Dispose | Otherwise MsQuic RSTs and poisons the pool |
| QPACK Base math only when `requiredInsertCount != 0` | Default static-only hot path must stay allocation-free |
| Queue GOAWAY/RST/DATA rather than a direct locked write | Direct write raced MITM HEADERS; Chrome saw DATA on idle streams |

---

*Last updated: 2026-09-16 — all 35 findings closed (implemented or intentional).*
