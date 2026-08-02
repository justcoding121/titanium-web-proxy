# Migration guide: 4.x → 5.0

Version 5.0 bundles a large security- and correctness-hardening pass (RFC-compliance fixes, resource
budgets, and defense-in-depth limits across every protocol the proxy speaks). Rather than stage these
across an interim 6.0, all of them ship together in 5.0.0, since most of the individually-breaking
pieces are small and independently justified but few applications will be affected by every one of
them at once.

This page enumerates **every breaking or observable-behavior change** introduced since 4.x, with the
rationale and the remedy if the new default does not fit your deployment. If you are upgrading from
before 4.0, also read
[Breaking changes: unified logging and timing](Home#breaking-changes-unified-logging-and-timing) on
the Home page first.

> **Quick triage:** if you just want the pre-5.0 posture back for TLS and the observe/enforce-capable
> limits in one step, set `proxyServer.Profile = ProxyProfile.LegacyCompatible;` before `Start()`. It
> does not restore every item below (some, like the certificate store relocation and the framing
> fixes, are not modes you can dial back), but it covers the two broadest ones — see
> [Policy profiles](#policy-profiles-and-observeenforce-modes-additive) below.

## Contents

- [TLS: legacy protocols now require explicit opt-in](#tls-legacy-protocols-now-require-explicit-opt-in)
- [Certificate store relocated to a per-user protected folder](#certificate-store-relocated-to-a-per-user-protected-folder)
- [Ambiguous HTTP/1 framing is now rejected](#ambiguous-http1-framing-is-now-rejected)
- [Cumulative body budgets are now enforced end-to-end](#cumulative-body-budgets-are-now-enforced-end-to-end)
- [WebSocket protocol violations now close the connection](#websocket-protocol-violations-now-close-the-connection)
- [HTTP/2 CONTINUATION and reset abuse budgets](#http2-continuation-and-reset-abuse-budgets)
- [HTTP/3 and QPACK behavior changes (experimental)](#http3-and-qpack-behavior-changes-experimental)
- [`ProxyAuthorizationException.Headers` credentials are redacted](#proxyauthorizationexceptionheaders-credentials-are-redacted)
- [Stacked `Content-Encoding` is now actually decoded](#stacked-content-encoding-is-now-actually-decoded)
- [`HasBody` correctness fix for 204/304/1xx/CONNECT](#hasbody-correctness-fix-for-204304xxxconnect)
- [Oversized/overflowing chunk sizes are now rejected](#oversizedoverflowing-chunk-sizes-are-now-rejected)
- [Chunked HTTP/1.0 requests are now downgraded, not forwarded as-is](#chunked-http10-requests-are-now-downgraded-not-forwarded-as-is)
- [New: client header read deadline](#new-client-header-read-deadline)
- [New: global/per-endpoint connection admission limits (opt-in)](#new-globalper-endpoint-connection-admission-limits-opt-in)
- [New: outbound private-network destination blocking (opt-in)](#new-outbound-private-network-destination-blocking-opt-in)
- [Policy profiles and observe/enforce modes (additive)](#policy-profiles-and-observeenforce-modes-additive)
- [New: Happy Eyeballs (RFC 8305) address racing](#new-happy-eyeballs-rfc-8305-address-racing)
- [Connection IDs are monotonic `long` counters, not `Guid`](#connection-ids-are-monotonic-long-counters-not-guid)
- [Internal-only changes (no action needed)](#internal-only-changes-no-action-needed)

---

## TLS: legacy protocols now require explicit opt-in

**Before:** `ProxyServer.SupportedSslProtocols` defaulted to
`Ssl3 | Tls | Tls11 | Tls12 | Tls13` — every protocol back to SSL 3.0.

**Now:** it defaults to `Tls12 | Tls13` only.

**Why:** SSL 3.0 (POODLE) and TLS 1.0/1.1 are deprecated by the IETF (RFC 8996) and by every major
browser; leaving them enabled by default on the client- and server-facing TLS surface of a MITM proxy
is a needless downgrade-attack surface.

**Remedy:** if you must interoperate with a legacy client or origin that only speaks TLS 1.0/1.1, opt
back in explicitly:

```csharp
#pragma warning disable SYSLIB0039 // deliberate legacy-TLS opt-in
proxyServer.SupportedSslProtocols = SslProtocols.Tls | SslProtocols.Tls11 |
                                     SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore SYSLIB0039
```

or select the bundled profile that does this for you (also relaxes a couple of the observe/enforce
limits below to `Observe`):

```csharp
proxyServer.Profile = ProxyProfile.LegacyCompatible;
```

SSL 3.0 itself is not offered by any profile; if you genuinely need it, set
`SupportedSslProtocols` directly as shown above (add `SslProtocols.Ssl3`).

---

## Certificate store relocated to a per-user protected folder

**Before:** the root CA and generated leaf-certificate cache lived next to the hosting assembly on
Windows desktop (`Path.GetDirectoryName(Assembly.Location)` / `AppContext.BaseDirectory`), or under
`%AppData%`/`~/.local/share` (UWP/Linux/Mac) with no dedicated subfolder.

**Now:** both live under a per-user, non-world-writable location in a dedicated
`Titanium.Web.Proxy` subfolder — `%LocalAppData%\Titanium.Web.Proxy` on Windows,
`~/.local/share/Titanium.Web.Proxy` (or platform equivalent of
`Environment.SpecialFolder.ApplicationData`) on Linux/macOS, with permissions tightened to `0700` on
Unix on a best-effort basis.

**Why:** a certificate store living next to the application binary (often world-readable, sometimes
world-writable depending on install location) lets any other local user or process on the machine
read the CA private key, or on some deployments replace the leaf-certificate cache outright.

**Remedy:** this is a one-time transition per machine, handled mostly automatically:

- The proxy logs a one-time warning if it finds a root certificate from a pre-5.0 install still
  sitting in the old location. That old root is not deleted automatically — remove it yourself (and
  untrust it from your OS/browser certificate store) once you've re-trusted the new one.
- Call `CertificateManager.EnsureRootCertificate(...)` as usual after upgrading; it creates a fresh
  root (or reuses an existing one) in the new location and you re-trust it exactly as you did the
  first time you set the proxy up.
- If your deployment pins or backs up the certificate path (Docker volumes, provisioning scripts,
  documentation), update those paths to the new location.

---

## Ambiguous HTTP/1 framing is now rejected

**Before:** a request or response with, e.g., both `Content-Length` and `Transfer-Encoding`, multiple
conflicting `Content-Length` values, or an unsupported transfer-coding, could be parsed leniently and
forwarded — the classic request-smuggling precondition.

**Now:** every wire-parsed HTTP/1 message is validated against RFC 9112 §6.3 before it is ever handed
to a `BeforeRequest`/`BeforeResponse` callback or forwarded upstream/downstream. A request that
violates the framing rules gets a `400` response (ambiguous framing) or `501` (unsupported
transfer-coding) instead of being forwarded; a response-side violation reports the failure through
`ProxyServer.Logging` and closes the connection rather than risk desynchronizing a pooled connection.

**Why:** this closes a request/response-smuggling class of bug — silently forwarding an ambiguous
message is exactly the behavior smuggling attacks rely on.

**Remedy:** if you knowingly proxy traffic to/from a non-conformant legacy peer that depends on the
old lenient behavior, you can disable this specific check (framing validation is the one policy
family that has no `Observe` mode — it is enforce-only or off):

```csharp
proxyServer.PolicyModes = proxyServer.PolicyModes.WithAllowAmbiguousFramingEnabled();
```

Treat this as a deliberate, audited exception, not a default — it reopens the smuggling
precondition this change closes.

---

## Cumulative body budgets are now enforced end-to-end

**Before:** `ProxyServer.MaxBufferedBodyBytes` existed but was not consulted by every buffering path.
In particular, whole-body reads via `GetRequestBody()`/`GetResponseBody()` on HTTP/1, and client-facing
DATA-frame buffering on HTTP/2, accumulated into memory with **no cumulative cap of their own** —
only the streaming/chunk-by-chunk paths (`OnRequestBodyWrite`/`OnResponseBodyWrite`) and the HTTP/2
origin-bridge pipe were already bounded.

**Now:** every whole-body buffering path on HTTP/1, HTTP/2, and HTTP/3 (experimental) is wrapped by a
`BoundedWriteStream` that enforces `MaxBufferedBodyBytes` cumulatively as bytes arrive, not just at the
end. A request-side breach returns `413 Payload Too Large` before the request reaches the origin; a
response-side breach closes the connection rather than forwarding a truncated body.

**Why:** an unbounded whole-body read is a memory-exhaustion vector — a malicious or merely very large
upload/download previously had no ceiling on some paths regardless of the configured limit.

**Remedy:** if your application legitimately handles bodies larger than the default 4 MiB via
`GetRequestBody()`/`GetResponseBody()`, raise the limit:

```csharp
proxyServer.MaxBufferedBodyBytes = 64 * 1024 * 1024; // 64 MiB
```

or prefer the streaming APIs (`OnRequestBodyWrite`/`OnResponseBodyWrite`, see
[Streaming Bodies](Streaming-Bodies)) which process a body in bounded-size pieces regardless of total
length. If you need to migrate gradually, record breaches without rejecting anything by dropping the
`BodyBudget` policy family to `Observe`:

```csharp
proxyServer.PolicyModes = proxyServer.PolicyModes.With(PolicyFamily.BodyBudget, PolicyMode.Observe);
```

---

## WebSocket protocol violations now close the connection

**Before:** a reserved/undefined WebSocket opcode, or a frame whose declared payload length exceeded
`MaxWebSocketFramePayloadBytes`, could reach the point of being buffered before any rejection, and the
failure mode on rejection was an abrupt, non-conformant connection teardown rather than a proper
Close handshake.

**Now:** the declared payload length (including the 64-bit extended length, its reserved bit, and the
`int.MaxValue` ceiling) is validated **before** any payload byte is buffered, and any protocol
violation — reserved opcode, oversized declared length, RSV bits without a negotiated extension —
triggers an RFC 6455-compliant Close handshake (status `1002 Protocol Error` or `1009 Message Too
Big` as appropriate) sent to both sides before the TCP connections are torn down.

**Why:** rejecting *after* buffering a declared-oversized payload defeats the point of the limit; an
abrupt non-conformant teardown can also confuse WebSocket client libraries that expect a Close frame.

**Remedy:** none needed for typical use — this only changes behavior for frames that were already
protocol violations or already over your configured limit. If your `MaxWebSocketFramePayloadBytes` was
sized for the old post-buffering check, no change is needed; the new pre-buffer check uses the same
property and the same limit.

---

## HTTP/2 CONTINUATION and reset abuse budgets

**Before:** an HTTP/2 peer could open a header block with an unbounded sequence of (including
zero-length) CONTINUATION frames, and could send an unbounded number of incomplete-stream resets
(the "Rapid Reset" pattern), with no dedicated budget guarding either. The concurrent-stream limit the
proxy advertised in `SETTINGS_MAX_CONCURRENT_STREAMS` and the limit it actually enforced were also two
independently-maintained values that could drift apart.

**Now:** an open header block is bounded by both a frame-count and a wall-clock
(`ProxyResourceLimits.MaxOpenHeaderBlockDuration`) limit; peer-initiated incomplete resets are capped
by a budget; and the advertised/enforced concurrent-stream limit is a single consolidated value.
Breaching any of these sends `GOAWAY`/`RST_STREAM` with `ENHANCE_YOUR_CALM`.

**Why:** unbounded CONTINUATION sequences and rapid resets are known HTTP/2 resource-exhaustion attack
patterns (CVE-2023-44487 and related CONTINUATION-flood advisories).

**Remedy:** none needed for conformant peers — these budgets are generous defaults sized well above
any legitimate header block or reset pattern. If you have a peer that legitimately needs a larger
header-block assembly window, raise `ProxyResourceLimits.MaxOpenHeaderBlockDuration` via
`ResolvedSessionPolicy`, or drop the `Http2AbuseBudget` policy family to `Observe` to measure before
enforcing:

```csharp
proxyServer.PolicyModes = proxyServer.PolicyModes.With(PolicyFamily.Http2AbuseBudget, PolicyMode.Observe);
```

---

## HTTP/3 and QPACK behavior changes (experimental)

HTTP/3 remains gated behind `ProxyServer.EnableHttp3 = true` and `[Experimental("TWP001")]`, so these
only affect you if you've already opted in:

- **QPACK never blocks a stream:** the proxy advertises `SETTINGS_QPACK_BLOCKED_STREAMS = 0` (already
  the case, but the wiki previously described the opposite — see the corrected
  [HTTP/3](HTTP-3#dynamic-table-mode-opt-in) page). A field section whose Required Insert Count is not
  yet satisfied now immediately raises `QPACK_DECOMPRESSION_FAILED` and aborts the connection, rather
  than a peer being able to make a request stream wait indefinitely.
- **Critical-stream closure is fatal:** an unexpected close of a critical unidirectional stream (e.g.
  the control stream) now raises `H3_CLOSED_CRITICAL_STREAM` per RFC 9114, instead of being tolerated.
- **First control-stream frame must be SETTINGS:** enforced as `H3_MISSING_SETTINGS` otherwise.
- **Cumulative body budgets** apply to H3 request/response bodies the same way as H1/H2 (see
  [above](#cumulative-body-budgets-are-now-enforced-end-to-end)); a breach maps to
  `Http3ErrorCode.ExcessiveLoad`.
- **Background task ownership:** all per-connection background tasks (QPACK encoder/decoder
  readers/writers, unidirectional stream handlers) are now tracked and joined on teardown, so a
  connection close no longer leaves orphaned tasks or produces unobserved-exception noise in your
  logs.
- **Auto-mode SVCB is background-only:** `EnableHttpsSvcbDnsDiscovery` no longer awaits DNS on
  CONNECT/request paths. A cache miss queues coalesced background discovery and the current
  connection continues over H2/H1; later connections may upgrade once the capability cache is warm
  (or after `Alt-Svc`).
- **`DnsServerEndPoint` default:** previously hard-coded to a public resolver / loopback in docs.
  Now defaults to the first usable OS-configured plain-UDP DNS server (best-effort; does not honor
  NRPT/DoH/VPN split-DNS). When none is discoverable, proactive discovery is skipped — there is no
  silent fallback to a public third-party resolver.
- **HTTP/2 capability cache TTL:** in-memory origin ALPN capability results now live for 30 minutes
  (was 5). Stale positives are detected after client ALPN commitment: with
  `AllowHttpProtocolTranslation` the tunnel repairs via the H2→H1.1 bridge; without it the tunnel
  fails closed rather than writing HTTP/2 frames to a non-HTTP/2 origin.

**Remedy:** none needed unless you were relying on lenient QPACK blocking behavior, which was never a
documented or intentional feature. If you depended on first-CONNECT H3 via synchronous SVCB, keep
`EnableHttpsSvcbDnsDiscovery = true` and expect H3 on the second connection (or from `Alt-Svc`), or
force `UpstreamHttpProtocol.Http3` when fail-closed H3 is required.

---

## `ProxyAuthorizationException.Headers` credentials are redacted

**Before:** `ProxyAuthorizationException.Headers` exposed the raw `Authorization`/
`Proxy-Authorization` header value, including the plaintext credential, to anything that caught or
logged the exception.

**Now:** those two header values are replaced with the literal string `[REDACTED]` before the
exception is constructed. Every other header is unaffected.

**Why:** this exception is commonly logged or reported to crash-reporting infrastructure; doing so
previously leaked live proxy credentials into logs by default.

**Remedy:** if you have code that specifically needs the *real* credential value from this exception
path (e.g. to drive your own retry-with-different-credentials logic), capture the credential in your
own authentication callback (`ProxyBasicAuthenticateFunc`, etc.) before the exception is raised,
rather than reading it back out of `Headers`. There is no opt-out flag for the redaction itself.

---

## Stacked `Content-Encoding` is now actually decoded

**Before:** on HTTP/1 and HTTP/2, a response with multiple comma-separated `Content-Encoding` values
(e.g. `Content-Encoding: gzip, br`) was not recognized by the single-value decoder lookup, so
decompression was **silently skipped entirely** — any code reading the body via
`GetResponseBodyAsString()`/`OnResponseBodyWrite` saw the still-compressed bytes. HTTP/3 already
decoded these correctly.

**Now:** H1/H2 build the same reverse-order decompression chain HTTP/3 always used, so a stacked
`Content-Encoding` is fully decoded before your handler sees the body.

**Why:** this was a plain correctness bug, not a deliberate limitation — RFC 9110 §8.4 explicitly
allows a comma-separated list, and the proxy already had the chaining logic for H3.

**Remedy:** if any of your handlers were compensating for this bug (e.g. re-attempting decompression
themselves, or specifically special-casing garbled bytes from a stacked-encoding origin), remove that
workaround — you'll now receive already-decoded bytes.

---

## `HasBody` correctness fix for 204/304/1xx/CONNECT

**Before:** the "does this response have a body" check excluded `HEAD` up front, but for other cases
fell through to `!KeepAlive`, which incorrectly reported `true` for a `204`/`304`/1xx response sent
with `Connection: close`, and never excluded a successful (`2xx`) `CONNECT` tunnel response at all.

**Now:** the RFC 9110 §6.4.1 exclusions (1xx, 204, 304, `HEAD` requests, and 2xx responses to
`CONNECT`) are checked unconditionally, before any `Connection`-header-based fallback.

**Why:** treating one of these responses as having a body could cause the proxy to wait for/attempt to
read body bytes that will never arrive, or to forward a phantom body to the client.

**Remedy:** none needed — this only affects internal framing decisions; no public API shape changed.
If a handler was working around the old bug (e.g. assuming a body was always present after a `CONNECT`
tunnel), remove that workaround.

---

## Oversized/overflowing chunk sizes are now rejected

**Before:** the `chunk-size` line of a chunked body was parsed without an upper bound, so a
maliciously large hex value (e.g. one that overflows a signed integer) could decode to a small or
negative sentinel and desynchronize the parser.

**Now:** chunk sizes are parsed with a bounded, overflow-safe parser and rejected outright above
`ProxyLimits.DefaultMaxChunkSizeBytes` (1 GiB) — far larger than any legitimate chunk, but small enough
to reject the attack pattern.

**Remedy:** none needed for conformant traffic.

---

## Chunked HTTP/1.0 requests are now downgraded, not forwarded as-is

**Before:** a chunked-encoded request arriving over HTTP/1.0 syntax could be forwarded upstream as-is,
even to an origin that does not support chunked framing (chunked transfer coding requires HTTP/1.1+).

**Now:** such a request is fully buffered (subject to the same `MaxBufferedBodyBytes` budget described
[above](#cumulative-body-budgets-are-now-enforced-end-to-end)) and re-framed with `Content-Length`
before being forwarded to an HTTP/1.0-only origin.

**Why:** forwarding chunked framing to a peer that doesn't support it is itself a framing hazard, not
just an interop nicety.

**Remedy:** none needed for typical use. If you serve very large chunked bodies from HTTP/1.0 clients
to HTTP/1.0-only origins, make sure `MaxBufferedBodyBytes` is sized for the bodies you expect — the
downgrade needs the whole body in memory.

---

## New: client header read deadline

Reading the client's request line and headers is now subject to a deadline
(`ProxyServer.ClientHeaderTimeoutSeconds`), attributed as `ProxyTimeoutKind.ClientHeader` when it
fires. Previously, a client that connected and then sent its request line/headers arbitrarily slowly
(a "slow-loris" pattern) could hold a connection and its resources open indefinitely.

**Remedy:** the default is generous for real clients; if you have an unusual client that legitimately
drip-feeds headers slowly, raise `ClientHeaderTimeoutSeconds`.

---

## New: global/per-endpoint connection admission limits (opt-in)

`ProxyServer.MaxConcurrentClientConnections` and a per-endpoint equivalent are new and **default to
`null` (unlimited)** — this preserves the pre-5.0 unbounded-by-default behavior. They are purely
opt-in; setting no other option in this guide changes your admission behavior at all.

---

## New: outbound private-network destination blocking (opt-in)

`ProxyServer.BlockPrivateNetworkDestinations` is new and **defaults to `false`**. When enabled, the
proxy refuses to open an outbound connection to a private, link-local, loopback, or multicast address
resolved for a proxied request (unless an explicit upstream proxy is configured for that request).
This is intended for proxies exposed to less-trusted clients, to prevent them from using the proxy as
an SSRF pivot into your internal network. It is `false` by default and `true` under the
`ProxyProfile.PublicFacing` profile.

---

## Policy profiles and observe/enforce modes (additive)

None of this is required reading to upgrade — it's new, additive surface for tuning several of the
items above without code changes to individual limits:

- `ProxyServer.Profile` (`ProxyProfile.Balanced` (default), `LegacyCompatible`, `PublicFacing`) applies
  a bundle of the settings above atomically. **`Balanced` reproduces the 5.0 defaults described in this
  guide** — selecting no profile at all is equivalent to `Balanced`.
- `ProxyServer.PolicyModes` lets you drop an individual policy family (`BodyBudget`,
  `DecompressionRatio`, `HeaderLimits`, `AdmissionControl`, `Http2AbuseBudget`) to `PolicyMode.Observe`
  (record a metric on breach, but don't reject/close) or `PolicyMode.Disabled` (don't even check) —
  except framing validation, which has no dial and is covered by `AllowAmbiguousFraming` instead (see
  [above](#ambiguous-http1-framing-is-now-rejected)).
- Typed metrics are published on an `System.Diagnostics.Metrics.Meter` named `ProxyMetrics.MeterName`,
  so any OpenTelemetry-compatible exporter can observe policy breaches, connection admission/rejection,
  timeouts, pool outcomes, parser errors, auth rounds, and in-memory certificate cache occupancy
  (`twp.certificates.cached`) without code changes.

## New: Happy Eyeballs (RFC 8305) address racing

**Before:** `TcpConnectionFactory` tried each of a hostname's resolved addresses fully sequentially,
each getting the full connect timeout before falling back to the next. A dual-stack host with a
broken address family (a common real-world failure — e.g. IPv6 blackholed by network policy) paid
that entire timeout on every single connection to that host, not just the first.

**Now:** resolved addresses are interleaved by address family (RFC 8305 §4, so a broken family cannot
starve the healthy one behind several same-family attempts) and raced with a 250ms staggered start
per address (RFC 8305's Connection Attempt Delay) rather than tried one at a time. The first address
to complete a TCP (or SOCKS) connect wins; every other in-flight attempt is cancelled and its socket
disposed. This is a pure latency improvement with no configuration surface and no behavior visible
to callers beyond faster connects on affected networks — nothing to change in your code.

## Connection IDs are monotonic `long` counters, not `Guid`

**Before:** `SessionEventArgsBase.ClientConnectionId`, `SessionEventArgsBase.ServerConnectionId`, and
`HttpRequestTiming.UpstreamConnectionId` were `Guid` / `Guid?`, allocated with `Guid.NewGuid()` once
per transport connection. Unbound upstream identity was `Guid.Empty`.

**Now:** those properties are `long` / `long?`. Values are process-wide monotonic counters starting at
`1` (wrapping back to `1` after `long.MaxValue`). Unbound upstream identity is `0` (and
`UpstreamConnectionId` remains `null` until a connection is acquired). Multiplexed HTTP/2 and HTTP/3
streams that share one client or origin connection still expose the same value.

**Why:** connection identity is only used for in-process correlation (equality, UI, timing). A counter
is cheaper than a UUID, produces readable IDs for logs/UI, and avoids drawing OS entropy on every
accept/connect.

**Remedy:** change stored field types from `Guid` to `long`, and replace checks like
`serverConnectionId != Guid.Empty` with `serverConnectionId != 0`. IDs are unique for the lifetime of
the process only — do not persist them across restarts expecting global uniqueness.

## Internal-only changes (no action needed)

The following were removed or replaced as part of this hardening pass but were never part of the
public API surface, so no consumer code can reference them and no action is needed:

- The internal `ProxyTypes.Https` enum value and the dead synchronous SOCKS/HTTPS handler code paths
  behind it (`internal` types only).
- The internal `ProxyTimeoutScope` type, replaced by the internal `DeadlineRegistry` mechanism.
- `HeaderCollection.GetEnumerator()` now returns a concrete `HeaderCollection.Enumerator` struct
  instead of `IEnumerator<HttpHeader>`, matching the pattern `List<T>`/`Dictionary<TKey,TValue>` use
  in the BCL to make `foreach` over a `HeaderCollection`-typed variable allocation-free. This is
  source-compatible for the overwhelming majority of callers (`foreach` loops, and code that assigns
  the result to an `IEnumerator<HttpHeader>`-typed variable, both keep compiling unchanged); only code
  that reflects on the exact return type of `GetEnumerator()` itself would need updating, which no
  known consumer does.
