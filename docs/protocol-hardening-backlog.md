# Protocol Hardening Backlog

> **Context** — September 2026 principal-architect review of the core TWP NuGet package
> (HTTP/2, HTTP/3, HPACK, QPACK, flow-control, TLS, relay arms).
> Thirty-five findings were triaged. Twelve were fixed immediately (commit `d11d03d9`).
> This document tracks the remaining twenty-three and provides the re-review checklist
> for the twelve that were already fixed.

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

1. Run `dotnet test --filter "Http2"` (110 integration tests) after each related change.
2. Run `dotnet test --filter "Http3|Quic|H3"` after H3/QUIC changes.
3. Manual smoke-test with Chrome DevTools Network panel (`Protocol` column) and `net-export` to confirm no `ERR_HTTP2_PROTOCOL_ERROR` on cold page loads.
4. Run `dotnet test` on the full unit test suite (`Titanium.Web.Proxy.UnitTests`) — ignore the three pre-existing Firefox/HeaderBuilder failures.

---

## Part B — Outstanding items (not fixed)

Items are grouped by: **Intentional design**, **Future hardening**, and **Needs architectural decision**.

---

### B1 — Intentional design choices (no code change expected)

These were flagged but on investigation the current behaviour is deliberate. Document the intent so future reviewers do not re-open them unnecessarily.

#### B1-a  Flow-control window applied before overflow error (finding #13)
**Location:** `Http2FlowController.cs` `OnWindowUpdate` lines 111–134  
**Behaviour:** When a WINDOW_UPDATE would overflow 2³¹−1 the window is incremented and the caller is returned `overflow=true`. RFC 9113 §6.9.1 says the window MUST NOT exceed the limit, but it does not specify the window state on termination.  
**Why intentional:** The stream (or connection) is immediately closed by the caller on `overflow=true`. The stale window value is never read again. The alternative — not incrementing — does not change correctness because the object is about to be discarded. An "atomic max then error" guard would add complexity for zero observable benefit.  
**Action required:** None. Update this comment if behaviour changes.

#### B1-b  Prefetch uses `CancellationToken.None` (finding #32)
**Location:** `ExplicitClientHandler.cs` ~line 344, `Http2NegotiationHandler.cs` ~line 110  
**Behaviour:** TCP connection prefetch runs with `CancellationToken.None` so a client disconnect does not abort an in-flight TLS handshake to origin mid-way, leaving the origin with a half-open connection.  
**Why intentional:** Documented in comments. `AbandonDeferredHttp2Negotiation` / `finally` blocks ensure the prefetch is properly returned to the pool or closed when the session ends. This is the established pattern for connection prefetching.  
**Action required:** None.

#### B1-c  `IgnoreServerCertificateErrors` is a full cert bypass (finding #19)
**Location:** `CertificateHandler.cs` lines 37–40  
**Behaviour:** When `IgnoreServerCertificateErrors=true` all `SslPolicyErrors` are accepted including name mismatch, expired, and untrusted CA.  
**Why intentional:** This is an explicit opt-in property. Default is `false`. Production deployments that set it to `true` are accepting the security trade-off. The `ServerCertificateValidationCallback` provides scoped exceptions for certificates that should be individually trusted.  
**Action required:** None, but mark this property `[Obsolete("Use ServerCertificateValidationCallback for scoped trust. Setting this to true accepts all certs including name mismatches and expired certificates.")]` in a future major version.

#### B1-d  Dual-relay teardown waits on flow reservation (finding #22)
**Location:** `Http2Helper.cs`, `Http2FlowController.cs`  
**Behaviour:** `ReserveAsync` has a 60-second `WaitAsync` guard. On disconnect the session's `CancellationToken` is cancelled first, which unblocks `WaitAsync(ct)` immediately. The 60-second path is only reached on a live connection where the peer has stopped sending WINDOW_UPDATE — which is a peer violation, not a proxy defect.  
**Why intentional:** The CancellationToken threading is correct. The 60s timeout is the backstop against a misbehaving origin that never sends WINDOW_UPDATE. Reducing it would cause legitimate slow origins to be disconnected.  
**Action required:** None.

---

### B2 — Future hardening (code changes needed, low-risk window)

These are real correctness gaps. None causes data loss or protocol errors in current production traffic, but they should be fixed when the relevant subsystem is next touched.

