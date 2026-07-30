# Security considerations

The 5.0 hardening pass (see the [migration guide](Migration-4.x-to-5.0) for the full list of behavior
changes) closes a lot of gaps, but none of it should be read as a blanket guarantee. This page calls
out where a protection is **conditional** — it only applies under a specific configuration or code
path — so you don't assume coverage you don't actually have.

## WebSocket frame validation only applies where the proxy decodes frames

The reserved-opcode rejection, pre-buffer frame-size validation, and RFC 6455-conformant close
handling described in [Protocol Support](Protocol-Support#websocket-safety) only run when the proxy
is actually parsing WebSocket frames — i.e. the connection was decrypted (`decryptSsl: true`, or plain
HTTP) and the WebSocket upgrade was intercepted by `WebSocketInterceptRelay`. Two paths bypass all of
this by construction, not by omission:

- **`decryptSsl: false`** on the endpoint/tunnel handling that connection: the TLS bytes are relayed
  opaquely end-to-end, so the proxy cannot see — let alone validate — anything inside them, WebSocket
  or otherwise.
- **Non-HTTP relay** on a `SocksProxyEndPoint`: traffic that doesn't look like HTTP at the start of the
  connection is relayed transparently to the SOCKS-negotiated destination with no HTTP or WebSocket
  parsing at all (see [SOCKS endpoint](Home#socks-endpoint)).

If you need the WebSocket protections, make sure the traffic in question is actually being decrypted
and intercepted, not relayed opaquely.

## Body-size budgets protect specific mechanisms, not "the body" in general

`MaxBufferedBodyBytes` (and its HTTP/2/HTTP/3 equivalents) now cumulatively bounds every
*whole-body-buffering* code path — see
[Cumulative body budgets](Migration-4.x-to-5.0#cumulative-body-budgets-are-now-enforced-end-to-end).
It does **not** bound:

- **The streaming hooks** (`OnRequestBodyWrite`/`OnResponseBodyWrite`, see
  [Streaming Bodies](Streaming-Bodies)) — by design, these process a body in bounded-size pieces with
  no cap on total length, so an endless stream (e.g. server-sent events) can run indefinitely. Memory
  stays flat *inside the proxy* regardless, but only because it never accumulates the body itself — if
  your own handler copies every piece it sees into a growing buffer, that accumulation is on you, not
  the proxy.
- **A custom `RespondStreaming` producer** you supply — the proxy imposes no ceiling on how much you
  choose to write.
- **HTTP/1 aggregate header-block bytes** — `PolicyFamily.HeaderLimits` exists as a named policy
  family (see `PolicyFamily.cs`) but is not yet wired to numeric enforcement at every header-reading
  call site; only the HTTP/2 decoded-header-list cap (`MaxDecodedHeaderListBytes`) and the client
  header *read deadline* (a time bound, not a byte bound — see
  [New: client header read deadline](Migration-4.x-to-5.0#new-client-header-read-deadline)) are
  actually enforced today.

Treat memory-exhaustion protection as scoped to the specific mechanism you're using, and prefer the
streaming APIs for anything whose size you can't bound in advance.

## HTTP/3 and QPACK hardening is inert until you opt in

Every HTTP/3-specific fix from this hardening pass — background-task ownership, the critical-stream
and missing-SETTINGS enforcement, the QPACK immediate-failure behavior instead of blocking, and the
HTTP/3 body budgets — only has any effect if `ProxyServer.EnableHttp3 = true`. The QPACK
dynamic-table-specific behavior further requires `ProxyServer.EnableQpackDynamicTable = true` on top of
that. Neither is on by default, and both remain marked `[Experimental("TWP001")]`: if you do opt in,
treat the HTTP/3 path as less battle-tested in production than the HTTP/1.x/HTTP/2 path, independent
of how thoroughly it's been hardened here.

## The root CA private key is protected from other users, not other processes as you

Moving the certificate store to a per-user folder and no longer passing the real PFX password on the
`certutil.exe` command line (see
[Certificate store relocated](Migration-4.x-to-5.0#certificate-store-relocated-to-a-per-user-protected-folder))
raises the bar from *"any local user or process on the machine can read the CA private key"* to *"any
process running as the same OS user account can."* That second bar is not, and cannot be, eliminated
by this proxy: a MITM TLS proxy must hold a CA private key somewhere accessible to itself at runtime in
order to sign leaf certificates, and any other code running as your account has the same filesystem
access you do. If your threat model includes other processes running as your own account (e.g. a
shared, multi-tenant build/CI user), protect the key with OS-level mechanisms outside the proxy's
control — a hardware-backed key store, a dedicated service account, or equivalent — rather than
expecting the file relocation alone to isolate it.

## See also

- [Migration guide: 4.x → 5.0](Migration-4.x-to-5.0) — the full list of behavior changes this release
  introduces.
- [Protocol Feature Support](Protocol-Support) — full Yes/No/Partial breakdown per protocol.