#### B2-a  SETTINGS/PING/GOAWAY on non-zero stream not rejected in MITM relay (finding #5)
**Location:** `Http2Helper.Copy.cs` `CopyHttp2FrameAsync`  
**RFC requirement:** RFC 9113 §6.5 / §6.7 / §6.8: SETTINGS, PING, GOAWAY MUST use stream 0.  
**Current behaviour:** MITM relay only validates stream 0 for DATA/HEADERS/RST/PRIORITY. `Http2OriginConnection` already checks this; the MITM relay does not.  
**Risk:** A malicious client could send SETTINGS on stream 1. Currently relayed without error.  
**Fix:** In `CopyHttp2FrameAsync` add stream-0 guards for `FrameType.Settings`, `FrameType.Ping`, and `FrameType.GoAway`. Emit GOAWAY(PROTOCOL_ERROR) if violated.  
**Test to add:** Unit test sending SETTINGS on stream ID 5, asserting GOAWAY.

#### B2-b  HEADERS/DATA padding overflow is clamped, not `PROTOCOL_ERROR` (finding #4)
**Location:** `Http2Helper.Copy.cs` ~line 830, `Http2OriginConnection.cs` ~line 1375  
**RFC requirement:** RFC 9113 §6.2 / §6.3: pad-length ≥ payload length is `PROTOCOL_ERROR`.  
**Current behaviour:** `fragmentLength = 0`, processing continues.  
**Risk:** A crafted frame can inject empty header blocks or hide bytes in padding.  
**Fix:** If `1 + padLength > frameLength`, send GOAWAY(PROTOCOL_ERROR) and close.  
**Note:** Real browsers / servers never send malformed padding. Safe to add.

#### B2-c  Unknown `:scheme` (ws, wss, custom) treated as missing (finding #8)
**Location:** `Http2Helper.Copy.Headers.cs` `MyHeaderListener.Scheme`  
**Behaviour:** `Scheme` only sets `http` or `https`; everything else produces `""` → "missing :scheme" rejection.  
**Impact:** WebSocket-over-h2 (`wss:`) from a non-RFC-8441 client fails if scheme is not `https`.  
**Fix:** Store the raw scheme string. In the scheme-required check, treat any non-empty scheme as valid. Re-encode the original scheme in `Http2Helper.Hpack.cs` instead of forcing `http`/`https` from `IsHttps`.

#### B2-d  Missing/empty `:path` on non-CONNECT classified as trailer (finding #7)
**Location:** `Http2Helper.Copy.Headers.cs` `isMainHeaders` predicate  
**Behaviour:** `isMainHeaders = (method && path) || (connect && authority)`. GET with empty `:path` is treated as trailers; "trailers before headers" is logged but no RST is sent.  
**Fix:** If `:method` is present, treat the block as request headers regardless. RST(PROTOCOL_ERROR) for missing/empty `:path` on non-CONNECT, non-OPTIONS-* requests.  
**Poor-client note:** Some embedded/IoT h2 clients emit empty `:path` for root requests. Consider a configurable grace mode (`AllowMissingPath`) defaulting to reject.

#### B2-e  `SETTINGS_ENABLE_PUSH` value > 1 not rejected (finding #14)
**Location:** `Http2Helper.Copy.cs` SETTINGS parsing  
**RFC requirement:** RFC 9113 §6.5.2: `SETTINGS_ENABLE_PUSH` MUST be 0 or 1; other values are `PROTOCOL_ERROR`.  
**Fix:** Same guard as `ENABLE_CONNECT_PROTOCOL`: `value > 1 → GOAWAY(PROTOCOL_ERROR)`. One-line fix.

#### B2-f  H3 pseudo-header validation incomplete (finding #9)
**Location:** `Http3RequestStream.cs` ~lines 95–110  
**Missing checks vs RFC 9114 §4.3.1:** required set enforcement, duplicate pseudo-header rejection, unknown `:…` field rejection, pseudo-after-regular ordering. Missing `:path` is silently defaulted to `"/"`.  
**Fix:** Apply the same validation rules as `MyHeaderListener` in the H2 path. Do not invent `:path`; reject as `H3_MESSAGE_ERROR`.

#### B2-g  H3 trailers silently dropped (finding #11)
**Location:** `Http3OriginBridge.Quic.cs` ~lines 281, 340, 695  
**Impact:** gRPC `grpc-status`, `grpc-message`, and any trailing checksum headers are lost for H3 origins.  
**Fix:** Decode the second HEADERS block as trailers, store on `TrailingHeaders`, emit to the client side (H2 trailer HEADERS or H1 chunked trailers). The H2 arm (`Http2OriginConnection`) already handles trailers correctly — use it as the reference implementation.

#### B2-h  H3 control stream: SETTINGS / GOAWAY errors swallowed (finding #21)
**Location:** `Http3OriginClientSession.cs` ~lines 164–175  
**Behaviour:** First frame not SETTINGS → `return settings` (null), no connection error signalled. RFC 9114 §6.2.2: `H3_MISSING_SETTINGS`.  
**Fix:** Close the origin QUIC connection with the appropriate H3 error code and surface via `Http3ConnectionException`.

#### B2-i  QUIC auth CTS lifecycle (finding #10)
**Location:** `QuicClientHandler.cs` ~lines 144–145, 160, 178  
**Issue:** `using var connectionCts` / `using var linked` are disposed when `GetQuicServerConnectionOptionsAsync` returns; `HandleQuicConnectionAsync` may call `Reject()` on the disposed CTS → `ObjectDisposedException`.  
**Fix:** Move connection-lifetime CTS ownership to the connection scope, linked to the listener shutdown token.

#### B2-j  GOAWAY Last-Stream-ID should be highest-processed (finding #28)
**Location:** `Http2Helper.Send.cs` + ~20 callsites in `Http2Helper.Copy.cs`  
**Behaviour:** Most callsites pass `streamId` (the offending frame's ID) or 0. RFC 9113 §6.8: Last-Stream-ID is the highest stream the sender *has processed*, so the peer knows which streams to retry.  
**Fix:** Thread `connectionState.LastClientStreamId` (or equivalent highest-processed value) through `SendGoAwayAsync` callers. Browsers tolerate the current behaviour (they retry conservatively), so this is correctness-over-compatibility.  
**Note:** This is a multi-callsite refactor. Introduce `connectionState.LastProcessedClientStreamId` first, then migrate callers one-by-one.

---

### B3 — Needs architectural decision

These require a team/architectural call before coding. They cannot be safely fixed with a local patch.

#### B3-a  QPACK dynamic table: Base and post-base indexes ignored  *(partially hardened — see below)*
**Location:** `QpackDecoder.cs` ~lines 99–118; `QpackEncoder.cs` ~lines 364–387  
**Issue:** Delta Base is parsed but discarded (`out _`). Dynamic indexed fields use `TryGetByAbsoluteIndex(wireIndex)` directly — but RFC 9204 §4.5.2 requires `absoluteIndex = Base − 1 − wireIndex` (relative), and §4.5.5 post-base fields require `absoluteIndex = Base + wireIndex`. Both conversions are missing, so any session where a peer actually inserts rows into the dynamic table and uses relative/post-base wire references would get the wrong header value silently.  
**How it was silently dangerous:** The existing guard only fires when `context == null` (dynamic table disabled). When `EnableQpackDynamicTable = true` (context != null) and a peer sends `requiredInsertCount > 0`, the decoder proceeded with incorrect absolute-index lookups — potentially returning a completely different header name/value or throwing a misleading not-found exception.  
**Partially fixed (2026-09-15):** Added a fail-fast guard in `QpackDecoder.DecodeCore` that throws `Http3ConnectionException(QpackDecompressionFailed)` with a clear message whenever `requiredInsertCount != 0 && context != null`. This prevents silent header corruption at the cost of a visible connection error for anyone who enables the dynamic table and hits a peer that fills it. This is strictly safer than the previous silent mis-decode.  
**Remaining work:** Full RFC 9204 §4.5 Base-relative decoding:
1. Parse S-bit + DeltaBase (already parsed, currently discarded via `_ = sBit; _ = deltaBase`).
2. Compute `Base = ric − (sBit ? deltaBase+1 : deltaBase)`.
3. Resolve relative dynamic refs: `abs = Base − 1 − wireIndex`.
4. Resolve post-base dynamic refs: `abs = Base + wireIndex`.
5. Fix `QpackEncoder` to compute and write the correct Required Insert Count + S/DeltaBase preamble when encoding with a non-empty dynamic table.
6. Add unit tests with real relative/post-base encoded blocks.  
**Decision still needed:** (a) Complete the above implementation per RFC 9204, or (b) throw a `NotSupportedException` at startup when `EnableQpackDynamicTable = true` to document the incompleteness publicly until (a) is done.  
**Priority upgrade:** Moved to B2 (future hardening) — the silent corruption is fixed; the feature is functionally incomplete and must be properly implemented before `EnableQpackDynamicTable = true` is safe to use in production.

#### B3-b  Compressed relay skips header-block validation (finding #24)
**Location:** `Http2Helper.Copy.cs` ~lines 780–848  
**Issue:** When HPACK decryption is off (`!suppressConnectionFrameRelay`), HEADERS frames are relayed without semantic validation (duplicate pseudo-headers, connection-specific fields, empty names). HPACK table sync relies on `NoOpHeaderListener` in some paths — needs verification.  
**Decision needed:** Add a decode-for-validation pass even for relay (high-correctness) vs. trust the upstream peer (current, high-performance). The performance cost on relay (no decryption) is significant; consider a sampling/debug mode.

#### B3-c  HTTP/1 header parser: no count or size cap (finding #17)
**Location:** `HeaderParser.cs` ~lines 101–118  
**Issue:** No maximum header count or total byte limit. `obs-fold` (leading whitespace continuation) not rejected per RFC 9112 §5.2. Empty header names not RST'd at the HTTP/2 layer.  
**Decision needed:** What limits to apply and whether exceeding them is a 400 or a connection close. Must not break large-header API use-cases (e.g., very long JWT Bearer tokens, cookie jars).

#### B3-d  `ValidateServerCertificate` sync-blocks async callback (finding #33)
**Location:** `CertificateHandler.cs` ~line 31  
**Issue:** `ServerCertificateValidationCallback` is awaited with `GetAwaiter().GetResult()` from inside `SslStream.RemoteCertificateValidationCallback` (a sync delegate). If the user callback posts back to the calling thread this deadlocks.  
**Decision needed:** The fix requires making the TLS validation callback async, which touches the `SslStream` integration surface. Microsoft's `SslStream` does not support async cert validation — the only correct fix is to pre-fetch the certificate decision before the TLS handshake (changing the API surface) or use `SslClientAuthenticationOptions.RemoteCertificateValidationCallback` with a pre-resolved result.

#### B3-e  RFC 8441 (extended CONNECT) in MITM bridge: proxy advertises without forwarding (finding #16 — partial)
**Resolved portion:** False injection removed in `d11d03d9`.  
**Remaining concern:** In the MITM path with h2↔h1.1 bridge (`Http2ToHttp11BridgeHandler`), `ENABLE_CONNECT_PROTOCOL=1` is still advertised to the client unconditionally when `enableRfc8441=true`. The bridge *does* translate extended CONNECT to h1.1 WebSocket Upgrade, so this is functionally correct for the WebSocket case. However, for other `Upgrade` protocols (e.g., `connect-tcp` from RFC 9298) the bridge silently fails.  
**Decision needed:** (a) Enumerate supported `:protocol` values and only advertise when the bridge handles them, or (b) document the current WebSocket-only guarantee explicitly.

---

## Part C — Pre-existing test failures (not from this session)

These tests fail on the `develop` branch baseline before any of the changes in this session:

| Test | File | Category | Status |
|------|------|----------|--------|
| `FirefoxEnterpriseRoots_HkcuAndTempProfile_DoNotTouchPoliciesJson` | `Titanium.Web.Proxy.UnitTests` | Firefox Windows registry interaction | Pre-existing; platform-specific |
| `HeaderText_SerializesRequestAndResponseStartLines` | `Titanium.Web.Proxy.UnitTests` | `HeaderBuilder.GetBuffer after Return` | Pre-existing; possibly flaky pool reuse |
| `WriteHeadersAsync_WritesAsciiHeaders` | `Titanium.Web.Proxy.UnitTests` | `HeaderBuilder.GetBuffer after Return` | Pre-existing; same root cause |

These should be investigated independently and are outside the scope of the protocol hardening work above.

---

## Part D — Prioritised fix order recommendation

```
Priority 1 (next sprint — correctness, low regression risk):
  B2-e  SETTINGS_ENABLE_PUSH value validation  (1-line fix)
  B2-b  Padding overflow PROTOCOL_ERROR        (2-line fix per site)
  B2-a  SETTINGS/PING/GOAWAY stream-0 guard    (small, isolated)

Priority 2 (next sprint — correctness, moderate complexity):
  B2-j  GOAWAY Last-Stream-ID                  (multi-callsite refactor)
  B2-g  H3 trailers (gRPC impact)              (H3 arm change)
  B2-f  H3 pseudo-header validation            (port H2 logic to H3)

Priority 3 (next quarter — correctness, high complexity or RFC edge cases):
  B2-c  Unknown :scheme pass-through
  B2-d  Missing :path RST
  B2-h  H3 control stream error propagation
  B2-i  QUIC auth CTS lifecycle

Priority 4 (needs architectural decision before starting):
  B3-a  QPACK dynamic table full implementation  ← fail-fast guard added; need RFC 9204 Base decoding
  B3-b  Compressed relay validation
  B3-c  H1 header parser limits
  B3-d  Async cert validation
  B3-e  RFC 8441 protocol enumeration

Intentional design (no change unless requirements change):
  B1-a  Flow-control window on overflow
  B1-b  Prefetch CancellationToken.None
  B1-c  IgnoreServerCertificateErrors
  B1-d  Dual-relay teardown CancellationToken threading
```

---

*Last updated: 2026-09-15 — principal architect review, commit `d11d03d9`; QPACK dynamic-table fail-fast guard added (QpackDecoder.cs)*
